using System.Text.Json;
using Controller.Configuration;

namespace Controller.Scheduling;

internal static class VideoCommand
{
    public static bool IsLoop(PlaylistCommand command) => command.Transport == "udp" && command.Type == "command" &&
        ControllerOptions.Same(ControllerOptions.Text(command.Payload, "experience"), "video") &&
        command.Payload.EnumerateObject().Any(p => ControllerOptions.Same(p.Name, "loop") && p.Value.ValueKind == JsonValueKind.True);
}
