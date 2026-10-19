using System.Text.Json;
using PhotoBooth.Networking;

namespace Controller.Hardware;

internal sealed record SerialInput(string Name, string Kind, string[] Events);
internal sealed record SerialDeviceAnnouncement(string DeviceId, string Name, SerialInput[] Inputs, string[] Commands)
{
    internal const string EventName = "device.announced";
    internal const string Protocol = "saft.serial";
    internal const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Fields(string? Protocol, int ProtocolVersion, string? DeviceId, SerialInput[]? Inputs, string[]? Commands);

    internal static SerialDeviceAnnouncement Parse(BroadcastEnvelope message)
    {
        var fields = message.Body.Deserialize<Fields>(Json) ?? throw new ArgumentException("Missing serial announcement.");
        if (message.Type != "event" || message.Event != EventName || fields.Protocol != Protocol || fields.ProtocolVersion != ProtocolVersion ||
            fields.Inputs is null || fields.Inputs.Length > 32 || fields.Commands is null || fields.Commands.Length > 32)
            throw new ArgumentException("Unsupported serial announcement or capabilities.");
        BroadcastOptions.ValidateName(fields.DeviceId, "deviceId", false);
        if (fields.DeviceId != fields.DeviceId!.Trim() || message.Source.Contains('/')) throw new ArgumentException("Invalid serial identity.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in fields.Inputs)
        {
            if (input is null || input.Events is null || input.Events.Length is 0 or > 16) throw new ArgumentException("Invalid serial input.");
            BroadcastOptions.ValidateName(input.Name, "input", false);
            BroadcastOptions.ValidateName(input.Kind, "kind", false);
            if (input.Name != input.Name.Trim() || !names.Add(input.Name)) throw new ArgumentException("Duplicate serial input.");
            ValidateNames(input.Events);
        }
        ValidateNames(fields.Commands);
        return new(fields.DeviceId, message.Source, fields.Inputs, fields.Commands);
    }
    internal bool AcceptsEvent(BroadcastEnvelope message)
    {
        var input = Controller.Configuration.ControllerOptions.Text(message.Body, "input");
        return input == "device" && message.Event == "device.status" ||
            Inputs.Any(i => i.Name.Equals(input, StringComparison.OrdinalIgnoreCase) && i.Events.Contains(message.Event, StringComparer.OrdinalIgnoreCase));
    }
    internal string Signature => JsonSerializer.Serialize(this);
    private static void ValidateNames(string[] values)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            BroadcastOptions.ValidateName(value, "capability", false);
            if (value != value.Trim() || !names.Add(value)) throw new ArgumentException("Duplicate or invalid serial capability.");
        }
    }
}
