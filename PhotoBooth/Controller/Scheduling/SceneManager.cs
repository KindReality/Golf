using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;
using System.Diagnostics;

namespace Controller.Scheduling;

internal sealed record SceneStatus(string? Active, string? Destination, string Phase, int TriggeredPlaylists = 0);
internal sealed class SceneManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PlaylistEngine _engine;
    private readonly ActivityLog _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _work = [];
    private ControllerOptions _options;
    private Context? _selected;
    private string _phase = "Starting";
    private bool _stopping, _halted;
    private long _generation;
    private int _triggeredPlaylists;
    public SceneManager(ControllerOptions options, PlaylistEngine engine, ActivityLog log)
    {
        _options = options; _engine = engine; _log = log;
        engine.TriggeredPlaylistStarted += PlaylistStarted; engine.TriggeredPlaylistFinished += PlaylistFinished;
    }
    public SceneStatus Snapshot() { lock (_gate) return new(_selected?.Scene.Name, _phase is "Active" or "Stopped" or "Failed" ? null : _selected?.Scene.Transition?.Scene, _phase, _triggeredPlaylists); }
    private void PlaylistStarted() { lock (_gate) if (!_stopping) _triggeredPlaylists++; }
    private void PlaylistFinished()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _triggeredPlaylists = Math.Max(0, _triggeredPlaylists - 1);
            if (!_halted && _triggeredPlaylists == 0 && _selected?.Scene.Transition is null && ControllerOptions.Same(_selected?.Scene.Name, "active") && _options.Scenes.Any(s => ControllerOptions.Same(s.Name, "idle"))) Request("idle");
        }
    }
    public void Start() => Request(_options.Scenes[0].Name);
    public bool Request(string name, BroadcastChain? chain = null)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            var options = _options; var scene = options.Scene(name);
            if (!_halted && _phase != "Failed" && ControllerOptions.Same(_selected?.Scene.Name, scene.Name)) return false;
            var ancestry = (chain ?? BroadcastChain.Root(options.EffectiveDeviceName)).Enter($"{options.EffectiveDeviceName}/scene:{scene.Name}");
            var old = _selected;
            Cancel(old);
            _halted = false;
            var current = new Context(scene, options, ++_generation, _shutdown.Token);
            _selected = current; _phase = "Transitioning";
            // Selection is immediate; activation is cancellable work, never a lock.
            _engine.SetSceneContext(current.Execution, current.Lifetime.Token);
            _log.Info("SceneChangeRequested", $"Scene requested: {scene.Name}", new { PreviousScene = old?.Scene.Name, Scene = scene.Name, current.Generation, ancestry.CorrelationId });
            if (old is not null) Retire(old);
            current.Work = Task.Run(() => TransitionAsync(current, ancestry)); Track(current.Work);
            return true;
        }
    }
    private void Cancel(Context? context)
    {
        if (context is null) return;
        context.Lifetime.Cancel(); context.Ambient?.StopScheduling();
        if (context.ActivationStarted && !context.ActivationCompleted)
            _log.Info("SceneActivationCancelled", $"Activation cancelled: {context.Scene.Name}", new { Scene = context.Scene.Name, context.Generation, PlaybackContinues = true });
    }
    private bool Current(Context context) => !_stopping && !_halted && ReferenceEquals(_selected, context) && !context.Lifetime.IsCancellationRequested;
    private async Task TransitionAsync(Context current, BroadcastChain chain)
    {
        var token = current.Lifetime.Token;
        try
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); if (!Current(current)) return;
                current.ActivationStarted = true; _phase = "Activating";
                _log.Info("SceneActivating", $"Scene activating: {current.Scene.Name}", new { Scene = current.Scene.Name, current.Generation, chain.CorrelationId });
            }
            using var activationStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var activation = current.Scene.ActivatingPlaylist is { } entry
                ? RunPhaseAsync(entry, current.Options, chain, "Scene activation", activationStop.Token, current.Execution) : Task.CompletedTask;
            if (current.Scene.Transition is not null)
            {
                var next = TransitionAfterDelayAsync(current, chain, activationStop);
                try
                {
                    var first = await Task.WhenAny(activation, next).ConfigureAwait(false);
                    if (first == activation)
                    {
                        try { await activation.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (activationStop.IsCancellationRequested)
                        {
                            // The automatic deadline can cancel late cues before its scene
                            // command has finished sending. That is a handoff, not a failure.
                        }
                        lock (_gate) if (Current(current) && !activationStop.IsCancellationRequested) { current.ActivationCompleted = true; _phase = "Waiting"; }
                        await next.ConfigureAwait(false);
                    }
                    else await next.ConfigureAwait(false);
                    lock (_gate) if (Current(current)) { current.ActivationCompleted = true; _phase = "Active"; }
                }
                finally
                {
                    activationStop.Cancel();
                    try { await activation.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (activationStop.IsCancellationRequested) { }
                }
                return;
            }
            await activation.ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); if (!Current(current)) return;
                current.ActivationCompleted = true;
                var latest = _options.Scenes.FirstOrDefault(s => ControllerOptions.Same(s.Name, current.Scene.Name));
                if (latest is null) { Request(_options.Scenes[0].Name); return; }
                // Live ambient edits apply without replaying activation. Changing the
                // scene's mode, like a running storyboard edit, applies on its next entry.
                if (latest.Transition is null) { current.Scene = latest; current.Options = _options; }
                current.Ambient = new(_engine, current.Options, current.Scene, token, _log, current.Execution);
                _phase = "Active";
                _log.Info("SceneActivated", $"Scene active: {current.Scene.Name}", new { Scene = current.Scene.Name, current.Generation, chain.CorrelationId });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error("SceneTransitionFailed", ex, new { Scene = current.Scene.Name, current.Generation });
            lock (_gate) if (Current(current)) { _phase = "Failed"; Cancel(current); }
        }
    }
    private async Task TransitionAfterDelayAsync(Context current, BroadcastChain chain, CancellationTokenSource activationStop)
    {
        var token = current.Lifetime.Token;
        var transition = current.Scene.Transition!;
        var playlist = current.Scene.ActivatingPlaylist is { } activationName ? current.Options.Playlist(activationName) : null;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (playlist?.MaximumDurationMilliseconds is { } cap)
            preparation.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, cap - Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds)));
        SceneTransitionEstimate estimate;
        try { estimate = await SceneTransitionTiming.EstimateAsync(current.Scene, current.Options, _engine.ReadMediaDurationMilliseconds, preparation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && playlist?.MaximumDurationMilliseconds is not null)
        { estimate = new(playlist.MaximumDurationMilliseconds.Value, true); }
        _log.Info("SceneTransitionScheduled", $"{current.Scene.Name} → {transition.Scene} after {estimate.DurationMilliseconds / 1000:0.###} s", new
        { Scene = current.Scene.Name, NextScene = transition.Scene, transition.Device, current.Generation, estimate.DurationMilliseconds,
            MinimumDurationMilliseconds = playlist?.EffectiveMinimumDurationMilliseconds ?? 5000, MaximumDurationMilliseconds = playlist?.MaximumDurationMilliseconds,
            estimate.HasUnknownMedia, CountdownFromSceneStart = true });
        await _engine.WaitUntilAsync(current.Started + DeadlineScheduler.Ticks(estimate.DurationMilliseconds), token).ConfigureAwait(false);
        Task send;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested(); if (!Current(current)) return;
            activationStop.Cancel(); // No old delayed cues are sent after the handoff.
            send = _engine.SendSceneTransitionAsync(current.Scene, transition, current.Options, chain, token);
        }
        await send.ConfigureAwait(false);
        _log.Info("SceneTransitionSent", $"Scene command: {current.Scene.Name} → {transition.Scene} on {transition.Device}",
            new { Scene = current.Scene.Name, NextScene = transition.Scene, transition.Device, current.Generation });
    }
    private void Track(Task task)
    {
        lock (_gate) { _work.RemoveWhere(t => t.IsCompleted); _work.Add(task); }
    }
    private async Task RunPhaseAsync(string playlist, ControllerOptions options, BroadcastChain chain, string phase, CancellationToken token, SceneExecutionContext execution)
    {
        var run = _engine.Run(playlist, options, chain, phase, token, execution, priority: CommandPriority.Low, commandsOnly: true);
        await run.Completion.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (await run.Outcome.ConfigureAwait(false) != PlaylistOutcome.Completed)
            throw new InvalidOperationException($"{phase} playlist {playlist} did not complete successfully.");
    }
    private void Retire(Context context)
    {
        Track(Task.Run(async () =>
        {
            // Only this context disposes its resources; never touch a replacement context.
            await context.Work.ConfigureAwait(false);
            if (context.Ambient is { } ambient) await ambient.DisposeAsync().ConfigureAwait(false);
            context.Lifetime.Dispose();
        }));
    }
    public void StopImmediately()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _halted = true; ++_generation; _phase = "Stopped";
            Cancel(_selected);
            _engine.CancelAll();
        }
    }
    public void Apply(ControllerOptions options)
    {
        lock (_gate)
        {
            _options = options; if (_halted) return;
            var scene = options.Scenes.FirstOrDefault(s => ControllerOptions.Same(s.Name, _selected?.Scene.Name));
            if (scene is null) { Request(options.Scenes[0].Name); return; }
            if (_phase != "Active" || _selected!.Scene.Transition is not null || scene.Transition is not null) return;
            if (_selected!.Ambient is { } old) { old.StopScheduling(); Track(old.DisposeAsync()); }
            _selected.Scene = scene; _selected.Options = options;
            _selected.Ambient = new(_engine, options, scene, _selected.Lifetime.Token, _log, _selected.Execution);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] work;
        lock (_gate)
        {
            if (_stopping) return; _stopping = true; _shutdown.Cancel(); Cancel(_selected);
            if (_selected is { } current) Retire(current);
            work = _work.ToArray();
        }
        _engine.TriggeredPlaylistStarted -= PlaylistStarted; _engine.TriggeredPlaylistFinished -= PlaylistFinished;
        await Task.WhenAll(work).ConfigureAwait(false); _shutdown.Dispose();
    }
    internal int OutstandingWork { get { lock (_gate) { _work.RemoveWhere(t => t.IsCompleted); return _work.Count; } } }
    private sealed class Context(SceneOptions scene, ControllerOptions options, long generation, CancellationToken shutdown)
    {
        public SceneOptions Scene = scene;
        public ControllerOptions Options = options;
        public readonly long Generation = generation;
        public readonly long Started = Stopwatch.GetTimestamp();
        public readonly SceneExecutionContext Execution = new(scene.Name, generation);
        public readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public Task Work = Task.CompletedTask;
        public AmbientSession? Ambient;
        public bool ActivationStarted, ActivationCompleted;
    }
}
