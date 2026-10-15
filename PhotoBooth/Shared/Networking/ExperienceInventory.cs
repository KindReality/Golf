using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoBooth.Networking;

public sealed record ExperienceScreen
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Display { get; init; } = "";
    public string DisplayLabel { get; init; } = "";
    public string Kind { get; init; } = "video";
    public string Roles { get; init; } = "";
    public bool Available { get; init; }
    public bool MediaBound { get; init; }
    public bool Playable => Kind == "video" && Available && !MediaBound;
}
public sealed record ExperienceSpeaker
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Channels { get; init; }
    public bool Default { get; init; }
    public string Roles { get; init; } = "";
}
public sealed record ExperienceInventory
{
    public int SchemaVersion { get; init; } = 1;
    public string InstanceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Hostname { get; init; } = "";
    public ExperienceScreen[] Screens { get; init; } = [];
    public ExperienceSpeaker[] Speakers { get; init; } = [];
    public string[] VideoFiles { get; init; } = [];
    public string[] AudioFiles { get; init; } = [];
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public void Validate()
    {
        if (SchemaVersion != 1 || !Guid.TryParse(InstanceId, out _)) throw new ArgumentException("Unsupported Experience inventory.");
        BroadcastOptions.ValidateName(DeviceName, "deviceName", false);
        ControllerDiscovery.MediaShare(Hostname);
        if (Screens is null || Speakers is null || VideoFiles is null || AudioFiles is null || Screens.Length > 1024 || Speakers.Length > 128 || VideoFiles.Length > 4096 || AudioFiles.Length > 4096)
            throw new ArgumentException("Experience inventory exceeds supported counts.");
        var screens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in Screens)
        {
            if (screen is null || !Text(screen.Id, 128) || !Text(screen.Name, 80) || screen.Name.Contains('/') ||
                !Text(screen.Display, 256) || !Text(screen.DisplayLabel, 256) || !screens.Add(screen.Display + "/" + screen.Id) ||
                screen.Kind is not ("video" or "mat" or "visualization" or "fastled")) throw new ArgumentException("Invalid or duplicate inventory screen.");
            RoleList.Parse(screen.Roles);
        }
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in Speakers)
        {
            if (speaker is null || !Text(speaker.Id, 512) || !Text(speaker.Name, 512) || speaker.Channels is < 1 or > 128 || !speakers.Add(speaker.Id))
                throw new ArgumentException("Invalid or duplicate inventory speaker.");
            RoleList.Parse(speaker.Roles);
        }
        foreach (var files in new[] { VideoFiles, AudioFiles })
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var relative = PhotoBooth.Media.MediaLibrary.Normalize(file);
                if (!names.Add(relative)) throw new ArgumentException("Duplicate inventory media path.");
            }
        }
    }
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum && !value.Any(char.IsControl);
    public byte[] Serialize() { Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json); if (bytes.Length > MaximumBytes) throw new ArgumentException("Experience inventory exceeds 4 MB."); return bytes; }
    public static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
internal static class ExperienceDiscovery
{
    public const string Event = "experience.announced";
    private static string? Text(JsonElement body, string key) => body.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static bool IsRequest(BroadcastEnvelope message) => message.Type == "command" && Text(message.Body, "command") == "discover" && Text(message.Body, "service") == "experience";
    public static byte[] Request(BroadcastOptions options) => BroadcastEnvelope.Create(options, "command", "*", new { command = "discover", service = "experience" });
    public static byte[] Announce(BroadcastOptions options, string instanceId, string revision, int port) => BroadcastEnvelope.Create(options, "event", "*",
        new { @event = Event, input = "experience", hostname = Environment.MachineName, instanceId, revision, inventoryPort = port });
    public static (string Hostname, string InstanceId, string Revision, int Port)? Read(BroadcastEnvelope message)
    {
        if (message.Type != "event" || message.Event != Event) return null;
        var host = Text(message.Body, "hostname"); var instance = Text(message.Body, "instanceId"); var revision = Text(message.Body, "revision");
        ControllerDiscovery.MediaShare(host ?? "");
        var field = message.Body.EnumerateObject().FirstOrDefault(p => p.Name.Equals("inventoryPort", StringComparison.OrdinalIgnoreCase)).Value;
        if (!Guid.TryParse(instance, out _) || revision is not { Length: 64 } || revision.Any(c => !Uri.IsHexDigit(c)) || field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Invalid Experience inventory announcement.");
        return (host!, instance!, revision, port);
    }
}
