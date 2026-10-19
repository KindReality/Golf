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

// Owns dispatch, item cooldowns and estimated media end times; no remote playback acknowledgments.
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
    internal Func<Controller.Networking.ExperiencePeer[]> ReadExperiences { get; init; } = () => [];
    internal Func<int, int> ChooseAudioOutput { get; init; } = Random.Shared.Next;
    private ControllerOptions _options;
    private SceneVideoLoops? _sceneLoops;
    private readonly List<SceneVideoLoops> _triggerLoops = [];
    internal event Action? TriggeredPlaylistStarted;
    internal event Action? TriggeredPlaylistFinished;
    internal Func<string, double?> ReadMediaDurationMilliseconds { get; init; } = MediaDurations.ReadMilliseconds;
    internal void SetSceneLoops(SceneVideoLoops loops) => Volatile.Write(ref _sceneLoops, loops);
    internal async Task StopSceneLoopsAsync(SceneVideoLoops loops)
    {
        SceneVideoLoops[] triggered;
        lock (_gate) { triggered = _triggerLoops.Where(l => ControllerOptions.Same(l.SceneName, loops.SceneName)).ToArray(); foreach (var item in triggered) _triggerLoops.Remove(item); }
        await loops.StopAsync(_dispatcher, _log, _shutdown.Token).ConfigureAwait(false);
        foreach (var item in triggered) await item.StopAsync(_dispatcher, _log, _shutdown.Token).ConfigureAwait(false);
    }
    private bool _started, _stopping;
    internal Func<int, int, int> ChooseInterval { get; init; } = Random.Shared.Next;
    internal Func<int, int> ChooseRole { get; init; } = Random.Shared.Next;

    public PlaylistEngine(ControllerOptions options, ICommandDispatcher dispatcher, ActivityLog log)
    { _options = options; _dispatcher = dispatcher; _log = log; _scheduler = new(log); }
    public void Start() { lock (_gate) { _started = true; ApplyTimers(); } }
    public void Apply(ControllerOptions options) { lock (_gate) { _options = options; if (_started) ApplyTimers(); } }
    public PlaylistRun Run(string name, ControllerOptions? snapshot = null, BroadcastChain? chain = null, string? owner = null, CancellationToken token = default, SceneVideoLoops? loops = null, bool isAmbient = false, string? roles = null, bool includeSubwoofer = false, CommandPriority priority = CommandPriority.Medium, bool nested = false)
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
                if (state.Triggered && state.Loops?.Count > 0 && state.MediaEnds.Count == 0 && playlist.DurationMilliseconds is null)
                    throw new InvalidOperationException("A triggered playlist containing only looping media needs an explicit duration.");
                var durationTasks = await Task.WhenAll(state.MediaEnds).WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
                var end = Math.Max(state.Started + DeadlineScheduler.Ticks(playlist.DurationMilliseconds ?? 0), durationTasks.DefaultIfEmpty(state.Started).Max());
                _log.Info("PlaylistDurationResolved", $"{playlist.Name}: waiting for its duration and non-looping media", new { Playlist = playlist.Name, state.Id, playlist.DurationMilliseconds, AutomaticDurationMilliseconds = Stopwatch.GetElapsedTime(state.Started, end).TotalMilliseconds, MediaEndTimesEstimated = true });
                await _scheduler.UntilAsync(end, state.Cancellation.Token).ConfigureAwait(false);
            }, loops: loops, isAmbient: isAmbient, roles: roles, includeSubwoofer: includeSubwoofer, priority: priority,
                triggered: !nested && !isAmbient && priority != CommandPriority.Low);
    }
    public string RunManually(string name) => Run(name, owner: "Manual").Id;
    internal PlaylistRun RunAmbientItem(PlaylistOptions playlist, PlaylistItem item, ControllerOptions options, string scene, CancellationToken token, SceneVideoLoops? loops = null)
    {
        var chain = BroadcastChain.Root(options.EffectiveDeviceName).Enter($"{options.EffectiveDeviceName}/playlist:{playlist.Name}");
        return StartRun(playlist, options, chain, $"Scene: {scene}", token, state => ExecuteItemAsync(playlist, item, options, chain, state), 1, loops, isAmbient: true, priority: CommandPriority.Low);
    }
    private PlaylistRun StartRun(PlaylistOptions playlist, ControllerOptions options, BroadcastChain chain, string? owner, CancellationToken token, Func<RunState, Task> execute, int? count = null, SceneVideoLoops? loops = null, bool isAmbient = false, string? roles = null, bool includeSubwoofer = false, CommandPriority priority = CommandPriority.Medium, bool triggered = false)
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
            state.Priority = priority;
            state.Triggered = triggered;
            if (triggered)
            {
                var scene = playlist.Items.Where(i => i.Enabled).SelectMany(i => i.Commands).FirstOrDefault(c => ControllerOptions.Text(c.Payload, "command") == "scene");
                state.Loops = new SceneVideoLoops(ControllerOptions.Text(scene?.Payload ?? default, "value") ?? state.Loops?.SceneName ?? options.Scenes[0].Name);
                _triggerLoops.Add(state.Loops);
            }
            state.Roles = roles;
            state.IncludeSubwoofer = includeSubwoofer;
            _runs.Add(state.Id, state);
        }
        if (triggered) TriggeredPlaylistStarted?.Invoke();
        _scheduler.Post(() => _ = ExecuteRunAsync(state, execute, chain));
        return new(state.Id, state.Completion.Task);
    }
    private async Task ExecuteRunAsync(RunState state, Func<RunState, Task> execute, BroadcastChain chain)
    {
        _log.Info("PlaylistStarted", $"Playlist started: {state.Name}", new { state.Id, Playlist = state.Name, state.Owner, state.IsAmbient, chain.CorrelationId });
        try { await execute(state).ConfigureAwait(false); _log.Info("PlaylistFinished", $"Playlist finished: {state.Name}", new { state.Id, Playlist = state.Name }); }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested) { _log.Info("PlaylistCancelled", $"Playlist cancelled: {state.Name}", new { state.Id, Playlist = state.Name }); }
        catch (Exception ex) { _log.Error("PlaylistFailed", ex, new { state.Id, Playlist = state.Name }); }
        finally
        {
            lock (_gate) { _runs.Remove(state.Id); if (state.Triggered && state.Loops?.Count == 0) _triggerLoops.Remove(state.Loops); state.Cancellation.Dispose(); }
            if (state.Triggered) TriggeredPlaylistFinished?.Invoke();
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
            if (RoleList.ParseTargets(roles).Length > 0)
                _log.Info("PlaylistRoleTargetSelected", $"{playlist.Name}/{item.Name} → roles {roles}", new
                { Playlist = playlist.Name, Item = item.Name, run.Id, run.IsAmbient, item.BroadcastMode, ConfiguredRoles = item.Roles, SelectedRoles = roles, IncludeSubwoofer = includeSubwoofer, chain.CorrelationId });
            foreach (var command in item.Commands)
            {
                token.ThrowIfCancellationRequested(); attempted = true;
                var id = Guid.NewGuid().ToString("N");
                try
                {
                    if (command.Playlist is { } nested)
                        await Run(nested, options, chain, run.Owner, token, run.Loops, run.IsAmbient, roles, includeSubwoofer, run.Priority, nested: true).Completion.ConfigureAwait(false);
                    else
                    {
                        var routed = PlaylistRoleTarget.Apply(command, roles, includeSubwoofer);
                        var requestedRoles = ControllerOptions.Text(routed.Payload, "roles");
                        if (ControllerOptions.Same(playlist.AudioOutputMode, "Random") && PlaylistAudioTarget.IsAudio(routed))
                        {
                            var target = PlaylistAudioTarget.Random(routed, ReadExperiences(), ChooseAudioOutput);
                            routed = target.Command;
                            _log.Info("PlaylistAudioOutputSelected", $"{playlist.Name}/{item.Name} → {target.Hostname}: {target.SpeakerName}",
                                new { Playlist = playlist.Name, Item = item.Name, Roles = requestedRoles, target.Hostname, target.SpeakerName, AudioDevice = ControllerOptions.Text(routed.Payload, "audioDevice"), routed.Device, Value = ControllerOptions.Text(routed.Payload, "value") }, id);
                        }
                        var outgoing = routed with { Priority = SceneVideoLoops.IsLoop(command) ? CommandPriority.Low : run.Priority,
                            Context = new() { Scene = run.Loops?.SceneName, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient } };
                        Telemetry.SetRequestContext(id, new()
                        {
                            Source = options.EffectiveDeviceName, Device = outgoing.Device, Roles = requestedRoles,
                            Scene = outgoing.Context.Scene, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient,
                            Experience = ControllerOptions.Text(command.Payload, "experience"), Value = ControllerOptions.Text(command.Payload, "value"),
                            CorrelationId = chain.CorrelationId, Priority = outgoing.Priority.ToString().ToLowerInvariant()
                        });
                        var due = Stopwatch.GetTimestamp();
                        TaskCompletionSource? sending = null;
                        if (SceneVideoLoops.IsLoop(command))
                            sending = (run.Loops ?? throw new InvalidOperationException("A looping video requires an active scene.")).Register(outgoing, options, id);
                        DispatchReceipt receipt;
                        try { receipt = await _dispatcher.SendAsync(outgoing, options, chain, id, token).ConfigureAwait(false); }
                        finally { sending?.TrySetResult(); }
                        if (!run.IsAmbient && !SceneVideoLoops.IsLoop(command) && ControllerOptions.Text(command.Payload, "command") != "stop" &&
                            ControllerOptions.Text(command.Payload, "experience") is "video" or "audio" && ControllerOptions.Text(command.Payload, "value") is { } media)
                        {
                            var sent = Stopwatch.GetTimestamp();
                            run.MediaEnds.Add(EstimateEndAsync());
                            async Task<long> EstimateEndAsync()
                            {
                                try
                                {
                                    var duration = await MediaDurations.ReadAsync(media, ReadMediaDurationMilliseconds, token).ConfigureAwait(false);
                                    if (duration is > 0 && double.IsFinite(duration.Value)) return sent + DeadlineScheduler.Ticks(duration.Value);
                                    _log.Warning("PlaylistMediaDurationUnavailable", "Set a playlist duration when local media metadata is unavailable", new { Playlist = playlist.Name, Item = item.Name, Media = media });
                                }
                                catch (Exception ex) { _log.Warning("PlaylistMediaDurationUnavailable", ex.Message, new { Playlist = playlist.Name, Media = media }); }
                                return sent;
                            }
                        }
                        var value = ControllerOptions.Text(command.Payload, "value");
                        _log.Info("CommandSent", $"{playlist.Name}/{item.Name} → {outgoing.Device}" + (value is null ? "" : $" ({value})"), new { Playlist = playlist.Name, Item = item.Name, run.IsAmbient, outgoing.Priority, command.Transport, command.Type, outgoing.Device, Roles = ControllerOptions.Text(outgoing.Payload, "roles"), Value = value, chain.CorrelationId, SendLatencyMilliseconds = receipt.SentAt > 0 ? Stopwatch.GetElapsedTime(due, receipt.SentAt).TotalMilliseconds : (double?)null }, id);
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
        lock (_gate) return _runs.Values.Where(r => !r.Cancellation.IsCancellationRequested).Select(r => new RunningPlaylist(r.Id, r.Name, r.Owner, Stopwatch.GetElapsedTime(r.Started).TotalMilliseconds, Volatile.Read(ref r.Remaining))).ToArray();
    }
    public void Cancel(string id) { lock (_gate) { if (_runs.TryGetValue(id, out var run)) run.Cancellation.Cancel(); } }
    public void CancelAll()
    {
        lock (_gate)
        {
            Volatile.Read(ref _sceneLoops)?.Abandon();
            foreach (var loops in _triggerLoops) loops.Abandon();
            _triggerLoops.Clear();
            // Cancellation can complete a nested run synchronously and remove it.
            foreach (var run in _runs.Values.ToArray())
            {
                run.Loops?.Abandon();
                if (_runs.ContainsKey(run.Id)) run.Cancellation.Cancel();
            }
        }
    }
    public void Receive(BroadcastEnvelope message)
    {
        var options = Volatile.Read(ref _options);
        foreach (var trigger in options.Triggers.Where(t => t.Enabled && t.IntervalMilliseconds is null && ControllerOptions.Same(t.SourceDevice, message.Source) && ControllerOptions.Same(t.Input, ControllerOptions.Text(message.Body, "input")) && ControllerOptions.Same(t.Event, message.Event)))
            Fire(trigger, options, BroadcastChain.From(message));
    }
    private void Fire(TriggerOptions trigger, ControllerOptions options, BroadcastChain chain)
    {
        var identity = $"{options.EffectiveDeviceName}/trigger:{trigger.Name}";
        TriggerState state;
        BroadcastChain entered;
        lock (_gate)
        {
            if (_stopping) return;
            if (!_triggers.TryGetValue(identity, out state!)) _triggers.Add(identity, state = new());
            if (state.Busy || state.Completed != 0 && Stopwatch.GetElapsedTime(state.Completed).TotalMilliseconds < (trigger.CooldownMilliseconds ?? options.Build.DefaultTriggerCooldownMilliseconds)) return;
            try
            {
                entered = chain.Enter(identity);
                if (!_branches.Accept(identity, chain.CorrelationId, options.RequestDeduplicationSeconds)) return;
                state.Busy = true;
            }
            catch (Exception ex) { state.Busy = false; _log.Warning("TriggerSuppressed", ex.Message, new { trigger.Name, chain.CorrelationId }); return; }
        }
        // Run notifications can enter SceneManager; never hold the engine gate across them.
        try { var run = Run(trigger.Playlist, options, entered, trigger.Name); _ = CompleteTriggerAsync(run.Completion, state); }
        catch (Exception ex) { lock (_gate) state.Busy = false; _log.Warning("TriggerSuppressed", ex.Message, new { trigger.Name, chain.CorrelationId }); }
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
        public CommandPriority Priority;
        public bool Triggered;
        public readonly List<Task<long>> MediaEnds = [];
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
