using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoBooth.Networking;

namespace Controller.Configuration;

internal static class PlaylistRoleTarget
{
    internal static string? Select(PlaylistItem item, string? inherited, Func<int, int> choose)
    {
        var roles = RoleList.ParseTargets(item.Roles);
        if (roles.Length == 0) return inherited;
        if (!ControllerOptions.Same(item.BroadcastMode, "Random")) return string.Join(",", roles);
        var choices = RoleList.RandomChoices(item.Roles);
        if (choices.Length == 0) throw new ArgumentException("Random role selection requires a positive rule or '*'.");
        return choices[choices.Length == 1 ? 0 : choose(choices.Length)];
    }

    internal static PlaylistCommand Apply(PlaylistCommand command, string? roles, bool includeSubwoofer = false)
    {
        if (command.Playlist is not null) return command;
        var body = command.Payload;
        var assigned = RoleList.ParseTargets(roles);
        var experience = ControllerOptions.Text(body, "experience")?.ToLowerInvariant();
        var media = experience is "video" or "audio";
        var playback = media && (ControllerOptions.Text(body, "command")?.ToLowerInvariant() is null or "play");
        if (assigned.Length > 0 && includeSubwoofer && experience == "audio")
            assigned = assigned.Append("subwoofer").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (assigned.Length > 0 && playback && command.Transport == "udp" && command.Type == "command")
        {
            var payload = JsonNode.Parse(body.GetRawText())!.AsObject();
            foreach (var key in payload.Select(field => field.Key).Where(key => new[] { "screen", "displayName", "audioDevice", "audioDevices", "roles" }.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
                payload.Remove(key);
            payload["roles"] = string.Join(",", assigned);
            command = command with { Payload = JsonSerializer.SerializeToElement(payload) };
            body = command.Payload;
        }
        var roleProperty = body.ValueKind == JsonValueKind.Object ? body.EnumerateObject().FirstOrDefault(field => ControllerOptions.Same(field.Name, "roles")).Value : default;
        if (roleProperty.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
            throw new ArgumentException("Playback roles must be a comma-separated string.");
        var targets = RoleList.ParseTargets(roleProperty.ValueKind == JsonValueKind.String ? roleProperty.GetString() : null);
        if (targets.Length == 0) return command;
        if (!playback || command.Transport != "udp" || command.Type != "command")
            throw new ArgumentException("Role targets apply to UDP video/audio playback commands only.");
        if (body.EnumerateObject().Any(field => new[] { "screen", "displayName", "audioDevice", "audioDevices" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Role playback commands cannot include direct screen/display/audio targets.");
        return command with { Device = command.Device ?? "*" };
    }
}
