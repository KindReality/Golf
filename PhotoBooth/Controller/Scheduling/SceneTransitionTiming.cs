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
        var minimum = (scene.Transition?.MinimumSceneDurationSeconds ?? 5) * 1000;
        var media = new List<(string File, double Start)>();
        var visited = 0;
        double Visit(string name, double at, HashSet<string> path)
        {
            if (!path.Add(name) || path.Count > 8) throw new ArgumentException("Activation playlist nesting is cyclic or too deep.");
            try
            {
                foreach (var item in options.Playlist(name, "storyboard").Items.Where(i => i.Enabled))
                {
                    at += item.DelayMilliseconds;
                    foreach (var command in item.Commands)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++visited > 10000) throw new ArgumentException("Activation timing exceeds 10000 expanded commands.");
                        if (command.Playlist is { } nested) at = Visit(nested, at, path);
                        else if (command.Type == "command" && command.Transport == "udp" && !VideoCommand.IsLoop(command) &&
                            ControllerOptions.Text(command.Payload, "command") != "stop" &&
                            ControllerOptions.Text(command.Payload, "experience") is "video" or "audio" &&
                            ControllerOptions.Text(command.Payload, "value") is { } file)
                            media.Add((file, at));
                    }
                }
                return at;
            }
            finally { path.Remove(name); }
        }
        if (scene.ActivatingPlaylist is { } entry) Visit(entry, 0, new(StringComparer.OrdinalIgnoreCase));
        if (media.Count == 0) return new(minimum, false);
        var durations = new Dictionary<string, Task<double?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in media)
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
        foreach (var (file, at) in media)
        {
            var duration = await durations[file].ConfigureAwait(false);
            if (duration is not > 0 || !double.IsFinite(duration.Value)) { unknown = true; continue; }
            end = Math.Max(end, at + duration.Value);
        }
        return new(Math.Max(minimum, Math.Ceiling(end)), unknown);
    }
}
