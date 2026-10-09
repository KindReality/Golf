using System.Net;
using System.Net.Sockets;
using PhotoBooth.Diagnostics;

namespace PhotoBooth.Networking;

internal static class UdpBroadcastSender
{
    public static async Task SendAsync(byte[] bytes, BroadcastOptions options, CancellationToken token = default)
    {
        options.ValidateNetwork();
        var message = BroadcastEnvelope.Parse(bytes);
        // A limited broadcast with OS routing can choose a virtual/VPN adapter instead of the LAN.
        // Bind each active IPv4 interface and use its subnet broadcast; retries share one request ID.
        var routes = BroadcastRouting.Select(options, BroadcastRouting.Inventory());
        SocketException? failure = null;
        var sent = false;
        for (var attempt = 0; attempt < options.UdpSendAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (message.IsExpired(DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The request expired before all send attempts completed.");
            foreach (var route in routes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                    if (route.Address is not null) client.Client.Bind(new IPEndPoint(route.Address, 0));
                    var destination = new IPEndPoint(route.Destination, options.UdpPort);
                    await client.SendAsync(bytes, destination, token).ConfigureAwait(false);
                    sent = true;
                    Telemetry.Info("UdpBroadcastSent", new
                    {
                        Destination = destination.ToString(),
                        Interface = route.Address?.ToString(),
                        InterfaceName = route.Name,
                        message.Type, message.Device, message.Source, message.Event,
                        Bytes = bytes.Length,
                        Attempt = attempt + 1
                    }, message.RequestId);
                }
                catch (SocketException ex)
                {
                    failure = ex;
                    Telemetry.Warning("UdpBroadcastInterfaceFailed", new
                    {
                        Interface = route.Address?.ToString(), InterfaceName = route.Name,
                        Destination = route.Destination.ToString(), Attempt = attempt + 1,
                        Reason = ex.Message, ex.SocketErrorCode
                    }, message.RequestId);
                }
            }
            if (attempt + 1 < options.UdpSendAttempts)
                await Task.Delay(options.UdpRetryDelayMilliseconds, token).ConfigureAwait(false);
        }
        if (!sent) throw failure ?? new SocketException((int)SocketError.NetworkUnreachable);
    }
}
