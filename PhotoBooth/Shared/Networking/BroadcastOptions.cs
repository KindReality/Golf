using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace PhotoBooth.Networking;

public record BroadcastOptions
{
    public string? DeviceName { get; init; }
    public int UdpPort { get; init; } = 21325;
    public string UdpBroadcastAddress { get; init; } = "255.255.255.255";
    public string? UdpInterfaceAddress { get; init; }
    public int UdpSendAttempts { get; init; } = 3;
    public int UdpRetryDelayMilliseconds { get; init; } = 75;
    public double RequestLifetimeSeconds { get; init; } = 10;
    public double RequestDeduplicationSeconds { get; init; } = 60;
    [JsonIgnore]
    public string EffectiveDeviceName => string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();

    public void ValidateNetwork()
    {
        ValidateName(EffectiveDeviceName, "DeviceName", false);
        if (UdpPort is < 1 or > 65535 || UdpSendAttempts is < 1 or > 5 ||
            UdpRetryDelayMilliseconds is < 20 or > 1000 ||
            !double.IsFinite(RequestLifetimeSeconds) || RequestLifetimeSeconds is <= 0 or > 300 ||
            !double.IsFinite(RequestDeduplicationSeconds) || RequestDeduplicationSeconds is <= 0 or > 3600 ||
            RequestDeduplicationSeconds < RequestLifetimeSeconds ||
            RequestLifetimeSeconds * 1000 <= (UdpSendAttempts - 1) * UdpRetryDelayMilliseconds)
            throw new ArgumentException("Invalid UDP port, retries, request lifetime or duplicate retention. Retention must cover the request lifetime.");
        if (!IPAddress.TryParse(UdpBroadcastAddress, out var broadcast) || broadcast.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(broadcast) || broadcast.Equals(IPAddress.Any) || broadcast.GetAddressBytes()[0] >= 224 && !broadcast.Equals(IPAddress.Broadcast))
            throw new ArgumentException("UdpBroadcastAddress must be an IPv4 broadcast address for the dedicated subnet.");
        if (!string.IsNullOrWhiteSpace(UdpInterfaceAddress) &&
            (!IPAddress.TryParse(UdpInterfaceAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork ||
             local.Equals(IPAddress.Any) || local.Equals(IPAddress.Broadcast)))
            throw new ArgumentException("UdpInterfaceAddress must be a local IPv4 interface address or empty for automatic selection.");
    }

    internal static void ValidateName(string? name, string field, bool wildcard)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl) || !wildcard && name.Trim() == "*")
            throw new ArgumentException($"{field} requires a nonempty name of up to 128 characters{(wildcard ? " or *" : "")}.");
    }
}
