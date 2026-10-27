using Controller.Diagnostics;
using PhotoBooth.Media;

namespace Controller.Configuration;

internal static class PlaylistMediaValidation
{
    internal static void WarnMissing(ControllerOptions options, MediaInventory inventory, ActivityLog log)
    {
        var available = inventory.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in options.Playlists)
            foreach (var item in playlist.Items)
            {
                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var command in item.Commands)
                {
                    if (command.Playlist is not null || command.Transport != "udp" || command.Type != "command" ||
                        ControllerOptions.Text(command.Payload, "experience")?.ToLowerInvariant() is not ("video" or "audio") ||
                        ControllerOptions.Text(command.Payload, "command")?.ToLowerInvariant() is not (null or "play") ||
                        ControllerOptions.Text(command.Payload, "value") is not { Length: > 0 } file) continue;
                    string path;
                    try { path = MediaLibrary.Normalize(file); }
                    catch (ArgumentException) { path = file; }
                    if (!available.Contains(path) && reported.Add(path))
                        log.Warning("PlaylistMediaMissingAtStartup", $"Missing media: {path} — {playlist.Name}/{item.Name}",
                            new { Playlist = playlist.Name, Item = item.Name, Media = path, Phase = "Startup", PlaybackBlocked = false });
                }
            }
    }
}
