using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PhotoBooth.Networking;

// DNS-SD discovery is read-only. Stored MAC identities are verified through WLED's JSON API.
public static class WledDiscovery
{
    private const string Service = "_wled._tcp.local";
    public static async Task<WledDevice[]> FindAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var catalog = new DnsCatalog();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        var group = IPAddress.Parse("224.0.0.251");
        foreach (var address in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Distinct())
        {
            try
            {
                udp.JoinMulticastGroup(group, address);
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                await udp.SendAsync(Query(), new IPEndPoint(group, 5353), deadline.Token).ConfigureAwait(false);
            }
            catch (SocketException) { } // Continue with other connected interfaces.
        }
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var response = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                try { catalog.Add(response.Buffer); } catch (ArgumentException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        using var client = new WledClient();
        using var limit = new SemaphoreSlim(4);
        var devices = await Task.WhenAll(catalog.Addresses.Take(128).Select(async address =>
        {
            await limit.WaitAsync(token).ConfigureAwait(false);
            try { return await client.ReadAsync(address, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { return null; }
            finally { limit.Release(); }
        })).ConfigureAwait(false);
        return devices.OfType<WledDevice>().DistinctBy(d => d.Mac).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static byte[] Query()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0,0,0,0,0,1,0,0,0,0,0,0 });
        foreach (var part in Service.Split('.')) { stream.WriteByte((byte)part.Length); stream.Write(Encoding.ASCII.GetBytes(part)); }
        stream.Write(new byte[] { 0,0,12,0,1 }); // PTR / IN
        return stream.ToArray();
    }

    public sealed class DnsCatalog
    {
        private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Host, int Port)> _services = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _addresses = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> Addresses => _instances.Where(_services.ContainsKey).SelectMany(instance =>
        {
            var (host, port) = _services[instance];
            return _addresses.TryGetValue(host, out var ips) ? ips.Select(ip => port == 80 ? ip : $"{ip}:{port}") : [];
        }).Distinct();
        public void Add(byte[] packet)
        {
            if (packet.Length < 12 || packet.Length > 9000 || (packet[2] & 0x80) == 0) return;
            int offset = 12, questions = U16(packet, 4), records = U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
            if (questions > 128 || records > 512 || _instances.Count > 256 || _services.Count > 512 || _addresses.Count > 1024) return;
            for (int i = 0; i < questions; i++) { Name(packet, ref offset); Require(packet, offset, 4); offset += 4; }
            for (int i = 0; i < records; i++)
            {
                var name = Name(packet, ref offset); Require(packet, offset, 10);
                int type = U16(packet, offset), length = U16(packet, offset + 8); offset += 10;
                int end = offset + length; Require(packet, offset, length);
                int cursor = offset;
                if (type == 12 && name.Equals(Service, StringComparison.OrdinalIgnoreCase)) _instances.Add(Name(packet, ref cursor));
                else if (type == 33 && length >= 7)
                { int port = U16(packet, cursor + 4); cursor += 6; var host = Name(packet, ref cursor); if (port != 0) _services[name] = (host, port); }
                else if (type == 1 && length == 4)
                {
                    if (!_addresses.TryGetValue(name, out var addresses)) _addresses[name] = addresses = [];
                    var ip = new IPAddress(packet.AsSpan(offset, 4));
                    if (!IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224 && addresses.Count < 8) addresses.Add(ip.ToString());
                }
                if (cursor > end) throw new ArgumentException("DNS record exceeds payload.");
                offset = end;
            }
        }
        private static int U16(byte[] bytes, int at) { Require(bytes, at, 2); return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at)); }
        private static void Require(byte[] bytes, int at, int count) { if (at < 0 || count < 0 || at > bytes.Length - count) throw new ArgumentException("Truncated DNS packet."); }
        private static string Name(byte[] bytes, ref int offset)
        {
            int cursor = offset, resume = -1, jumps = 0;
            var labels = new List<string>();
            while (true)
            {
                Require(bytes, cursor, 1); int length = bytes[cursor++];
                if (length == 0) break;
                if ((length & 0xC0) == 0xC0)
                {
                    Require(bytes, cursor, 1); if (++jumps > 32) throw new ArgumentException("DNS pointer loop.");
                    if (resume < 0) resume = cursor + 1;
                    cursor = ((length & 0x3F) << 8) | bytes[cursor]; continue;
                }
                if (length > 63 || labels.Count > 127) throw new ArgumentException("Invalid DNS label.");
                Require(bytes, cursor, length); labels.Add(Encoding.UTF8.GetString(bytes, cursor, length)); cursor += length;
            }
            offset = resume < 0 ? cursor : resume;
            var name = string.Join('.', labels); if (name.Length > 255) throw new ArgumentException("DNS name too long.");
            return name;
        }
    }
}
