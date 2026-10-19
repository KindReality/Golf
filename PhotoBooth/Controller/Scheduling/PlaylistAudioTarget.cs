using System.Text.Json;
using System.Text.Json.Nodes;
using Controller.Configuration;
using Controller.Networking;
using PhotoBooth.Networking;

namespace Controller.Scheduling;

internal sealed record SelectedAudioTarget(PlaylistCommand Command, string Hostname, string SpeakerName);

// Uses already-fetched inventories: selecting a speaker never performs device/network I/O.
internal static class PlaylistAudioTarget
{
    internal static bool IsAudio(PlaylistCommand command) => command.Transport == "udp" && command.Type == "command" &&
        ControllerOptions.Same(ControllerOptions.Text(command.Payload, "experience"), "audio") &&
        ControllerOptions.Text(command.Payload, "command") is null or "play";

    internal static void Validate(PlaylistCommand command)
    {
        if (!IsAudio(command)) throw new ArgumentException("Random audio outputs apply only to UDP audio playback.");
        if (command.Payload.EnumerateObject().Any(p => p.Name.Equals("audioDevices", StringComparison.OrdinalIgnoreCase)) ||
            ControllerOptions.Text(command.Payload, "audioDevice") is { Length: > 0 } output && output != "*")
            throw new ArgumentException("Random audio uses detected outputs; omit audioDevices and set audioDevice to * or omit it. Roles may restrict eligible speakers.");
    }

    internal static SelectedAudioTarget Random(PlaylistCommand command, ExperiencePeer[] peers, Func<int, int> choose)
    {
        Validate(command);
        var file = ControllerOptions.Text(command.Payload, "value");
        var roles = ControllerOptions.Text(command.Payload, "roles");
        var hasRoles = RoleList.ParseTargets(roles).Length > 0;
        var candidates = peers.Where(p => p.Usable && (command.Device == "*" || ControllerOptions.Same(command.Device, p.Inventory.DeviceName)) &&
                p.Inventory.AudioFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            .SelectMany(p => p.Inventory.Speakers.Where(s => !hasRoles || RoleList.Matches(s.Roles, roles))
                .Select(s => new { Peer = p.Inventory, Speaker = s, Key = p.Inventory.Hostname + "/" + s.Id }))
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No eligible online Experience speaker has the requested local media: " + file);
        var selected = candidates[choose(candidates.Length)];
        var payload = JsonNode.Parse(command.Payload.GetRawText())!.AsObject();
        foreach (var key in payload.Select(p => p.Key).Where(k => new[] { "audioDevice", "roles" }.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray()) payload.Remove(key);
        payload["audioDevice"] = selected.Speaker.Id;
        return new(command with { Device = selected.Peer.DeviceName, Payload = JsonSerializer.SerializeToElement(payload) }, selected.Peer.Hostname, selected.Speaker.Name);
    }
}
