using Controller.Networking;
using PhotoBooth.Networking;

namespace Controller.Application;

internal sealed record LiveTarget(string Hostname, string Id, string? Display = null)
{
    public bool Audio => Display is null;
}
internal sealed record LivePlan(byte[][] Commands, string[] Skipped);
internal sealed record LiveRole(string Name, int Screens, int Speakers);
internal static class LivePlayback
{
    internal static readonly HashSet<string> Videos = new([".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".mkv", ".wmv"], StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> Audio = new([".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".wma"], StringComparer.OrdinalIgnoreCase);
    public static LiveRole[] Roles(ExperiencePeer[] peers, IEnumerable<string>? known = null)
    {
        var names = RoleCatalog.Names(peers.SelectMany(p => p.Inventory.Screens.Select(s => s.Roles)
            .Concat(p.Inventory.Speakers.Select(s => s.Roles))).Concat(known ?? []));
        return names.Select(name => new LiveRole(name,
            peers.Where(p => p.Usable).Sum(p => p.Inventory.Screens.Count(s => s.Playable && RoleList.Matches(s.Roles, name))),
            peers.Where(p => p.Usable).Sum(p => p.Inventory.Speakers.Count(s => RoleList.Matches(s.Roles, name))))).ToArray();
    }

    public static LivePlan BuildRoles(BroadcastOptions options, ExperiencePeer[] peers, IEnumerable<string> selection, string filename, int volume, bool muteVideo, double playbackSpeed = 1)
    {
        VideoPlaybackSpeed.Validate(playbackSpeed);
        var video = ValidateMedia(filename, volume);
        var roles = string.Join(",", RoleList.ParseTargets(string.Join(",", selection)));
        if (roles.Length == 0) return new([], []);
        var commands = new List<byte[]>(); var skipped = new List<string>();
        foreach (var peer in peers)
        {
            var inventory = peer.Inventory;
            var matches = video ? inventory.Screens.Any(s => s.Kind == "video" && RoleList.Matches(s.Roles, roles))
                : inventory.Speakers.Any(s => RoleList.Matches(s.Roles, roles));
            if (!matches) continue;
            if (!peer.Usable) { skipped.Add(inventory.DeviceName + ": offline or duplicate device name"); continue; }
            if (video && !inventory.Screens.Any(s => s.Playable && RoleList.Matches(s.Roles, roles)))
            { skipped.Add(inventory.DeviceName + ": matching screens unavailable or media locked"); continue; }
            if (!(video ? inventory.VideoFiles : inventory.AudioFiles).Contains(filename, StringComparer.OrdinalIgnoreCase))
            { skipped.Add(inventory.DeviceName + ": media has not been downloaded"); continue; }
            if (!video && inventory.Speakers.Count(s => RoleList.Matches(s.Roles, roles)) > 32)
                throw new ArgumentException("Select roles matching at most 32 audio outputs per device.");
            // One command per Experience: the recipient selects matching outputs once and coordinates its audio.
            object payload = video
                ? new { experience = "video", value = filename, roles, loop = false, mute = muteVideo, playbackSpeed }
                : new { experience = "audio", value = filename, roles, loop = false, mute = false, volume };
            try { commands.Add(BroadcastEnvelope.Create(options, "command", inventory.DeviceName, payload, context: new() { Item = "Live" })); }
            catch (ArgumentException ex) when (ex.Message.Contains("1200"))
            { throw new ArgumentException("The selected role list exceeds the UDP packet size. Select fewer roles.", ex); }
        }
        if (commands.Count == 0 && skipped.Count == 0)
            skipped.Add("No available " + (video ? "video screens" : "speakers") + " match the selected roles.");
        return new(commands.ToArray(), skipped.ToArray());
    }

    private static bool ValidateMedia(string filename, int volume)
    {
        PhotoBooth.Media.MediaLibrary.Normalize(filename);
        if (volume is < 0 or > 100)
            throw new ArgumentException("Invalid Live playback request.");
        var video = Videos.Contains(Path.GetExtension(filename));
        if (!video && !Audio.Contains(Path.GetExtension(filename))) throw new ArgumentException("Choose a video or audio file.");
        return video;
    }

    public static LivePlan Build(BroadcastOptions options, ExperiencePeer[] peers, IEnumerable<LiveTarget> selection, string filename, int volume, bool muteVideo, double playbackSpeed = 1)
    {
        VideoPlaybackSpeed.Validate(playbackSpeed);
        var video = ValidateMedia(filename, volume);
        var commands = new List<byte[]>(); var skipped = new List<string>();
        foreach (var group in selection.Distinct().Where(t => t.Audio != video).GroupBy(t => t.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            var peer = peers.FirstOrDefault(p => p.Inventory.Hostname.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (peer is null || !peer.Usable) { skipped.Add(group.Key + ": offline or duplicate device name"); continue; }
            var inventory = peer.Inventory;
            if (!(video ? inventory.VideoFiles : inventory.AudioFiles).Contains(filename, StringComparer.OrdinalIgnoreCase)) { skipped.Add(group.Key + ": media has not been downloaded"); continue; }
            if (video)
            {
                foreach (var target in group)
                {
                    var screen = inventory.Screens.FirstOrDefault(s => s.Id == target.Id && s.Display == target.Display);
                    if (screen?.Playable != true) { skipped.Add(group.Key + ": screen unavailable or media locked"); continue; }
                    commands.Add(BroadcastEnvelope.Create(options, "command", inventory.DeviceName, new { experience = "video", value = filename, screen = screen.Display + "/" + screen.Name, loop = false, mute = muteVideo, playbackSpeed }, context: new() { Item = "Live" }));
                }
            }
            else
            {
                var outputs = group.Select(t => inventory.Speakers.FirstOrDefault(s => s.Id == t.Id)).ToArray();
                if (outputs.Any(o => o is null)) { skipped.Add(group.Key + ": audio device disconnected"); continue; }
                if (outputs.Length > 32) throw new ArgumentException("Select at most 32 audio outputs per device.");
                object payload = outputs.Length == inventory.Speakers.Length
                    ? new { experience = "audio", value = filename, audioDevice = "*", loop = false, mute = false, volume }
                    : new { experience = "audio", value = filename, audioDevices = outputs.Select(o => inventory.Speakers.Count(s => s.Name.Equals(o!.Name, StringComparison.OrdinalIgnoreCase)) == 1 ? o!.Name : o!.Id).ToArray(), loop = false, mute = false, volume };
                try { commands.Add(BroadcastEnvelope.Create(options, "command", inventory.DeviceName, payload, context: new() { Item = "Live" })); }
                catch (ArgumentException ex) when (ex.Message.Contains("1200")) { throw new ArgumentException("The selected speaker list exceeds the UDP packet size. Select fewer outputs or all outputs on this device.", ex); }
            }
        }
        return new(commands.ToArray(), skipped.ToArray());
    }
}
