using System.Text.Json;
using PhotoBooth.Networking;

namespace Controller.Configuration;

internal sealed record PinnedActionOptions
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? EventSource { get; init; }
    public required PlaylistCommand Command { get; init; }

    internal static string Signature(PinnedActionOptions? value) => JsonSerializer.Serialize(value, ControllerOptions.Json);
    internal void Validate(ControllerOptions options)
    {
        if (!Guid.TryParseExact(Id, "N", out _)) throw new ArgumentException("Pinned actions require a stable ID.");
        BroadcastOptions.ValidateName(Name, "pinned action name", false);
        if (Name != Name.Trim()) throw new ArgumentException("Trim the pinned action name.");
        ValidateCommand(Command, options, EventSource);
    }
    internal static void ValidateCommand(PlaylistCommand command, ControllerOptions options, string? eventSource = null)
    {
        if (command is null) throw new ArgumentException("Choose an action to send.");
        if (eventSource is not null)
        {
            if (command.Type != "event") throw new ArgumentException("Source device overrides apply only to events.");
            BroadcastOptions.ValidateName(eventSource, "event source", false);
        }
        if (command.Playlist is { } playlist)
        {
            BroadcastOptions.ValidateName(playlist, "playlist", false);
            if (command.Device is not null || command.Payload.ValueKind != JsonValueKind.Undefined || command.Type != "command" || command.Transport != "udp")
                throw new ArgumentException("Local playlist actions cannot include destination or payload fields.");
            return; // Literal references may be temporarily unavailable after a show edit.
        }
        if (command.Transport is not ("udp" or "arduino") || command.Type is not ("command" or "event") || command.Payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Choose a valid command or event.");
        if (command.Transport == "arduino" && (command.Type != "command" || command.Device == "*" || string.IsNullOrWhiteSpace(ControllerOptions.Text(command.Payload, "command"))))
            throw new ArgumentException("Arduino commands require a named device and command.");
        if (command.Type == "event") ControllerOptions.RequireInput(command.Payload);
        if (ControllerOptions.Text(command.Payload, "experience") is "video" or "audio")
            PhotoBooth.Media.MediaLibrary.Normalize(ControllerOptions.Text(command.Payload, "value") ?? "");
        BroadcastEnvelope.Create(options with { DeviceName = eventSource ?? options.EffectiveDeviceName }, command.Type, command.Device!, command.Payload);
    }
}
