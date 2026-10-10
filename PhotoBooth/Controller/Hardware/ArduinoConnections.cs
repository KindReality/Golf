using System.IO.Ports;
using System.Text;
using System.Diagnostics;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Hardware;

internal sealed record ArduinoStatus(string Name, string Port, bool Connected, string Status);
internal sealed class ArduinoConnections(ActivityLog log, Func<byte[], string, CancellationToken, Task> received) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Connection> _retired = [];

    public void Apply(ArduinoOptions[] options)
    {
        lock (_gate)
        {
            foreach (var existing in _connections.Values.ToArray())
            {
                if (options.Any(option => option.Enabled && option == existing.Options)) continue;
                existing.Stop();
                _connections.Remove(existing.Options.Name);
                _retired.Add(existing);
            }
            foreach (var option in options.Where(option => option.Enabled))
            {
                if (_connections.ContainsKey(option.Name)) continue;
                _connections.Add(option.Name, new(option, log, received));
            }
        }
    }

    public ArduinoStatus[] Snapshot()
    {
        lock (_gate) return _connections.Values.Select(connection => connection.Snapshot()).ToArray();
    }

    public Task<long> SendAsync(ArduinoOptions settings, byte[] bytes, CancellationToken token)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(settings.Name, out var connection) || connection.Options != settings)
                throw new IOException($"Arduino {settings.Name} is unavailable with the storyboard's serial settings.");
            return connection.SendAsync(bytes, token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Connection[] connections;
        lock (_gate)
        {
            connections = [.. _connections.Values, .. _retired];
            foreach (var connection in connections) connection.Stop();
            _connections.Clear();
            _retired.Clear();
        }
        await Task.WhenAll(connections.Select(connection => connection.Completion)).ConfigureAwait(false);
        foreach (var connection in connections) connection.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        private readonly object _portGate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ActivityLog _log;
        private readonly Func<byte[], string, CancellationToken, Task> _received;
        private readonly SemaphoreSlim _writer = new(1);
        private SerialPort? _port;
        private string _status = "Connecting";
        public ArduinoOptions Options { get; }
        public Task Completion { get; }

        public Connection(ArduinoOptions options, ActivityLog log, Func<byte[], string, CancellationToken, Task> received)
        {
            Options = options;
            _log = log;
            _received = received;
            Completion = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public ArduinoStatus Snapshot()
        {
            lock (_portGate) return new(Options.Name, Options.Port, _port?.IsOpen == true, _status);
        }

        public async Task<long> SendAsync(byte[] bytes, CancellationToken token)
        {
            // WriteTimeout bounds disconnected/blocked devices; this I/O never blocks the scheduling thread.
            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (_portGate)
                    {
                        if (_port?.IsOpen != true) throw new IOException($"Arduino {Options.Name} is disconnected.");
                        var at = Stopwatch.GetTimestamp();
                        _port.Write(bytes, 0, bytes.Length);
                        _port.Write("\n");
                        return at;
                    }
                }, token).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
        }

        private void ReadLoop()
        {
            string? lastFailure = null;
            while (!_cancellation.IsCancellationRequested)
            {
                SerialPort? port = null;
                try
                {
                    port = new(Options.Port, Options.BaudRate, Parity.None, 8, StopBits.One)
                    {
                        DtrEnable = Options.DtrEnable,
                        ReadTimeout = 250,
                        WriteTimeout = 250,
                        Encoding = new UTF8Encoding(false, true),
                        NewLine = "\n"
                    };
                    port.Open();
                    lock (_portGate) { _port = port; _status = "Connected"; }
                    lastFailure = null;
                    _log.Info("ArduinoConnected", $"{Options.Name}: connected on {Options.Port}", new { Options.Name, Options.Port, Options.BaudRate });
                    var framer = new SerialLineFramer(BroadcastEnvelope.MaximumDatagramBytes);
                    while (!_cancellation.IsCancellationRequested)
                    {
                        byte value;
                        try { value = (byte)port.ReadByte(); }
                        catch (TimeoutException) { continue; }
                        var line = framer.Push(value, out var oversized);
                        if (oversized) _log.Warning("ArduinoLineTooLarge", $"{Options.Name}: discarded oversized JSON line");
                        if (line is not null)
                        {
                            try { _received(line, Options.Name, _cancellation.Token).GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
                            catch (Exception ex) { _log.Error("ArduinoMessageRejected", ex, new { Options.Name }); }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    lock (_portGate) _status = ex.Message;
                    if (!_cancellation.IsCancellationRequested && lastFailure != ex.Message)
                        _log.Error("ArduinoDisconnected", ex, new { Options.Name, Options.Port });
                    lastFailure = ex.Message;
                }
                finally
                {
                    lock (_portGate) { _port = null; port?.Dispose(); }
                }
                _cancellation.Token.WaitHandle.WaitOne(1000);
            }
        }

        public void Stop() => _cancellation.Cancel();
        public void Dispose() { _cancellation.Dispose(); _writer.Dispose(); }
    }
}
