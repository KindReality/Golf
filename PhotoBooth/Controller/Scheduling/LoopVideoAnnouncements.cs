using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Scheduling;

internal sealed record LoopAnnouncementOwner(string Id, CancellationToken Lifetime);

// Reassert desired looping playback without keeping a completed playlist run alive.
internal sealed class LoopVideoAnnouncements(ICommandDispatcher dispatcher, ActivityLog log,
    Func<TimeSpan> interval, CancellationToken shutdown) : IAsyncDisposable
{
    private sealed class Entry(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string owner, bool scene,
        CancellationToken lifetime, long next) : IDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal readonly PlaylistCommand Command = command;
        internal readonly ControllerOptions Options = options;
        internal readonly BroadcastChain Chain = chain;
        internal readonly string Owner = owner;
        internal readonly bool Scene = scene;
        internal readonly CancellationToken Lifetime = lifetime;
        internal CancellationToken Cancellation => _stop.Token;
        internal long Next = next;
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    private Task? _worker;
    private SceneExecutionContext? _scene;
    private CancellationTokenSource? _sceneStop;
    private bool _disposed;
    internal int Count { get { lock (_gate) return _entries.Count; } }
    internal void BeginScene(SceneExecutionContext scene, CancellationToken lifetime)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Remove(e => e.Scene);
            _sceneStop?.Cancel(); _sceneStop?.Dispose();
            _scene = scene; _sceneStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _stop.Token);
        }
        Wake();
    }
    internal void Remember(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string run,
        bool sceneOwned, SceneExecutionContext? scene, CancellationToken runToken)
    {
        if (!VideoCommand.IsLoop(command)) return;
        lock (_gate)
        {
            if (_disposed || _stop.IsCancellationRequested || runToken.IsCancellationRequested) return;
            var lifetime = runToken;
            if (sceneOwned)
            {
                if (scene is null || scene != _scene || _sceneStop is null || _sceneStop.IsCancellationRequested) return;
                lifetime = _sceneStop.Token;
            }
            Remove(e => e.Lifetime.IsCancellationRequested);
            var key = Target(command);
            if (_entries.Remove(key, out var old)) old.Dispose();
            if (_entries.Count >= 4096) throw new InvalidOperationException("Too many looping video targets.");
            _entries[key] = new(command, options, chain, run, sceneOwned, lifetime, long.MaxValue);
            _worker ??= Task.Run(RunAsync);
        }
        Wake();
    }
    internal void InitialSendFinished(PlaylistCommand command)
    {
        lock (_gate)
            if (_entries.TryGetValue(Target(command), out var entry) && ReferenceEquals(entry.Command, command))
                entry.Next = Stopwatch.GetTimestamp() + Stride();
        Wake();
    }
    internal void EndRun(string id)
    { lock (_gate) Remove(e => !e.Scene && e.Owner == id); Wake(); }
    internal void Clear()
    {
        lock (_gate) { if (_disposed) return; Remove(_ => true); _sceneStop?.Cancel(); }
        Wake();
    }
    private void Remove(Func<Entry, bool> match)
    { foreach (var pair in _entries.Where(p => match(p.Value)).ToArray()) { _entries.Remove(pair.Key); pair.Value.Dispose(); } }
    private long Stride() => Math.Max(1, (long)(interval().TotalSeconds * Stopwatch.Frequency));
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    private static string Target(PlaylistCommand command) => JsonSerializer.Serialize(new
    {
        Device = command.Device?.ToUpperInvariant(),
        Roles = RoleList.ParseTargets(ControllerOptions.Text(command.Payload, "roles")).Select(r => r.ToUpperInvariant()).ToArray(),
        Screen = ControllerOptions.Text(command.Payload, "screen")?.ToUpperInvariant(),
        Display = ControllerOptions.Text(command.Payload, "displayName")?.ToUpperInvariant()
    });
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Entry[] due; TimeSpan delay;
                lock (_gate)
                {
                    Remove(e => e.Lifetime.IsCancellationRequested);
                    var now = Stopwatch.GetTimestamp(); due = _entries.Values.Where(e => e.Next <= now).ToArray();
                    foreach (var entry in due) entry.Next = long.MaxValue;
                    var next = _entries.Values.Select(e => e.Next).DefaultIfEmpty(long.MaxValue).Min();
                    delay = next == long.MaxValue ? Timeout.InfiniteTimeSpan :
                        TimeSpan.FromSeconds(Math.Max(0, next - now) / (double)Stopwatch.Frequency);
                }
                foreach (var entry in due)
                {
                    await SendAsync(entry).ConfigureAwait(false);
                    lock (_gate) if (_entries.Values.Contains(entry)) entry.Next = Stopwatch.GetTimestamp() + Stride();
                }
                if (due.Length == 0) await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task SendAsync(Entry entry)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || !_entries.Values.Contains(entry) || entry.Lifetime.IsCancellationRequested) return;
            token = entry.Cancellation;
        }
        var command = entry.Command; var id = Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(entry.Options.RequestLifetimeSeconds));
        Telemetry.SetRequestContext(id, new() { Source = entry.Options.EffectiveDeviceName, Device = command.Device,
            Experience = "video", Value = ControllerOptions.Text(command.Payload, "value"), Roles = ControllerOptions.Text(command.Payload, "roles"),
            Scene = command.Context?.Scene, Playlist = command.Context?.Playlist, Item = command.Context?.Item,
            IsAmbient = command.Context?.IsAmbient == true, Priority = "low", CorrelationId = entry.Chain.CorrelationId });
        Task<DispatchReceipt>? pending = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            pending = dispatcher.SendAsync(command, entry.Options, entry.Chain, id, deadline.Token);
            await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            Telemetry.Info("VideoLoopCommandReannounced", new { command.Device, command.Context, command.Payload, OwnerRun = entry.Owner, SceneOwned = entry.Scene, IntervalSeconds = interval().TotalSeconds }, id);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (pending is not null) _ = ObserveAsync(pending);
            if (!token.IsCancellationRequested && !_stop.IsCancellationRequested) log.Warning("VideoLoopAnnouncementTimedOut", "Loop announcement timed out; retrying next interval", new { command.Device, command.Context }, id);
        }
        catch (Exception ex) { log.Error("VideoLoopAnnouncementFailed", ex, new { command.Device, command.Context, RetrySeconds = interval().TotalSeconds }, id); }
    }
    private static async Task ObserveAsync(Task pending) { try { await pending.ConfigureAwait(false); } catch { } }
    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate) { if (_disposed) return; _disposed = true; _stop.Cancel(); Remove(_ => true); _sceneStop?.Cancel(); worker = _worker; }
        if (worker is not null) await worker.ConfigureAwait(false);
        _sceneStop?.Dispose(); _wake.Dispose(); _stop.Dispose();
    }
}
