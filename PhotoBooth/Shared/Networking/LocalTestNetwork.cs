using System.Net;
using System.Net.Sockets;

namespace PhotoBooth.Networking;

// Explicit, process-local integration-test opt-in. Never stored in application configuration.
// Separate loopback addresses let both applications receive the same test datagrams
// without broadcasting to real devices or relying on shared unicast socket delivery.
internal static class LocalTestNetwork
{
    public const string Variable = "SAFT_LOCAL_TEST_ADDRESS";
    public static IPAddress? Address
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrEmpty(value)) return null;
            if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(address))
                throw new InvalidOperationException(Variable + " must be an IPv4 loopback address.");
            return address;
        }
    }
    public static IPAddress ListenAddress => Address ?? IPAddress.Any;
    public static BroadcastRoute[] Routes(IPAddress address) =>
        [new("local Controller test", address, IPAddress.Parse("127.0.0.2")),
         new("local Experience test", address, IPAddress.Parse("127.0.0.3"))];
}
