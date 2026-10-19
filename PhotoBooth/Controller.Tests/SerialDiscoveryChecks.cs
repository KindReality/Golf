using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Hardware;
using Controller.Networking;
using PhotoBooth.Networking;

internal static class SerialDiscoveryChecks
{
    internal static void CheckFirmware(string path)
    {
        SerialDeviceAnnouncement? announcement = null;
        var count = 0;
        foreach (var line in File.ReadLines(path).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var message = BroadcastEnvelope.Parse(Encoding.UTF8.GetBytes(line));
            if (message.Event == SerialDeviceAnnouncement.EventName) announcement = SerialDeviceAnnouncement.Parse(message);
            else if (announcement is null || !announcement.AcceptsEvent(message)) throw new ArgumentException("Firmware event does not match its capabilities.");
            ++count;
        }
        Check(announcement is not null && announcement.Inputs.Length == 3 && count > 0, $"All {count} actual firmware messages match Controller's envelope and capability parser");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> condition, int timeout = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Serial test condition not reached."); await Task.Delay(5); }
    }
    private static byte[] Announcement(string name = "mega1", string id = "board-001", int version = 1) =>
        BroadcastEnvelope.Create(new() { DeviceName = name }, "event", "*", new
        {
            input = "device", @event = "device.announced", protocol = "saft.serial", protocolVersion = version, deviceId = id,
            inputs = new[] { new { name = "beam1", kind = "breakBeam", events = new[] { "beam.broken", "beam.restored" } } },
            commands = new[] { "discover", "status" }
        });
    private static byte[] Beam(string source = "mega1", string input = "beam1") =>
        BroadcastEnvelope.Create(new() { DeviceName = source }, "event", "*", new { input, @event = "beam.broken" });
    private static byte[] Command(string command = "status") =>
        BroadcastEnvelope.Create(new() { DeviceName = "controller" }, "command", "mega1", new { command });

    internal static async Task Run()
    {
        var log = new ActivityLog();
        var received = new ConcurrentQueue<byte[]>();
        var links = new ConcurrentDictionary<string, FakeLink>();
        UsbSerialPort[] inventory = [];
        var opened = new ConcurrentQueue<string>();
        await using var devices = new ArduinoConnections(log, (bytes, _, _) => { received.Enqueue(bytes); return Task.CompletedTask; },
            () => inventory, (port, baud, dtr) =>
            { Check(baud == 115200 && dtr, "Discovery uses agreed serial settings"); opened.Enqueue(port); return links[port]; }, monitor: false);
        devices.Apply([], new()); devices.Poll();
        Check(opened.IsEmpty, "No configured boards or USB ports means nothing is opened");

        var first = new FakeLink(); links["COM10"] = first;
        inventory = [new("COM10", @"USB\VID_2341&PID_0042\001")]; devices.Poll();
        await Until(() => devices.Snapshot().Any(s => s.Status == "Waiting for announcement"));
        first.Push(Beam()); first.Push(Announcement(version: 2));
        await Until(() => first.Pending == 0);
        Check(received.IsEmpty && !devices.Snapshot().Single().Connected, "Events and incompatible announcements cannot register an unknown port");
        first.Push(Announcement());
        await Until(() => devices.Snapshot().Any(s => s.Connected && s.Name == "mega1"));
        Check(devices.HasTarget("MEGA1") && !devices.HasTarget("other"), "Announced board can be resolved as a named network destination");
        Check(devices.Snapshot().Single().Capabilities!.Commands.SequenceEqual(["discover", "status"]), "Valid announcement exposes identity and supported commands");
        Check(received.IsEmpty, "Registration is not forwarded as a sensor trigger");
        first.Push(Beam()); await Until(() => received.Count == 1);
        first.Push(Beam(source: "impostor")); first.Push(Beam(input: "unadvertised"));
        await Until(() => log.Snapshot().Count(e => e.Message.StartsWith("ArduinoMessageRejected")) >= 2);
        Check(received.Count == 1, "Only the registered source and advertised events are accepted");
        var refused = false;
        try { await devices.SendAsync("mega1", Command("switch"), CancellationToken.None); }
        catch (ArgumentException) { refused = true; }
        Check(refused, "Unadvertised command is rejected without writing to the board");
        var dispatcher = new CommandDispatcher(devices, (_, _) => Task.CompletedTask, CancellationToken.None);
        var command = new PlaylistCommand { Transport = "arduino", Device = "mega1", Payload = JsonSerializer.SerializeToElement(new { command = "status" }) };
        await dispatcher.SendAsync(command, new ControllerOptions(), BroadcastChain.Root("check"), "serial-command", CancellationToken.None);
        Check(first.Writes.Count == 1 && BroadcastEnvelope.Parse(first.Writes.Single()).Device == "mega1", "Dispatcher routes by announced name without a predefined Arduino entry");

        var second = new FakeLink(); links["COM11"] = second;
        inventory = [.. inventory, new("COM11", @"USB\VID_2341&PID_0042\002")]; devices.Poll(); second.Push(Announcement(id: "board-002"));
        await Until(() => devices.Snapshot().Count(s => s.Status == "Conflicting device name or ID") == 2);
        refused = false;
        try { await devices.SendAsync("mega1", Command(), CancellationToken.None); }
        catch (IOException) { refused = true; }
        Check(refused, "Duplicate aliases block both devices instead of choosing one");
        first.Push(Beam()); second.Push(Beam()); await Until(() => first.Pending == 0 && second.Pending == 0);
        Check(received.Count == 1, "Ambiguous devices cannot send sensor events");
        inventory = [inventory[0]]; devices.Poll();
        await Until(() => devices.Snapshot().Single().Connected);
        first.Push(Beam()); await Until(() => received.Count == 2);
        Check(true, "Removing the conflicting board restores the unambiguous connection");

        inventory = []; devices.Poll();
        Check(devices.Snapshot().Length == 0, "Unplug removes the runtime registration");
        var moved = new FakeLink(); links["COM12"] = moved;
        inventory = [new("COM12", @"USB\VID_2341&PID_0042\001")]; devices.Poll(); moved.Push(Announcement());
        await Until(() => devices.Snapshot().Any(s => s.Connected && s.Port == "COM12"));
        await devices.SendAsync("mega1", Command(), CancellationToken.None);
        Check(moved.Writes.Count == 1, "Same device reconnects on a different COM port without configuration changes");
        var unknown = new FakeLink(); links["COM13"] = unknown;
        inventory = [.. inventory, new("COM13", @"USB\VID_0001&PID_0001\unknown")]; devices.Poll();
        await Until(() => unknown.Disposed, 6500);
        var openCount = opened.Count; devices.Poll();
        Check(opened.Count == openCount && !devices.HasTarget("USB device"), "Unrecognized USB port is released after the handshake timeout and is not immediately reprobed");
        devices.Apply([], new() { Enabled = false }); devices.Poll();
        Check(devices.Snapshot().Length == 0, "Disabling discovery hot-reloads and releases discovered ports");

        var defaults = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.defaults.json"));
        var options = ControllerOptions.Parse(defaults, new("Development", 1000));
        var offline = options with { Playlists = [.. options.Playlists, new() { Name = "offline-board", Items = [new() { Name = "status", Commands = [command] }] }] };
        ControllerOptions.Parse(JsonSerializer.Serialize(offline, ControllerOptions.Json), offline.Build);
        Check(true, "Playlists can be configured before their named serial device is connected");

        // Native USB filtering is exercised read-only; this never opens a physical port.
        var native = UsbSerialPorts.Enumerate();
        Check(native.All(p => p.Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && p.InstanceId.Length > 0),
            $"Native USB-only serial inventory succeeded ({native.Length} USB ports; none opened)");
        var routed = new ConcurrentQueue<BroadcastEnvelope>();
        await using var listener = new UdpBroadcastListener(() => new() { DeviceName = "controller" },
            (message, _) => { routed.Enqueue(message); return Task.CompletedTask; }, name => name == "mega1");
        listener.Bind(0);
        using var socket = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        await socket.SendAsync(Command(), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, listener.Port));
        await Until(() => routed.Count == 1);
        Check(routed.Single().Device == "mega1", "UDP listener accepts a board's named destination for the Controller bridge");
    }

    private sealed class FakeLink : ISerialLink
    {
        private readonly BlockingCollection<byte> _input = new();
        public readonly ConcurrentQueue<byte[]> Writes = new();
        private bool _disposed;
        internal bool Disposed => _disposed;
        internal int Pending => _input.Count;
        internal void Push(byte[] message) { foreach (var value in message) _input.Add(value); _input.Add((byte)'\n'); }
        public byte ReadByte()
        {
            if (_disposed) throw new IOException("Disconnected");
            if (_input.TryTake(out var value, 10)) return value;
            throw new TimeoutException();
        }
        public void Write(byte[] bytes) { if (_disposed) throw new IOException("Disconnected"); Writes.Enqueue(bytes); }
        public void Dispose() => _disposed = true;
    }
}
