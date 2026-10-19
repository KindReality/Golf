using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Scheduling;

internal sealed record SceneStatus(string? Active, string? Destination, string Phase, int TriggeredPlaylists = 0);
internal sealed class SceneManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PlaylistEngine _engine;
    private readonly ActivityLog _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _retired = [];
    private ControllerOptions _options;
    private Context? _active;
    private string? _destination;
    private string _phase = "Starting";
    private CancellationTokenSource? _transition;
    private Task _work = Task.CompletedTask;
    private bool _stopping;
    private bool _halted;
    private long _generation;
    private (string Name, BroadcastChain? Chain)? _pending;
    private int _triggeredPlaylists;
    public SceneManager(ControllerOptions options, PlaylistEngine engine, ActivityLog log)
    {
        _options = options; _engine = engine; _log = log;
        engine.TriggeredPlaylistStarted += PlaylistStarted; engine.TriggeredPlaylistFinished += PlaylistFinished;
    }
    public SceneStatus Snapshot() { lock (_gate) return new(_active?.Scene.Name, _pending?.Name ?? _destination, _phase, _triggeredPlaylists); }
    private void PlaylistStarted()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _triggeredPlaylists++;
            if (ControllerOptions.Same(_pending?.Name, "idle") && ControllerOptions.Same(_active?.Scene.Name, "active")) _pending = null;
        }
    }
    private void PlaylistFinished()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _triggeredPlaylists = Math.Max(0, _triggeredPlaylists - 1);
            if (_halted) return;
            var requestedScene = _pending?.Name ?? _destination ?? _active?.Scene.Name;
            if (_triggeredPlaylists == 0 && _options.Scenes.Any(s => ControllerOptions.Same(s.Name, "idle")) &&
                ControllerOptions.Same(requestedScene, "active"))
                Request("idle");
        }
    }
    public void Start() => Request(_options.Scenes[0].Name);
    public bool Request(string name, BroadcastChain? chain = null)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            var options = _options; var scene = options.Scene(name);
            _halted = false;
            if (_phase == "Activating")
            {
                if (ControllerOptions.Same(_active?.Scene.Name, scene.Name)) { _pending = null; return false; }
                _pending = (scene.Name, chain);
                _log.Info("SceneChangeDeferred", $"Scene queued: {scene.Name}", new { Scene = scene.Name, Current = _active?.Scene.Name });
                return true;
            }
            if (ControllerOptions.Same(_destination, scene.Name) || _destination is null && _phase == "Active" && ControllerOptions.Same(_active?.Scene.Name, scene.Name)) return false;
            var ancestry = (chain ?? BroadcastChain.Root(options.EffectiveDeviceName)).Enter($"{options.EffectiveDeviceName}/scene:{scene.Name}");
            _transition?.Cancel();
            _active?.Lifetime.Cancel(); _active?.Ambient?.StopScheduling();
            _active?.Loops.Seal();
            var previous = _work; var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _transition = cancellation; _destination = scene.Name; _phase = "Transitioning";
            var generation = ++_generation;
            // Serialize cleanup, then let the latest request replace all unsent transition work.
            _work = Task.Run(() => TransitionAsync(previous, scene, options, ancestry, generation, cancellation));
            return true;
        }
    }
    private async Task TransitionAsync(Task previous, SceneOptions scene, ControllerOptions options, BroadcastChain chain, long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await previous.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            Context? old;
            lock (_gate) old = _active;
            if (old is not null)
            {
                if (!old.ExitStarted)
                {
                    old.ExitStarted = true;
                    await _engine.StopSceneLoopsAsync(old.Loops).ConfigureAwait(false);
                    if (old.Scene.DeactivatingPlaylist is { } exit)
                    {
                        SetPhase(generation, "Deactivating");
                        await _engine.Run(exit, old.Options, chain, "Scene deactivation", token, old.Loops, priority: CommandPriority.Low).Completion.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                }
                if (old.Ambient is { } ambient) await ambient.DisposeAsync().ConfigureAwait(false);
                lock (_gate) { if (ReferenceEquals(_active, old)) _active = null; old.Lifetime.Dispose(); }
            }
            token.ThrowIfCancellationRequested();
            Context current;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); current = new Context(scene, options); _active = current; _phase = "Activating";
                _engine.SetSceneLoops(current.Loops);
                _log.Info("SceneActivating", $"Scene activating: {scene.Name}", new { Scene = scene.Name, chain.CorrelationId });
            }
            if (scene.ActivatingPlaylist is { } entry)
            { await _engine.Run(entry, options, chain, "Scene activation", token, current.Loops, priority: CommandPriority.Low).Completion.ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
            (string Name, BroadcastChain? Chain)? pending;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                // Activation retains its snapshot. The first ambient schedule uses the latest valid configuration.
                var latest = _options.Scenes.FirstOrDefault(s => ControllerOptions.Same(s.Name, scene.Name));
                pending = _pending; _pending = null;
                if (latest is not null)
                {
                    current.Scene = latest; current.Options = _options;
                    if (pending is null) current.Ambient = new(_engine, _options, latest, current.Lifetime.Token, _log, current.Loops);
                }
                _destination = null; _phase = "Active";
                // Commit the queued handoff atomically, so a newly arriving trigger cannot
                // be overwritten by an older pending Idle request after this gate is released.
                if (pending is { } next) Request(next.Name, next.Chain);
            }
            _log.Info("SceneActivated", $"Scene active: {scene.Name}", new { Scene = scene.Name, chain.CorrelationId });
            lock (_gate) if (!_halted && !_options.Scenes.Any(s => ControllerOptions.Same(s.Name, scene.Name))) Request(_options.Scenes[0].Name);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error("SceneTransitionFailed", ex, new { Scene = scene.Name });
            lock (_gate) if (_generation == generation) { _destination = null; _pending = null; _phase = "Failed"; }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_transition, cancellation)) _transition = null; cancellation.Dispose(); }
        }
    }
    private void SetPhase(long generation, string phase) { lock (_gate) if (_generation == generation) _phase = phase; }
    public void StopImmediately()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _halted = true;
            ++_generation;
            _pending = null; _destination = null; _phase = "Stopped";
            _transition?.Cancel();
            if (_active is { } current)
            {
                current.Lifetime.Cancel();
                current.Ambient?.StopScheduling();
                current.Loops.Abandon();
                // This scene is abandoned, not deactivated later when another is requested.
                current.ExitStarted = true;
            }
            _engine.CancelAll();
        }
    }
    public void Apply(ControllerOptions options)
    {
        lock (_gate)
        {
            _options = options;
            if (_halted) return;
            if (_destination is not null) return;
            var scene = options.Scenes.FirstOrDefault(s => ControllerOptions.Same(s.Name, _active?.Scene.Name));
            if (scene is null) { Request(options.Scenes[0].Name); return; }
            if (_active!.Ambient is { } old) { old.StopScheduling(); _retired.Add(old.DisposeAsync()); }
            _retired.RemoveAll(t => t.IsCompleted);
            _active.Scene = scene; _active.Options = options;
            _active.Ambient = new(_engine, options, scene, _active.Lifetime.Token, _log, _active.Loops);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task work;
        lock (_gate) { if (_stopping) return; _stopping = true; _shutdown.Cancel(); _active?.Lifetime.Cancel(); _active?.Ambient?.StopScheduling(); _active?.Loops.Seal(); work = _work; }
        _engine.TriggeredPlaylistStarted -= PlaylistStarted; _engine.TriggeredPlaylistFinished -= PlaylistFinished;
        await work.ConfigureAwait(false);
        if (_active is { } context) { if (context.Ambient is { } ambient) await ambient.DisposeAsync().ConfigureAwait(false); context.Lifetime.Dispose(); }
        await Task.WhenAll(_retired).ConfigureAwait(false); _shutdown.Dispose();
    }
    private sealed class Context(SceneOptions scene, ControllerOptions options)
    { public SceneOptions Scene = scene; public ControllerOptions Options = options; public readonly SceneVideoLoops Loops = new(scene.Name); public readonly CancellationTokenSource Lifetime = new(); public AmbientSession? Ambient; public bool ExitStarted; }
}
