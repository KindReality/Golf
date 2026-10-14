using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ExperienceX;

internal static class GForceControlChecks
{
    internal static async Task Run()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = lifetime.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var commands = new List<string>();
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(token);
                var stream = peer.GetStream();
                for (int i = 0; i < 6; i++)
                {
                    var header = new byte[76]; await stream.ReadExactlyAsync(header, token);
                    Check(header[0] == 2 && header[1] == 1 && header[8] == 1, "Invalid command header");
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    Check(id == i + 1, "Request IDs did not serialize concurrent callers");
                    var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(64))];
                    await stream.ReadExactlyAsync(payload, token);
                    Check(payload[^1] == 0, "Missing command NUL terminator");
                    commands.Add(Encoding.UTF8.GetString(payload[..^1]));
                    var notification = new byte[76]; notification[0] = 2; notification[1] = 2; notification[2] = 41;
                    BinaryPrimitives.WriteInt32BigEndian(notification.AsSpan(64), 3);
                    await stream.WriteAsync(notification, token);
                    await stream.WriteAsync(new byte[] { 0xFF, 0, 0x80 }, token); // Binary data, not UTF-8.
                    // Large Unicode output and short fragments exercise TCP stream reassembly.
                    var text = i == 1 ? string.Concat(Enumerable.Repeat("Azure Ω\n", 1024)) : "reply-" + i;
                    var reply = Encoding.UTF8.GetBytes(text);
                    header[1] = 3; header[8] = 0;
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(64), reply.Length);
                    foreach (var part in header.Chunk(7)) await stream.WriteAsync(part, token);
                    foreach (var part in reply.Chunk(37)) await stream.WriteAsync(part, token);
                }
            }, token);
            using var client = await GForceControlClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(3), token);
            Check((await client.GetThemesAsync(token)).Contains("Ω"), "Fragmented Unicode reply lost content");
            var parallel = await Task.WhenAll(client.GetActiveThemeAsync(token), client.GetColorMapAsync(token));
            Check(parallel[0] == "reply-2" && parallel[1] == "reply-3", "Concurrent replies crossed requests");
            await client.SetColorMapAsync("Blue\"; test\\palette", .5, token);
            await client.SetAudioSensitivityAsync(.75, token);
            await server;
            Check(commands[0] == "SetClientDesc( \"ExperienceX\", 530 );", "Wrong client identification");
            Check(commands[4] == "StartConfig( \"ColorMap\", \"Blue\\\"; test\\\\palette\", 0.5 );", "Palette name was not safely quoted");
            Check(commands[5].Contains("0.75"), "Numeric commands must use invariant culture");
        }
        finally { listener.Stop(); }
        foreach (var fault in new[] { "id", "version", "length", "truncated", "timeout", "cancel" }) await Fault(fault);
        Console.WriteLine("PASS G-Force framing, unsolicited binary notifications, fragmented/large Unicode replies, request serialization, string quoting, malformed replies, timeout and cancellation recovery");
    }

    private static async Task Fault(string fault)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);
                var stream = peer.GetStream();
                for (int i = 0; i < 2; i++)
                {
                    var header = new byte[76]; await stream.ReadExactlyAsync(header, lifetime.Token);
                    var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(64))];
                    await stream.ReadExactlyAsync(payload, lifetime.Token);
                    header[1] = 3; header[8] = 0; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(64), 0);
                    if (i == 1)
                    {
                        if (fault is "timeout" or "cancel") { await Task.Delay(400, lifetime.Token); return; }
                        if (fault == "id") header[4]++;
                        if (fault == "version") header[0] = 99;
                        if (fault == "length") BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(64), int.MaxValue);
                        if (fault == "truncated") { await stream.WriteAsync(header.AsMemory(0, 20), lifetime.Token); return; }
                    }
                    await stream.WriteAsync(header, lifetime.Token);
                }
            });
            using var client = await GForceControlClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromMilliseconds(250));
            using var cancel = new CancellationTokenSource();
            if (fault == "cancel") cancel.CancelAfter(80);
            try { await client.GetColorMapAsync(cancel.Token); throw new Exception("Malformed exchange succeeded: " + fault); }
            catch (Exception ex) when (fault switch
            {
                "timeout" => ex is TimeoutException,
                "cancel" => ex is OperationCanceledException,
                "truncated" => ex is EndOfStreamException,
                _ => ex is InvalidDataException
            }) { }
            try { await client.GetColorMapAsync(); throw new Exception("Damaged connection was reused"); }
            catch (ObjectDisposedException) { }
            await server;
        }
        finally { listener.Stop(); }
    }
    private static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
}
