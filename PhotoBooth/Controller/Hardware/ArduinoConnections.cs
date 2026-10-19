using System.Diagnostics;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Hardware;

internal sealed record ArduinoStatus(string Name, string Port, bool Connected, string Status)
{
    public SerialDeviceAnnouncement? Capabilities { get; init; }
}

internal sealed class ArduinoConnections : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Connection> _retired = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ActivityLog _log;
    private readonly Func<byte[], string, CancellationToken, Task> _received;
    private readonly Func<UsbSerialPort[]> _enumerate;
    private readonly Func<string, int, bool, ISerialLink> _open;
    private readonly bool _monitorEnabled;
    private ArduinoOptions[] _configured = [];
    private ArduinoDiscoveryOptions _discovery = new();
    private Task? _monitor;
    private string? _inventoryFailure;

    internal ArduinoConnections(ActivityLog log, Func<byte[], string, CancellationToken, Task> received,
        Func<UsbSerialPort[]>? enumerate = null, Func<string, int, bool, ISerialLink>? open = null, bool monitor = true)
    {
        _log = log; _received = received; _enumerate = enumerate ?? UsbSerialPorts.Enumerate;
        _open = open ?? ((port, baud, dtr) => new SerialLink(port, baud, dtr)); _monitorEnabled = monitor;
    }

    public void Apply(ArduinoOptions[] options, ArduinoDiscoveryOptions discovery)
    {
        lock (_gate)
        {
            _configured = options; _discovery = discovery;
            if (_monitorEnabled) _monitor ??= Task.Run(MonitorAsync);
        }
    }
    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do { Poll(); } while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    // Testable without opening real hardware; the native enumerator returns USB ports only.
    internal void Poll()
    {
        ArduinoOptions[] configured; ArduinoDiscoveryOptions discovery;
        lock (_gate) { configured = _configured; discovery = _discovery; }
        UsbSerialPort[] candidates = [];
        if (discovery.Enabled)
        {
            try { candidates = _enumerate(); _inventoryFailure = null; }
            catch (Exception ex)
            {
                if (_inventoryFailure != ex.Message) _log.Error("UsbSerialInventoryFailed", ex);
                _inventoryFailure = ex.Message;
                return; // Retain connections if an inventory query fails transiently.
            }
        }
        var desired = configured.Where(o => o.Enabled).Select(o => new Settings(o.Port, o.BaudRate, o.DtrEnable, o, ""))
            .Concat(candidates.Where(p => !configured.Any(o => Same(o.Port, p.Port)))
                .Take(128).Select(p => new Settings(p.Port, discovery.BaudRate, true, null, p.InstanceId)))
            .ToDictionary(s => s.Port, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return;
            foreach (var pair in _ports.ToArray())
            {
                var connection = pair.Value;
                if (desired.TryGetValue(pair.Key, out var settings) && settings == connection.Settings &&
                    (!connection.Completion.IsCompleted || Stopwatch.GetTimestamp() < connection.RetryAt)) continue;
                connection.Stop(); _ports.Remove(pair.Key); _retired.Add(connection);
            }
            foreach (var settings in desired.Values)
                if (!_ports.ContainsKey(settings.Port) && !_retired.Any(c => Same(c.Settings.Port, settings.Port) && !c.Completion.IsCompleted))
                    _ports.Add(settings.Port, new Connection(this, settings));
            foreach (var retired in _retired.Where(c => c.Completion.IsCompleted).ToArray())
            { retired.Dispose(); _retired.Remove(retired); }
        }
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private bool IsAmbiguous(Connection connection) => _ports.Values.Any(other => other != connection && other.Ready &&
        (Same(other.Name, connection.Name) || connection.Announcement is { } a && other.Announcement is { } b && Same(a.DeviceId, b.DeviceId)));
    private bool CanRoute(Connection connection) => _ports.TryGetValue(connection.Settings.Port, out var current) && current == connection && connection.Ready && !IsAmbiguous(connection);

    public ArduinoStatus[] Snapshot()
    {
        lock (_gate) return _ports.Values.Select(c => new ArduinoStatus(c.Name, c.Settings.Port, c.Ready && !IsAmbiguous(c),
            c.Ready && IsAmbiguous(c) ? "Conflicting device name or ID" : c.Status) { Capabilities = c.Announcement }).ToArray();
    }

    internal bool HasTarget(string name)
    {
        lock (_gate) return _ports.Values.Any(c => Same(c.Name, name) && (c.Announcement is not null || c.Settings.Configured is not null));
    }

    // Resolve at dispatch time: discovered COM numbers never become playlist configuration.
    // The selected connection is retained across the asynchronous write, never rebound to a replacement.
    public Task<long> SendAsync(string name, byte[] bytes, CancellationToken token, ArduinoOptions? expected = null)
    {
        name = name.Trim();
        Connection connection;
        var command = ControllerOptions.Text(BroadcastEnvelope.Parse(bytes).Body, "command");
        lock (_gate)
        {
            var matches = _ports.Values.Where(c => Same(c.Name, name) && CanRoute(c)).ToArray();
            if (matches.Length != 1) throw new IOException($"Arduino {name} is unavailable or its identity is ambiguous.");
            connection = matches[0];
            if (expected is not null && connection.Settings.Configured != expected) throw new IOException($"Arduino {name} serial settings changed.");
            if (connection.Announcement is { } announcement && !announcement.Commands.Contains(command, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Arduino {name} does not advertise command '{command}'.");
        }
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!CanRoute(connection)) throw new IOException($"Arduino {name} disconnected or became ambiguous before sending.");
                if (connection.Announcement is { } latest && !latest.Commands.Contains(command, StringComparer.OrdinalIgnoreCase))
                    throw new IOException($"Arduino {name} no longer advertises command '{command}'.");
                return connection.Write(bytes);
            }
        }, token);
    }

    private async Task ReceiveAsync(Connection connection, byte[] bytes, CancellationToken token)
    {
        var message = BroadcastEnvelope.Parse(bytes);
        if (message.IsExpired(DateTimeOffset.UtcNow)) return;
        if (message.Type != "event") throw new ArgumentException("Serial input must be an event.");
        if (message.Event == SerialDeviceAnnouncement.EventName)
        {
            var announcement = SerialDeviceAnnouncement.Parse(message);
            lock (_gate)
            {
                if (connection.Settings.Configured is { } configured && !Same(configured.Name, announcement.Name))
                    throw new ArgumentException("Serial announcement does not match its configured name.");
                if (connection.Announcement is { } previous && (!Same(previous.Name, announcement.Name) || !Same(previous.DeviceId, announcement.DeviceId)))
                    throw new ArgumentException("Serial identity changed without reconnecting.");
                var changed = connection.Announcement?.Signature != announcement.Signature;
                connection.Announcement = announcement;
                if (changed)
                {
                    _log.Info("ArduinoDiscovered", $"{announcement.Name}: discovered on {connection.Settings.Port}",
                        new { Port = connection.Settings.Port, Announcement = announcement });
                    if (IsAmbiguous(connection)) _log.Warning("ArduinoIdentityConflict", $"{announcement.Name}: duplicate serial identity; commands and events are blocked");
                }
            }
            return; // Transport discovery is not a sensor trigger.
        }
        lock (_gate)
        {
            if (!CanRoute(connection)) return;
            if (!Same(message.Source, connection.Name)) throw new ArgumentException("Serial event source does not match its announced device name.");
            if (connection.Announcement is { } a && !a.AcceptsEvent(message)) throw new ArgumentException("Serial event was not advertised by this device.");
        }
        await _received(bytes, connection.Name, token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_monitor is not null) await _monitor.ConfigureAwait(false);
        Connection[] connections;
        lock (_gate)
        {
            connections = [.. _ports.Values, .. _retired];
            foreach (var connection in connections) connection.Stop();
            _ports.Clear(); _retired.Clear();
        }
        await Task.WhenAll(connections.Select(c => c.Completion)).ConfigureAwait(false);
        foreach (var connection in connections) connection.Dispose();
        _shutdown.Dispose();
    }

    private sealed record Settings(string Port, int BaudRate, bool DtrEnable, ArduinoOptions? Configured, string InstanceId);
    private sealed class Connection : IDisposable
    {
        private readonly ArduinoConnections _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private ISerialLink? _link;
        private string _status = "Connecting";
        public Settings Settings { get; }
        public Task Completion { get; }
        public SerialDeviceAnnouncement? Announcement { get; set; }
        public long RetryAt { get; private set; }
        public string Name => Announcement?.Name ?? Settings.Configured?.Name ?? "USB device";
        public bool Ready => _link is not null && (Announcement is not null || Settings.Configured is not null);
        public string Status => Ready ? "Connected" : _status;

        public Connection(ArduinoConnections owner, Settings settings)
        {
            _owner = owner; Settings = settings;
            Completion = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public long Write(byte[] bytes)
        {
            if (_link is null || _cancellation.IsCancellationRequested) throw new IOException($"Arduino {Name} disconnected.");
            var at = Stopwatch.GetTimestamp(); _link.Write(bytes); return at;
        }
        private void ReadLoop()
        {
            ISerialLink? link = null;
            try
            {
                link = _owner._open(Settings.Port, Settings.BaudRate, Settings.DtrEnable);
                var openedAt = Stopwatch.GetTimestamp();
                lock (_owner._gate) { _link = link; _status = Settings.Configured is null ? "Waiting for announcement" : "Connected"; }
                _owner._log.Info("ArduinoSerialOpened", $"{Settings.Port}: {(Settings.Configured is null ? "waiting for device announcement" : "configured device connected")}");
                var framer = new SerialLineFramer(BroadcastEnvelope.MaximumDatagramBytes);
                while (!_cancellation.IsCancellationRequested)
                {
                    lock (_owner._gate)
                        if (Settings.Configured is null && Announcement is null && Stopwatch.GetElapsedTime(openedAt).TotalSeconds >= 5)
                            throw new TimeoutException("No compatible device announcement within five seconds.");
                    byte value;
                    try { value = link.ReadByte(); }
                    catch (TimeoutException) { continue; }
                    var bytes = framer.Push(value, out var oversized);
                    if (oversized) _owner._log.Warning("ArduinoLineTooLarge", $"{Settings.Port}: discarded oversized serial line");
                    if (bytes is null) continue;
                    try { _owner.ReceiveAsync(this, bytes, _cancellation.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // Unknown ports may contain ordinary serial text; one probe timeout is enough to log.
                        if (Announcement is not null || Settings.Configured is not null)
                            _owner._log.Error("ArduinoMessageRejected", ex, new { Name, Settings.Port });
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_owner._gate) _status = ex.Message;
                if (!_cancellation.IsCancellationRequested)
                    _owner._log.Warning("ArduinoDisconnected", $"{Settings.Port}: {ex.Message}", new { Name, Settings.Port, Error = ex.Message });
            }
            finally
            {
                lock (_owner._gate)
                {
                    _link = null; link?.Dispose();
                    // Do not repeatedly reset an unrecognized USB peripheral every second.
                    var seconds = Settings.Configured is not null || Announcement is not null ? 1 : 30;
                    RetryAt = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
                    if (_cancellation.IsCancellationRequested) _status = "Disconnected";
                }
            }
        }
        public void Stop() => _cancellation.Cancel();
        public void Dispose() => _cancellation.Dispose();
    }
}
