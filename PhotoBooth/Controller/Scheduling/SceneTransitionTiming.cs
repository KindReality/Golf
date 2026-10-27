using Controller.Configuration;

namespace Controller.Scheduling;

internal sealed record SceneTransitionEstimate(double DurationMilliseconds, bool HasUnknownMedia);

// Shared by the editor and scheduler. Cue delays accumulate exactly as CommandsOnly
// playlist execution does; media can overlap, so use the latest end, never their sum.
internal static class SceneTransitionTiming
{
    internal static async Task<SceneTransitionEstimate> EstimateAsync(SceneOptions scene, ControllerOptions options,
        Func<string, double?> readDuration, CancellationToken token)
    {
        var activation = scene.ActivatingPlaylist is { } name ? options.Playlist(name, "storyboard") : null;
        var minimum = activation?.EffectiveMinimumDurationMilliseconds ?? 5000;
        var maximum = activation?.MaximumDurationMilliseconds;
        double Bound(double value) => Math.Min(maximum ?? double.PositiveInfinity, Math.Max(minimum, value));
        // A fixed duration needs no potentially slow media metadata read.
        if (maximum == minimum) return new(minimum, false);
        var media = new List<(string File, double Start, double Speed)>();
        var visited = 0;
        double Visit(string name, double at, HashSet<string> path, bool root = false)
        {
            if (!path.Add(name) || path.Count > 8) throw new ArgumentException("Activation playlist nesting is cyclic or too deep.");
            try
            {
                var playlist = options.Playlist(name, "storyboard");
                var deadline = !root && playlist.MaximumDurationMilliseconds is { } limit ? at + limit : double.PositiveInfinity;
                foreach (var item in playlist.Items.Where(i => i.Enabled))
                {
                    at += item.DelayMilliseconds;
                    if (at >= deadline) return deadline;
                    foreach (var command in item.Commands)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++visited > 10000) throw new ArgumentException("Activation timing exceeds 10000 expanded commands.");
                        if (command.Playlist is { } nested) at = Visit(nested, at, path);
                        else if (command.Type == "command" && command.Transport == "udp" && !VideoCommand.IsLoop(command) &&
                            ControllerOptions.Text(command.Payload, "command")?.ToLowerInvariant() is null or "play" &&
                            ControllerOptions.Text(command.Payload, "experience") is "video" or "audio" &&
                            ControllerOptions.Text(command.Payload, "value") is { } file)
                            media.Add((file, at, PhotoBooth.Networking.VideoPlaybackSpeed.Read(command.Payload)));
                    }
                }
                return at;
            }
            finally { path.Remove(name); }
        }
        if (scene.ActivatingPlaylist is { } entry) Visit(entry, 0, new(StringComparer.OrdinalIgnoreCase), root: true);
        if (media.Count == 0) return new(Bound(0), false);
        var durations = new Dictionary<string, Task<double?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _, _) in media)
            if (!durations.ContainsKey(file)) durations[file] = ReadAsync(file);
        async Task<double?> ReadAsync(string file)
        {
            try { return await MediaDurations.ReadAsync(file, readDuration, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; } // Unknown metadata cannot extend the known timeline; the scene minimum still applies.
        }
        await Task.WhenAll(durations.Values).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var unknown = false; var end = 0d;
        foreach (var (file, at, speed) in media)
        {
            var duration = await durations[file].ConfigureAwait(false);
            if (duration is not > 0 || !double.IsFinite(duration.Value)) { unknown = true; continue; }
            end = Math.Max(end, at + duration.Value / speed);
        }
        return new(Bound(Math.Ceiling(end)), unknown);
    }
}
