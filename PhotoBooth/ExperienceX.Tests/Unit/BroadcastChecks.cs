using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhotoBooth.Networking;

internal static class BroadcastChecks
{
    public static async Task Run()
    {
        static void Reject(string json)
        {
            try
            {
                BroadcastEnvelope.Parse(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException) { return; }
            throw new Exception("Unsafe broadcast envelope accepted: " + json);
        }
        var defaults = new BroadcastOptions();
        defaults.ValidateNetwork();
        BroadcastInterface[] interfaces = [
            new("virtual", IPAddress.Parse("192.168.56.1"), IPAddress.Parse("255.255.255.0")),
            new("LAN", IPAddress.Parse("192.168.1.42"), IPAddress.Parse("255.255.255.0")),
            new("offline LAN", IPAddress.Parse("10.20.3.10"), IPAddress.Parse("255.255.0.0")),
            new("loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"))];
        var routes = BroadcastRouting.Select(defaults, interfaces);
        if (!routes.Select(route => route.Destination.ToString()).SequenceEqual(["192.168.56.255", "192.168.1.255", "10.20.255.255"]))
            throw new Exception("Automatic broadcasts did not include physical and offline LANs alongside virtual adapters.");
        var pinned = BroadcastRouting.Select(defaults with { UdpInterfaceAddress = "192.168.1.42" }, interfaces);
        if (pinned.Length != 1 || pinned[0].Destination.ToString() != "192.168.1.255" || pinned[0].Address?.ToString() != "192.168.1.42")
            throw new Exception("Explicit interface selection was not retained.");
        if (BroadcastRouting.Select(defaults with { UdpBroadcastAddress = "10.0.0.255" }, interfaces).Single().Destination.ToString() != "10.0.0.255")
            throw new Exception("Explicit broadcast destination was overridden.");
        if (defaults.EffectiveDeviceName != Environment.MachineName ||
            (defaults with
            {
                DeviceName = "  projector-pc  "
            }).EffectiveDeviceName != "projector-pc")
            throw new Exception("Device alias/hostname fallback failed.");
        foreach (var invalid in new[] { defaults with { DeviceName = "*" }, defaults with { UdpSendAttempts = 6 },
            defaults with { RequestDeduplicationSeconds = 1 }, defaults with { UdpBroadcastAddress = "224.0.0.1" },
            defaults with { UdpInterfaceAddress = "not-an-IP" }, defaults with { RequestLifetimeSeconds = double.NaN } })
        {
            try
            {
                invalid.ValidateNetwork();
            }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid broadcast options accepted.");
        }
        Reject("{}");
        Reject("null");
        Reject("{\"device\":\"*\",\"source\":\"sensor\"}");
        Reject("{\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"id\",\"type\":\"event\"}");
        Reject("{\"device\":\"*\",\"Device\":\"other\",\"source\":\"sensor\",\"requestId\":\"id\"}");
        Reject("{\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"id\",\"hopCount\":9}");
        Reject("{\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"" + new string('a', 129) + "\"}");
        Reject("{\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"id\",\"data\":\"" + new string('a', 1200) + "\"}");
        var sample = BroadcastEnvelope.Parse(BroadcastEnvelope.Create(defaults, "command", " LEFT ", new
        {
            experience = "video",
            value = "ping.mp4"
        }));
        if (!sample.Targets("left") || sample.Targets("right") || sample.IsExpired(DateTimeOffset.UtcNow) ||
            sample.IsExpired(DateTimeOffset.UtcNow.AddDays(365)) ||
            !sample.IsExpired(DateTimeOffset.UtcNow, sample.ReceivedTimestamp + 11 * Stopwatch.Frequency))
            throw new Exception("Command targeting/deadline failed.");
        Reject("{\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"id\",\"processingTimeoutSeconds\":-1}");

        // Use the same shared sender Camera uses and real subnet broadcasts, not loopback unicasts.
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is not null && !IPAddress.IsLoopback(address.Address))
            ?? throw new Exception("A local IPv4 network interface is required for the broadcast integration check.");
        var local = address.Address.GetAddressBytes();
        var mask = address.IPv4Mask.GetAddressBytes();
        var broadcast = new IPAddress(local.Zip(mask, (value, subnet) => (byte)(value | ~subnet)).ToArray()).ToString();
        var left = defaults with
        {
            DeviceName = "left"
        };
        var right = defaults with
        {
            DeviceName = "right"
        };
        var receivedLeft = new ConcurrentQueue<BroadcastEnvelope>();
        var receivedRight = new ConcurrentQueue<BroadcastEnvelope>();
        await using var first = new UdpBroadcastListener(() => left, (message, _) => { receivedLeft.Enqueue(message); return Task.CompletedTask; });
        await using var second = new UdpBroadcastListener(() => right, (message, _) => { receivedRight.Enqueue(message); return Task.CompletedTask; });
        first.Bind(0);
        second.Bind(first.Port);
        var sender = defaults with
        {
            DeviceName = "sensor",
            UdpPort = first.Port,
            UdpBroadcastAddress = broadcast,
            UdpInterfaceAddress = address.Address.ToString()
        };
        async Task Send(string type, string target, object body, string id)
            => await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, type, target, body, id), sender);
        await Send("event", "*", new
        {
            @event = "proximity.enter",
            data = new
            {
                distance = 20
            }
        }, "broadcast-event");
        await Until(() => receivedLeft.Count == 1 && receivedRight.Count == 1);
        await Send("command", " LeFt ", new
        {
            experience = "video",
            value = "ping.mp4"
        }, "left-only");
        await Until(() => receivedLeft.Count == 2);
        if (receivedRight.Count != 1)
            throw new Exception("A peer executed another device's command.");
        // Retry using a different IP and port: the logical source, not the transport endpoint, identifies a retry.
        using var retry = new UdpClient();
        await retry.SendAsync(BroadcastEnvelope.Create(sender, "command", "left", new
        {
            experience = "video",
            value = "ping.mp4"
        }, "left-only"),
            new IPEndPoint(IPAddress.Loopback, first.Port));
        await Task.Delay(150);
        if (receivedLeft.Count != 2)
            throw new Exception("Changing transport endpoints defeated duplicate suppression.");
        right = right with
        {
            DeviceName = "renamed"
        };
        await Send("command", "right", new
        {
            experience = "video",
            value = "ping.mp4"
        }, "old-name");
        await Send("command", "RENAMED", new
        {
            experience = "audio",
            value = "ping.mp3",
            volume = 50
        }, "new-name");
        await Until(() => receivedRight.Count == 2);
        if (receivedRight.Last().RequestId != "new-name")
            throw new Exception("Live alias routing failed.");
        var expired = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "command",
            device = "*",
            source = "sensor",
            requestId = "expired",
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            experience = "video",
            value = "ping.mp4"
        });
        using var expiredSender = new UdpClient() { EnableBroadcast = true };
        await expiredSender.SendAsync(expired, new IPEndPoint(IPAddress.Parse(broadcast), first.Port));
        await Task.Delay(150);
        if (receivedLeft.Count != 2 || receivedRight.Count != 2)
            throw new Exception("An expired command executed.");
        using (var noReply = new CancellationTokenSource(150))
        {
            try
            {
                await expiredSender.ReceiveAsync(noReply.Token);
                throw new Exception("Broadcast listener sent a reply.");
            }
            catch (OperationCanceledException) when (noReply.IsCancellationRequested) { }
        }
        var automatic = defaults with
        {
            UdpPort = first.Port,
            DeviceName = "automatic-sensor"
        };
        await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(automatic, "event", "*", new
        {
            @event = "default-route"
        }), automatic);
        await Until(() => receivedLeft.Count == 3 && receivedRight.Count == 3);
        Console.WriteLine($"Passed broadcast routing across virtual/physical/offline LANs, explicit overrides, real UDP broadcasts on {broadcast}, multiple receivers, wildcard routing and retry suppression without replies.");
    }

    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Broadcast did not reach the expected peers.");
            await Task.Delay(20);
        }
    }
}
