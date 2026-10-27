using System.Text.Json;

namespace PhotoBooth.Networking;

internal static class ControllerDiscovery
{
    public const string ResponseEvent = "controller.announced";
    internal sealed record Announcement(string ControllerId, string Hostname, string Revision, int DefinitionsPort, string MediaRevision);
    public static Announcement? ReadAnnouncement(BroadcastEnvelope message)
    {
        if (message.Type != "event" || message.Event != ResponseEvent) return null;
        var id = Text(message.Body, "controllerId"); var hostname = Text(message.Body, "hostname"); var revision = Text(message.Body, "revision");
        var mediaRevision = Text(message.Body, "mediaRevision");
        ValidateHostname(hostname);
        if (!Guid.TryParse(id, out _) || revision is not { Length: 64 } || !revision.All(Uri.IsHexDigit) ||
            mediaRevision is not { Length: 64 } || !mediaRevision.All(Uri.IsHexDigit) ||
            !message.Body.TryGetProperty("definitionsPort", out var field) || !field.TryGetInt32(out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Invalid controller announcement identity, revision or port.");
        return new(id!, hostname!, revision, port, mediaRevision);
    }
    private static string? Text(JsonElement body, string field) => body.EnumerateObject()
        .FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static bool IsRequest(BroadcastEnvelope message) => message.Type == "command" &&
        Text(message.Body, "command") == "discover" && Text(message.Body, "service") == "controller";
    public static byte[] Request(BroadcastOptions options, string requestId) => BroadcastEnvelope.Create(options, "command", "*",
        new { command = "discover", service = "controller" }, requestId);
    public static byte[] Response(BroadcastOptions options, BroadcastEnvelope? request = null, string? controllerId = null,
        string? revision = null, int definitionsPort = 54244, string? mediaRevision = null) => BroadcastEnvelope.Create(options, "event", "*",
        new { @event = ResponseEvent, input = "controller", hostname = Environment.MachineName,
            controllerId, revision, definitionsPort, mediaRevision = mediaRevision ?? PhotoBooth.Media.MediaInventory.EmptyRevision });
    public static string? Hostname(BroadcastEnvelope message, IReadOnlySet<string> requested)
    {
        if (message.Type != "event" || message.Event != ResponseEvent) return null;
        var hostname = Text(message.Body, "hostname");
        ValidateHostname(hostname);
        return hostname;
    }
    public static string MediaShare(string hostname)
    {
        ValidateHostname(hostname);
        return @"\\" + hostname + @"\media";
    }
    private static void ValidateHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Length > 253 || hostname is "." or ".." || hostname.StartsWith('.') || hostname.EndsWith('.') ||
            hostname.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Discovery hostname must be a hostname, without a path or share name.");
    }
}
