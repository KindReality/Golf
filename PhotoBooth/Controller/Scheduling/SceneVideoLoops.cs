using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Scheduling;

// A scene activation owns playback even after its playlist has finished sending.
internal sealed class SceneVideoLoops(string scene)
{
    public string SceneName => scene;
    private readonly object _gate = new();
    private readonly List<Playback> _playbacks = [];
    private bool _closed;
    internal int Count { get { lock (_gate) return _playbacks.Count; } }
    public void Seal() { lock (_gate) _closed = true; }
    // Emergency cancellation abandons ownership without sending cleanup commands.
    internal void Abandon() { lock (_gate) { _closed = true; _playbacks.Clear(); } }
    public static bool IsLoop(PlaylistCommand command) => command.Transport == "udp" && command.Type == "command" &&
        ControllerOptions.Same(ControllerOptions.Text(command.Payload, "experience"), "video") &&
        command.Payload.EnumerateObject().Any(p => ControllerOptions.Same(p.Name, "loop") && p.Value.ValueKind == JsonValueKind.True);
    public TaskCompletionSource Register(PlaylistCommand command, ControllerOptions options, string requestId)
    {
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("The scene has ended; no new looping videos may start from its old playlists.");
            if (_playbacks.Count >= 4096) throw new InvalidOperationException("A scene can own at most 4096 looping video requests before it changes.");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _playbacks.Add(new(command.Device!, options, requestId, completion.Task, command.Context, ControllerOptions.Text(command.Payload, "value")));
            return completion;
        }
    }
    public async Task StopAsync(ICommandDispatcher dispatcher, ActivityLog log, CancellationToken token)
    {
        Playback[] playbacks;
        lock (_gate) { _closed = true; playbacks = _playbacks.ToArray(); }
        // Finish any send that was already in flight before sending its stop. Never wait for video completion.
        await Task.WhenAll(playbacks.Select(p => p.Sending)).ConfigureAwait(false);
        foreach (var playback in playbacks)
        {
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            var stop = new PlaylistCommand
            {
                Priority = CommandPriority.Low,
                Device = playback.Device,
                Context = playback.Context,
                Payload = JsonSerializer.SerializeToElement(new { experience = "video", command = "stop", playbackRequestId = playback.RequestId, value = playback.Value })
            };
            try
            {
                PhotoBooth.Diagnostics.Telemetry.SetRequestContext(id, new()
                {
                    Source = playback.Options.EffectiveDeviceName, Device = playback.Device, Scene = scene,
                    Playlist = playback.Context?.Playlist, Item = playback.Context?.Item, IsAmbient = playback.Context?.IsAmbient == true, Experience = "video", Value = playback.Value, Priority = "low"
                });
                // Cleanup is a new action, independent of a canceled transition or an exhausted ancestry chain.
                await dispatcher.SendAsync(stop, playback.Options, BroadcastChain.Root(playback.Options.EffectiveDeviceName), id, token).ConfigureAwait(false);
                log.Info("SceneVideoStopSent", $"Stopped loop from {scene}", new { Scene = scene, playback.Device, PlaybackRequestId = playback.RequestId }, id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.Error("SceneVideoStopFailed", ex, new { Scene = scene, playback.Device, PlaybackRequestId = playback.RequestId }, id); }
        }
        lock (_gate) _playbacks.Clear();
    }
    private sealed record Playback(string Device, ControllerOptions Options, string RequestId, Task Sending, CommandContext? Context, string? Value);
}
