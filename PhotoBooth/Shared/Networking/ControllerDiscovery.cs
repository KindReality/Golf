using System.Text.Json;

namespace PhotoBooth.Networking;

internal static class ControllerDiscovery
{
    public const string ResponseEvent = "controller.discovered";
    private static string? Text(JsonElement body, string field) => body.EnumerateObject()
        .FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static bool IsRequest(BroadcastEnvelope message) => message.Type == "command" &&
        Text(message.Body, "command") == "discover" && Text(message.Body, "service") == "controller";
    public static byte[] Request(BroadcastOptions options, string requestId) => BroadcastEnvelope.Create(options, "command", "*",
        new { command = "discover", service = "controller" }, requestId);
    public static byte[] Response(BroadcastOptions options, BroadcastEnvelope request) => BroadcastEnvelope.Create(options, "event", request.Source,
        new { @event = ResponseEvent, input = "controller", hostname = Environment.MachineName, discoveryRequestId = request.RequestId });
    public static string? Hostname(BroadcastEnvelope message, IReadOnlySet<string> requested)
    {
        if (message.Type != "event" || message.Event != ResponseEvent || !requested.Contains(Text(message.Body, "discoveryRequestId") ?? "")) return null;
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
