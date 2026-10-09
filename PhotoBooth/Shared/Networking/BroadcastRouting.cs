using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhotoBooth.Networking;

internal sealed record BroadcastInterface(string Name, IPAddress Address, IPAddress Mask);
internal sealed record BroadcastRoute(string? Name, IPAddress? Address, IPAddress Destination);

internal static class BroadcastRouting
{
    public static BroadcastInterface[] Inventory() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is not null)
            .Select(address => new BroadcastInterface(adapter.Name, address.Address, address.IPv4Mask)))
        .ToArray();

    public static BroadcastRoute[] Select(BroadcastOptions options, IReadOnlyList<BroadcastInterface> inventory)
    {
        var destination = IPAddress.Parse(options.UdpBroadcastAddress);
        var explicitAddress = string.IsNullOrWhiteSpace(options.UdpInterfaceAddress) ? null : IPAddress.Parse(options.UdpInterfaceAddress);
        if (!destination.Equals(IPAddress.Broadcast))
            return [new(null, explicitAddress, destination)];
        var routes = inventory
            .Where(item => !IPAddress.IsLoopback(item.Address) && !item.Address.Equals(IPAddress.Any) &&
                !item.Mask.Equals(IPAddress.Any) && item.Mask.GetAddressBytes()[3] < 254 &&
                (explicitAddress is null || item.Address.Equals(explicitAddress)))
            .Select(item => new BroadcastRoute(item.Name, item.Address,
                new IPAddress(item.Address.GetAddressBytes().Zip(item.Mask.GetAddressBytes(), (address, mask) => (byte)(address | ~mask)).ToArray())))
            .DistinctBy(route => route.Address).ToArray();
        if (routes.Length > 0) return routes;
        if (explicitAddress is not null) return [new(null, explicitAddress, destination)];
        throw new InvalidOperationException("No active IPv4 network adapter with a broadcast subnet is available.");
    }
}
