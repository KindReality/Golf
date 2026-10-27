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
internal enum PlaylistOutcome { Completed, Cancelled, Failed }
internal sealed record PlaylistRun(string Id, Task Completion, Task<PlaylistOutcome> Outcome);
internal sealed record RunningPlaylist(string Id, string Playlist, string? Trigger, double ElapsedMilliseconds, int RemainingItems);

// Owns dispatch, item cooldowns and estimated media end times; no remote playback acknowledgments.
internal sealed class PlaylistEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ICommandDispatcher _dispatcher;
    private readonly ActivityLog _log;
    private readonly DeadlineScheduler _scheduler;
    private readonly LoopVideoAnnouncements _loopAnnouncements;
    internal TimeSpan LoopAnnouncementInterval { get; init; } = TimeSpan.FromSeconds(15);
    internal int LoopAnnouncementCount => _loopAnnouncements.Count;
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
    private SceneExecutionContext? _sceneContext;
    internal event Action? TriggeredPlaylistStarted;
    internal event Action? TriggeredPlaylistFinished;
    internal Func<string, double?> ReadMediaDurationMilliseconds { get; init; } = MediaDurations.ReadMilliseconds;
    internal void SetSceneContext(SceneExecutionContext context, CancellationToken lifetime = default)
    { Volatile.Write(ref _sceneContext, context); _loopAnnouncements.BeginScene(context, lifetime); }
    internal async Task SendSceneTransitionAsync(SceneOptions scene, SceneTransitionOptions transition, ControllerOptions options,
        BroadcastChain chain, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var command = new PlaylistCommand { Device = transition.Device, Priority = CommandPriority.Low,
            Context = new() { Scene = scene.Name, Playlist = scene.ActivatingPlaylist, Item = "Next scene" },
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { command = "scene", value = transition.Scene }) };
        Telemetry.SetRequestContext(id, new() { Source = options.EffectiveDeviceName, Device = command.Device, Scene = scene.Name,
            Playlist = scene.ActivatingPlaylist, Item = "Next scene", Value = transition.Scene, CorrelationId = chain.CorrelationId, Priority = "low" });
        await _dispatcher.SendAsync(command, options, chain, id, token).ConfigureAwait(false);
        _log.Info("CommandSent", $"{scene.Name} → scene {transition.Scene} on {transition.Device}",
            new { Scene = scene.Name, NextScene = transition.Scene, transition.Device, chain.CorrelationId }, id);
    }
    private bool _started, _stopping;
    internal Func<int, int, int> ChooseInterval { get; init; } = Random.Shared.Next;
    internal Func<int, int> ChooseRole { get; init; } = Random.Shared.Next;

    public PlaylistEngine(ControllerOptions options, ICommandDispatcher dispatcher, ActivityLog log)
    { _options = options; _dispatcher = dispatcher; _log = log; _scheduler = new(log); _loopAnnouncements = new(dispatcher, log, () => LoopAnnouncementInterval, _shutdown.Token); }
    public void Start() { lock (_gate) { _started = true; ApplyTimers(); } }
    public void Apply(ControllerOptions options) { lock (_gate) { _options = options; if (_started) ApplyTimers(); } }
    public PlaylistRun Run(string name, ControllerOptions? snapshot = null, BroadcastChain? chain = null, string? owner = null, CancellationToken token = default, SceneExecutionContext? sceneContext = null, bool isAmbient = false, string? roles = null, CommandPriority priority = CommandPriority.Medium, bool nested = false, bool commandsOnly = false, LoopAnnouncementOwner? loopOwner = null)
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
                var durationTasks = !state.CommandsOnly ? await Task.WhenAll(state.MediaEnds).WaitAsync(state.Cancellation.Token).ConfigureAwait(false) : [];
                var end = state.CommandsOnly ? state.Started : Math.Max(state.Started + DeadlineScheduler.Ticks(playlist.EffectiveMinimumDurationMilliseconds), durationTasks.DefaultIfEmpty(state.Started).Max());
                if (playlist.MaximumDurationMilliseconds is { } maximum) end = Math.Min(end, state.Started + DeadlineScheduler.Ticks(maximum));
                _log.Info("PlaylistDurationResolved", state.CommandsOnly ? $"{playlist.Name}: scheduled commands sent" : $"{playlist.Name}: waiting for bounded media timing", new { Playlist = playlist.Name, state.Id, MinimumDurationMilliseconds = playlist.EffectiveMinimumDurationMilliseconds, playlist.MaximumDurationMilliseconds, AutomaticDurationMilliseconds = Stopwatch.GetElapsedTime(state.Started, end).TotalMilliseconds, MediaEndTimesEstimated = !state.CommandsOnly });
                await _scheduler.UntilAsync(end, state.Cancellation.Token).ConfigureAwait(false);
            }, sceneContext: sceneContext, isAmbient: isAmbient, roles: roles, priority: priority,
                triggered: !nested && !isAmbient && priority != CommandPriority.Low, commandsOnly: commandsOnly, loopOwner: loopOwner);
    }
    public string RunManually(string name) => Run(name, owner: "Manual").Id;
    internal PlaylistRun RunAmbientItem(PlaylistOptions playlist, PlaylistItem item, ControllerOptions options, string scene, CancellationToken token, SceneExecutionContext? sceneContext = null)
    {
        var chain = BroadcastChain.Root(options.EffectiveDeviceName).Enter($"{options.EffectiveDeviceName}/playlist:{playlist.Name}");
        return StartRun(playlist, options, chain, $"Scene: {scene}", token, state => ExecuteItemAsync(playlist, item, options, chain, state), 1, sceneContext, isAmbient: true, priority: CommandPriority.Low);
    }
    private PlaylistRun StartRun(PlaylistOptions playlist, ControllerOptions options, BroadcastChain chain, string? owner, CancellationToken token, Func<RunState, Task> execute, int? count = null, SceneExecutionContext? sceneContext = null, bool isAmbient = false, string? roles = null, CommandPriority priority = CommandPriority.Medium, bool triggered = false, bool commandsOnly = false, LoopAnnouncementOwner? loopOwner = null)
    {
        RunState state;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            token.ThrowIfCancellationRequested();
            if (_runs.Count >= 256) throw new InvalidOperationException("At most 256 playlist runs may be active.");
            state = new(Guid.NewGuid().ToString("N"), playlist.Name, owner, count ?? playlist.Items.Count(i => i.Enabled), CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token));
            state.SceneContext = sceneContext ?? Volatile.Read(ref _sceneContext);
            state.LoopOwner = loopOwner ?? new(state.Id, state.Cancellation.Token);
            state.IsAmbient = isAmbient;
            state.Priority = priority;
            state.Triggered = triggered;
            state.CommandsOnly = commandsOnly;
            state.ExternalToken = token;
            state.MaximumDurationMilliseconds = playlist.Type == "storyboard" ? playlist.MaximumDurationMilliseconds : null;
            if (state.MaximumDurationMilliseconds is { } maximum) state.Cancellation.CancelAfter(maximum);
            state.Roles = roles;
            _runs.Add(state.Id, state);
        }
        if (triggered) TriggeredPlaylistStarted?.Invoke();
        _scheduler.Post(() => _ = ExecuteRunAsync(state, execute, chain));
        return new(state.Id, state.Completion.Task, state.Outcome.Task);
    }
    private async Task ExecuteRunAsync(RunState state, Func<RunState, Task> execute, BroadcastChain chain)
    {
        var outcome = PlaylistOutcome.Completed;
        _log.Info("PlaylistStarted", $"Playlist started: {state.Name}", new { state.Id, Playlist = state.Name, state.Owner, state.IsAmbient, Scene = state.SceneContext?.SceneName, Generation = state.SceneContext?.Generation, chain.CorrelationId });
        try
        {
            await execute(state).ConfigureAwait(false);
            if (state.HadFailures) { outcome = PlaylistOutcome.Failed; _log.Warning("PlaylistFinishedWithErrors", $"Playlist finished with command errors: {state.Name}", new { state.Id, Playlist = state.Name }); }
            else _log.Info("PlaylistFinished", $"Playlist finished: {state.Name}", new { state.Id, Playlist = state.Name });
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (!state.ExternalToken.IsCancellationRequested && !_shutdown.IsCancellationRequested && state.MaximumDurationMilliseconds is { } maximum &&
                Stopwatch.GetElapsedTime(state.Started).TotalMilliseconds >= maximum)
                _log.Info("PlaylistMaximumReached", $"Playlist maximum reached: {state.Name}", new { state.Id, Playlist = state.Name, MaximumDurationMilliseconds = maximum, PlaybackContinues = true });
            else
            {
                outcome = PlaylistOutcome.Cancelled;
                _log.Info("PlaylistCancelled", $"Playlist cancelled: {state.Name}", new { state.Id, Playlist = state.Name, Scene = state.SceneContext?.SceneName, Generation = state.SceneContext?.Generation, PlaybackContinues = true });
            }
        }
        catch (Exception ex) { outcome = PlaylistOutcome.Failed; _log.Error("PlaylistFailed", ex, new { state.Id, Playlist = state.Name, Scene = state.SceneContext?.SceneName, Generation = state.SceneContext?.Generation }); }
        finally
        {
            _loopAnnouncements.EndRun(state.Id);
            lock (_gate) { _runs.Remove(state.Id); state.Cancellation.Dispose(); }
            if (state.Triggered) TriggeredPlaylistFinished?.Invoke();
            state.Outcome.TrySetResult(outcome);
            state.Completion.TrySetResult();
        }
    }
    private async Task ObserveCancelledDispatchAsync(Task<DispatchReceipt> dispatch, string requestId)
    {
        try { await dispatch.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("CancelledCommandDispatchFailed", ex, new { PlaybackContinues = true }, requestId); }
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
            if (RoleList.ParseTargets(roles).Length > 0)
                _log.Info("PlaylistRoleTargetSelected", $"{playlist.Name}/{item.Name} → roles {roles}", new
                { Playlist = playlist.Name, Item = item.Name, run.Id, run.IsAmbient, item.BroadcastMode, ConfiguredRoles = item.Roles, SelectedRoles = roles, chain.CorrelationId });
            foreach (var command in item.Commands)
            {
                token.ThrowIfCancellationRequested(); attempted = true;
                var id = Guid.NewGuid().ToString("N");
                try
                {
                    if (command.Playlist is { } nested)
                    {
                        var child = Run(nested, options, chain, run.Owner, token, run.SceneContext, run.IsAmbient, roles, run.Priority, nested: true, commandsOnly: run.CommandsOnly, loopOwner: run.LoopOwner);
                        await child.Completion.ConfigureAwait(false); token.ThrowIfCancellationRequested();
                        if (await child.Outcome.ConfigureAwait(false) != PlaylistOutcome.Completed) throw new InvalidOperationException($"Nested playlist {nested} did not complete successfully.");
                    }
                    else
                    {
                        var routed = PlaylistRoleTarget.Apply(command, roles);
                        var requestedRoles = ControllerOptions.Text(routed.Payload, "roles");
                        if (ControllerOptions.Same(playlist.AudioOutputMode, "Random") && PlaylistAudioTarget.IsAudio(routed))
                        {
                            var target = PlaylistAudioTarget.Random(routed, ReadExperiences(), ChooseAudioOutput);
                            routed = target.Command;
                            _log.Info("PlaylistAudioOutputSelected", $"{playlist.Name}/{item.Name} → {target.Hostname}: {target.SpeakerName}",
                                new { Playlist = playlist.Name, Item = item.Name, Roles = requestedRoles, target.Hostname, target.SpeakerName, AudioDevice = ControllerOptions.Text(routed.Payload, "audioDevice"), routed.Device, Value = ControllerOptions.Text(routed.Payload, "value") }, id);
                        }
                        var outgoing = routed with { Priority = VideoCommand.IsLoop(command) ? CommandPriority.Low : run.Priority,
                            Context = new() { Scene = run.SceneContext?.SceneName, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient } };
                        Telemetry.SetRequestContext(id, new()
                        {
                            Source = options.EffectiveDeviceName, Device = outgoing.Device, Roles = requestedRoles,
                            Scene = outgoing.Context.Scene, Playlist = playlist.Name, Item = item.Name, IsAmbient = run.IsAmbient,
                            Experience = ControllerOptions.Text(command.Payload, "experience"), Value = ControllerOptions.Text(command.Payload, "value"),
                            CorrelationId = chain.CorrelationId, Priority = outgoing.Priority.ToString().ToLowerInvariant()
                        });
                        var due = Stopwatch.GetTimestamp();
                        _loopAnnouncements.Remember(outgoing, options, chain, run.LoopOwner.Id, run.CommandsOnly || run.IsAmbient, run.SceneContext, run.LoopOwner.Lifetime);
                        DispatchReceipt receipt;
                        try
                        {
                            var dispatch = _dispatcher.SendAsync(outgoing, options, chain, id, token);
                            try { receipt = await dispatch.WaitAsync(token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            { _ = ObserveCancelledDispatchAsync(dispatch, id); throw; }
                        }
                        finally { if (VideoCommand.IsLoop(outgoing)) _loopAnnouncements.InitialSendFinished(outgoing); }
                        if (ControllerOptions.Text(command.Payload, "command") == "scene" && (outgoing.Device == "*" || ControllerOptions.Same(outgoing.Device, options.EffectiveDeviceName)))
                        {
                            var selected = Volatile.Read(ref _sceneContext);
                            if (ControllerOptions.Same(selected?.SceneName, ControllerOptions.Text(command.Payload, "value"))) run.SceneContext = selected;
                        }
                        if (!run.CommandsOnly && !run.IsAmbient && !VideoCommand.IsLoop(command) && ControllerOptions.Text(command.Payload, "command")?.ToLowerInvariant() is null or "play" &&
                            ControllerOptions.Text(command.Payload, "experience") is "video" or "audio" && ControllerOptions.Text(command.Payload, "value") is { } media)
                        {
                            var sent = Stopwatch.GetTimestamp();
                            run.MediaEnds.Add(EstimateEndAsync());
                            async Task<long> EstimateEndAsync()
                            {
                                try
                                {
                                    var duration = await MediaDurations.ReadAsync(media, ReadMediaDurationMilliseconds, token).ConfigureAwait(false);
                                    if (duration is > 0 && double.IsFinite(duration.Value)) return sent + DeadlineScheduler.Ticks(duration.Value / VideoPlaybackSpeed.Read(command.Payload));
                                    _log.Warning("PlaylistMediaDurationUnavailable", "Set a playlist duration when local media metadata is unavailable", new { Playlist = playlist.Name, Item = item.Name, Media = media });
                                }
                                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                                catch (Exception ex) { _log.Warning("PlaylistMediaDurationUnavailable", ex.Message, new { Playlist = playlist.Name, Media = media }); }
                                return sent;
                            }
                        }
                        var value = ControllerOptions.Text(command.Payload, "value");
                        _log.Info("CommandSent", $"{playlist.Name}/{item.Name} → {outgoing.Device}" + (value is null ? "" : $" ({value})"), new { Playlist = playlist.Name, Item = item.Name, run.IsAmbient, outgoing.Priority, command.Transport, command.Type, outgoing.Device, Roles = ControllerOptions.Text(outgoing.Payload, "roles"), Value = value, chain.CorrelationId, SendLatencyMilliseconds = receipt.SentAt > 0 ? Stopwatch.GetElapsedTime(due, receipt.SentAt).TotalMilliseconds : (double?)null }, id);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { run.HadFailures = true; _log.Error("CommandFailed", ex, new { Playlist = playlist.Name, Item = item.Name, command.Device, Scene = run.SceneContext?.SceneName, Generation = run.SceneContext?.Generation }, id); }
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
        _loopAnnouncements.Clear();
        lock (_gate)
        {
            // Cancellation can complete a nested run synchronously and remove it.
            foreach (var run in _runs.Values.ToArray())
            {
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
        await Task.WhenAll(pending).ConfigureAwait(false); await _loopAnnouncements.DisposeAsync().ConfigureAwait(false); _scheduler.Dispose(); _shutdown.Dispose();
        foreach (var state in _items.Values) state.Gate.Dispose();
    }
    private sealed class RunState(string id, string name, string? owner, int remaining, CancellationTokenSource cancellation)
    {
        public string Id = id, Name = name; public string? Owner = owner; public int Remaining = remaining;
        public SceneExecutionContext? SceneContext;
        public LoopAnnouncementOwner LoopOwner = null!;
        public bool CommandsOnly, HadFailures;
        public CancellationToken ExternalToken;
        public int? MaximumDurationMilliseconds;
        public bool IsAmbient;
        public CommandPriority Priority;
        public bool Triggered;
        public readonly List<Task<long>> MediaEnds = [];
        public string? Roles;
        public long Started = Stopwatch.GetTimestamp(); public CancellationTokenSource Cancellation = cancellation;
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PlaylistOutcome> Outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ItemState { public readonly SemaphoreSlim Gate = new(1, 1); public long LastCompleted; }
    private sealed class TriggerState { public bool Busy; public long Completed; }
    private sealed class TimerState(int interval, CancellationTokenSource cancellation)
    { public int Interval = interval; public CancellationTokenSource Cancellation = cancellation; public Task Completion = Task.CompletedTask; }
}
