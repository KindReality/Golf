using System.Diagnostics;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;
using PhotoBooth.Diagnostics;

namespace Controller.Scheduling;

internal sealed record DispatchReceipt(long SentAt);
internal interface ICommandDispatcher
{
    Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token);
}
internal sealed record PlaylistRun(string Id, Task Completion);
internal sealed record RunningPlaylist(string Id, string Playlist, string? Trigger, double ElapsedMilliseconds, int RemainingItems);

// Owns execution and item cooldowns. Scenes own lifetimes; the engine never waits for remote playback.
internal sealed class PlaylistEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ICommandDispatcher _dispatcher;
    private readonly ActivityLog _log;
    private readonly DeadlineScheduler _scheduler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, RunState> _runs = new();
    private readonly Dictionary<string, ItemState> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TriggerState> _triggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimerState> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _retiredTimers = [];
    private readonly RequestDeduplicator _branches = new();
    private ControllerOptions _options;
    private SceneVideoLoops? _sceneLoops;
    internal void SetSceneLoops(SceneVideoLoops loops) => Volatile.Write(ref _sceneLoops, loops);
    internal Task StopSceneLoopsAsync(SceneVideoLoops loops) => loops.StopAsync(_dispatcher, _log, _shutdown.Token);
    private bool _started, _stopping;
    internal Func<int, int, int> ChooseInterval { get; init; } = Random.Shared.Next;
    internal Func<int, int> ChooseRole { get; init; } = Random.Shared.Next;

    public PlaylistEngine(ControllerOptions options, ICommandDispatcher dispatcher, ActivityLog log)
    { _options = options; _dispatcher = dispatcher; _log = log; _scheduler = new(log); }
    public void Start() { lock (_gate) { _started = true; ApplyTimers(); } }
    public void Apply(ControllerOptions options) { lock (_gate) { _options = options; if (_started) ApplyTimers(); } }
    public PlaylistRun Run(string name, ControllerOptions? snapshot = null, BroadcastChain? chain = null, string? owner = null, CancellationToken token = default, SceneVideoLoops? loops = null, bool isAmbient = false, string? roles = null, bool includeSubwoofer = false)
    {
        var options = snapshot ?? Volatile.Read(ref _options);
        var playlist = options.Playlist(name, "storyboard");
        var ancestry = (chain ?? BroadcastChain.Root(options.EffectiveDeviceName)).Enter($"{options.EffectiveDeviceName}/playlist:{playlist.Name}");
        return StartRun(playlist, options, ancestry, owner, token,
            async state =>
            {
                foreach (var item in playlist.Items.Where(i => i.Enabled))
                {
                    await _scheduler.UntilAsync(DeadlineScheduler.After(item.DelayMilliseconds), state.Cancellation.Token).ConfigureAwait(false);
                    await ExecuteItemAsync(playlist, item, options, ancestry, state).ConfigureAwait(false);
                    Interlocked.Decrement(ref state.Remaining);
                }
            }, loops: loops, isAmbient: isAmbient, roles: roles, includeSubwoofer: includeSubwoofer);
    }
    public string RunManually(string name) => Run(name, owner: "Manual").Id;
    internal PlaylistRun RunAmbientItem(PlaylistOptions playlist, PlaylistItem item, ControllerOptions options, string scene, CancellationToken token, SceneVideoLoops? loops = null)
    {
        var chain = BroadcastChain.Root(options.EffectiveDeviceName).Enter($"{options.EffectiveDeviceName}/playlist:{playlist.Name}");
        return StartRun(playlist, options, chain, $"Scene: {scene}", token, state => ExecuteItemAsync(playlist, item, options, chain, state), 1, loops, isAmbient: true);
    }
    private PlaylistRun StartRun(PlaylistOptions playlist, ControllerOptions options, BroadcastChain chain, string? owner, CancellationToken token, Func<RunState, Task> execute, int? count = null, SceneVideoLoops? loops = null, bool isAmbient = false, string? roles = null, bool includeSubwoofer = false)
    {
        RunState state;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            token.ThrowIfCancellationRequested();
            if (_runs.Count >= 256) throw new InvalidOperationException("At most 256 playlist runs may be active.");
            state = new(Guid.NewGuid().ToString("N"), playlist.Name, owner, count ?? playlist.Items.Count(i => i.Enabled), CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token));
            state.Loops = loops ?? Volatile.Read(ref _sceneLoops);
            state.IsAmbient = isAmbient;
            state.Roles = roles;
            state.IncludeSubwoofer = includeSubwoofer;
            _runs.Add(state.Id, state);
            _scheduler.Post(() => _ = ExecuteRunAsync(state, execute, chain));
        }
        return new(state.Id, state.Completion.Task);
    }
    private async Task ExecuteRunAsync(RunState state, Func<RunState, Task> execute, BroadcastChain chain)
    {
        _log.Info("PlaylistStarted", $"Started {state.Name}", new { state.Id, Playlist = state.Name, state.Owner, state.IsAmbient, chain.CorrelationId });
        try { await execute(state).ConfigureAwait(false); _log.Info("PlaylistFinished", $"Finished {state.Name}", new { state.Id }); }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested) { _log.Info("PlaylistCancelled", $"Cancelled {state.Name}", new { state.Id }); }
        catch (Exception ex) { _log.Error("PlaylistFailed", ex, new { state.Id, Playlist = state.Name }); }
        finally
        {
            lock (_gate) { _runs.Remove(state.Id); state.Cancellation.Dispose(); }
            state.Completion.TrySetResult();
        }
    }
    private ItemState State(ControllerOptions options, PlaylistOptions playlist, PlaylistItem item)
    {
        var key = $"{options.EffectiveDeviceName}/{playlist.Name}/{item.Name}";
        lock (_gate) { if (!_items.TryGetValue(key, out var state)) _items.Add(key, state = new()); return state; }
    }
    internal int AmbientInterval(ControllerOptions options, PlaylistOptions playlist, PlaylistItem item, bool justCompleted = false)
    {
        var state = State(options, playlist, item);
        var remaining = justCompleted ? item.CooldownMilliseconds : RemainingCooldown(state, item.CooldownMilliseconds);
        var minimum = Math.Max(item.MinIntervalMilliseconds!.Value, (int)Math.Ceiling(remaining));
        // Completed items use the full cooldown; re-entering a scene uses only the remaining portion.
        return ChooseInterval(Math.Min(minimum, item.MaxIntervalMilliseconds!.Value), item.MaxIntervalMilliseconds.Value + 1);
    }
    private static double RemainingCooldown(ItemState state, int milliseconds)
    { var at = Interlocked.Read(ref state.LastCompleted); return at == 0 ? 0 : Math.Max(0, milliseconds - Stopwatch.GetElapsedTime(at).TotalMilliseconds); }
    private async Task ExecuteItemAsync(PlaylistOptions playlist, PlaylistItem item, ControllerOptions options, BroadcastChain chain, RunState run)
    {
        var token = run.Cancellation.Token;
        var state = State(options, playlist, item);
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        bool attempted = false;
        try
        {
            var cooldown = RemainingCooldown(state, item.CooldownMilliseconds);
            if (cooldown > 0) await _scheduler.UntilAsync(Stopwatch.GetTimestamp() + DeadlineScheduler.Ticks(cooldown), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var roles = PlaylistRoleTarget.Select(item, run.Roles, ChooseRole);
            var includeSubwoofer = run.IncludeSubwoofer || item.IncludeSubwoofer;
            if (RoleList.Parse(roles).Length > 0)
                _log.Info("PlaylistRoleTargetSelected", $"{playlist.Name}/{item.Name} → roles {roles}", new
                { Playlist = playlist.Name, Item = item.Name, run.Id, run.IsAmbient, item.BroadcastMode, ConfiguredRoles = item.Roles, SelectedRoles = roles, IncludeSubwoofer = includeSubwoofer, chain.CorrelationId });
            foreach (var command in item.Commands)
            {
                token.ThrowIfCancellationRequested(); attempted = true;
                var id = Guid.NewGuid().ToString("N");
                try
                {
                    if (command.Playlist is { } nested)
                        await Run(nested, options, chain, run.Owner, token, run.Loops, run.IsAmbient, roles, includeSubwoofer).Completion.ConfigureAwait(false);
                    else
                    {
                        var outgoing = PlaylistRoleTarget.Apply(command, roles, includeSubwoofer) with { Context = new() { Scene = run.Loops?.SceneName, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient } };
                        Telemetry.SetRequestContext(id, new()
                        {
                            Source = options.EffectiveDeviceName, Device = outgoing.Device, Roles = ControllerOptions.Text(outgoing.Payload, "roles"),
                            Scene = outgoing.Context.Scene, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient,
                            Experience = ControllerOptions.Text(command.Payload, "experience"), Value = ControllerOptions.Text(command.Payload, "value"),
                            CorrelationId = chain.CorrelationId
                        });
                        var due = Stopwatch.GetTimestamp();
                        TaskCompletionSource? sending = null;
                        if (SceneVideoLoops.IsLoop(command))
                            sending = (run.Loops ?? throw new InvalidOperationException("A looping video requires an active scene.")).Register(outgoing, options, id);
                        DispatchReceipt receipt;
                        try { receipt = await _dispatcher.SendAsync(outgoing, options, chain, id, token).ConfigureAwait(false); }
                        finally { sending?.TrySetResult(); }
                        var value = ControllerOptions.Text(command.Payload, "value");
                        _log.Info("CommandSent", $"{playlist.Name}/{item.Name} → {outgoing.Device}" + (value is null ? "" : $" ({value})"), new { Playlist = playlist.Name, Item = item.Name, run.IsAmbient, command.Transport, command.Type, outgoing.Device, Roles = ControllerOptions.Text(outgoing.Payload, "roles"), Value = value, chain.CorrelationId, SendLatencyMilliseconds = receipt.SentAt > 0 ? Stopwatch.GetElapsedTime(due, receipt.SentAt).TotalMilliseconds : (double?)null }, id);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.Error("CommandFailed", ex, new { Playlist = playlist.Name, Item = item.Name, command.Device }, id); }
            }
        }
        finally { if (attempted) Interlocked.Exchange(ref state.LastCompleted, Stopwatch.GetTimestamp()); state.Gate.Release(); }
    }
    public RunningPlaylist[] Snapshot()
    {
        lock (_gate) return _runs.Values.Select(r => new RunningPlaylist(r.Id, r.Name, r.Owner, Stopwatch.GetElapsedTime(r.Started).TotalMilliseconds, Volatile.Read(ref r.Remaining))).ToArray();
    }
    public void Cancel(string id) { lock (_gate) { if (_runs.TryGetValue(id, out var run)) run.Cancellation.Cancel(); } }
    public void Receive(BroadcastEnvelope message)
    {
        var options = Volatile.Read(ref _options);
        foreach (var trigger in options.Triggers.Where(t => t.Enabled && t.IntervalMilliseconds is null && ControllerOptions.Same(t.SourceDevice, message.Source) && ControllerOptions.Same(t.Input, ControllerOptions.Text(message.Body, "input")) && ControllerOptions.Same(t.Event, message.Event)))
            Fire(trigger, options, BroadcastChain.From(message));
    }
    private void Fire(TriggerOptions trigger, ControllerOptions options, BroadcastChain chain)
    {
        var identity = $"{options.EffectiveDeviceName}/trigger:{trigger.Name}";
        lock (_gate)
        {
            if (_stopping) return;
            if (!_triggers.TryGetValue(identity, out var state)) _triggers.Add(identity, state = new());
            if (state.Busy || state.Completed != 0 && Stopwatch.GetElapsedTime(state.Completed).TotalMilliseconds < (trigger.CooldownMilliseconds ?? options.Build.DefaultTriggerCooldownMilliseconds)) return;
            try
            {
                var entered = chain.Enter(identity);
                if (!_branches.Accept(identity, chain.CorrelationId, options.RequestDeduplicationSeconds)) return;
                state.Busy = true;
                var run = Run(trigger.Playlist, options, entered, trigger.Name);
                _ = CompleteTriggerAsync(run.Completion, state);
            }
            catch (Exception ex) { state.Busy = false; _log.Warning("TriggerSuppressed", ex.Message, new { trigger.Name, chain.CorrelationId }); }
        }
    }
    private async Task CompleteTriggerAsync(Task completion, TriggerState state)
    { await completion.ConfigureAwait(false); lock (_gate) { state.Completed = Stopwatch.GetTimestamp(); state.Busy = false; } }
    private void ApplyTimers()
    {
        var wanted = _options.Triggers.Where(t => t.Enabled && t.IntervalMilliseconds is not null).ToArray();
        foreach (var pair in _timers.ToArray())
            if (!wanted.Any(t => ControllerOptions.Same(t.Name, pair.Key) && t.IntervalMilliseconds == pair.Value.Interval))
            { _timers.Remove(pair.Key); pair.Value.Cancellation.Cancel(); _retiredTimers.Add(pair.Value.Completion); }
        _retiredTimers.RemoveWhere(t => t.IsCompleted);
        foreach (var trigger in wanted)
        {
            if (_timers.ContainsKey(trigger.Name)) continue;
            var timer = new TimerState(trigger.IntervalMilliseconds!.Value, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            _timers.Add(trigger.Name, timer); timer.Completion = TimerAsync(trigger.Name, timer);
        }
    }
    private async Task TimerAsync(string name, TimerState timer)
    {
        var next = DeadlineScheduler.After(timer.Interval); var stride = DeadlineScheduler.Ticks(timer.Interval);
        try
        {
            while (true)
            {
                await _scheduler.UntilAsync(next, timer.Cancellation.Token).ConfigureAwait(false);
                var options = Volatile.Read(ref _options);
                var trigger = options.Triggers.FirstOrDefault(t => ControllerOptions.Same(t.Name, name) && t.Enabled && t.IntervalMilliseconds == timer.Interval);
                if (trigger is not null) Fire(trigger, options, BroadcastChain.Root(options.EffectiveDeviceName));
                next += stride;
                var now = Stopwatch.GetTimestamp(); if (next <= now) next += ((now - next) / stride + 1) * stride;
            }
        }
        catch (OperationCanceledException) when (timer.Cancellation.IsCancellationRequested) { }
        finally { timer.Cancellation.Dispose(); }
    }
    internal Task WaitUntilAsync(long at, CancellationToken token) => _scheduler.UntilAsync(at, token);
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_stopping) return; _stopping = true; _shutdown.Cancel();
            pending = _runs.Values.Select(r => r.Completion.Task).Concat(_timers.Values.Select(t => t.Completion)).Concat(_retiredTimers).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false); _scheduler.Dispose(); _shutdown.Dispose();
        foreach (var state in _items.Values) state.Gate.Dispose();
    }
    private sealed class RunState(string id, string name, string? owner, int remaining, CancellationTokenSource cancellation)
    {
        public string Id = id, Name = name; public string? Owner = owner; public int Remaining = remaining;
        public SceneVideoLoops? Loops;
        public bool IsAmbient;
        public bool IncludeSubwoofer;
        public string? Roles;
        public long Started = Stopwatch.GetTimestamp(); public CancellationTokenSource Cancellation = cancellation;
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ItemState { public readonly SemaphoreSlim Gate = new(1, 1); public long LastCompleted; }
    private sealed class TriggerState { public bool Busy; public long Completed; }
    private sealed class TimerState(int interval, CancellationTokenSource cancellation)
    { public int Interval = interval; public CancellationTokenSource Cancellation = cancellation; public Task Completion = Task.CompletedTask; }
}
