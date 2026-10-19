using Controller.Configuration;
using Controller.Diagnostics;

namespace Controller.Scheduling;

// Reload cancels scheduling only. Scene exit also cancels the lifetime of already running items.
internal sealed class AmbientSession
{
    private readonly CancellationTokenSource _scheduling = new();
    private readonly object _gate = new();
    private bool _disposed;
    public Task Completion { get; }
    public AmbientSession(PlaylistEngine engine, ControllerOptions options, SceneOptions scene, CancellationToken lifetime, ActivityLog log, SceneVideoLoops? loops = null)
    {
        var playlist = options.Playlist(scene.AmbientPlaylist, "ambient");
        if (playlist.DurationMilliseconds is { } duration) _scheduling.CancelAfter(duration);
        Completion = Task.WhenAll(playlist.Items.Where(i => i.Enabled).Select(LoopAsync));
        async Task LoopAsync(PlaylistItem item)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_scheduling.Token, lifetime);
            try
            {
                var completed = false;
                while (true)
                {
                    var interval = engine.AmbientInterval(options, playlist, item, completed);
                    await engine.WaitUntilAsync(DeadlineScheduler.After(interval), linked.Token).ConfigureAwait(false);
                    var run = engine.RunAmbientItem(playlist, item, options, scene.Name, lifetime, loops);
                    await run.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
                    completed = true;
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception ex) { log.Error("AmbientItemFailed", ex, new { Scene = scene.Name, Playlist = playlist.Name, Item = item.Name }); }
        }
    }
    public void StopScheduling() { lock (_gate) if (!_disposed) _scheduling.Cancel(); }
    public async Task DisposeAsync() { StopScheduling(); await Completion.ConfigureAwait(false); lock (_gate) { if (_disposed) return; _disposed = true; _scheduling.Dispose(); } }
}
