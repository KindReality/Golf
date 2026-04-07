using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SaftApp.Serial
{
    public class SerialService : ISerialService
    {
        private readonly SerialOptions _options;
        private SerialPort? _port;
        private CancellationTokenSource? _reconnectCts;
        private Task? _reconnectTask;

        public event EventHandler<string>? LineReceived;
        public event EventHandler<SerialStatusEventArgs>? StatusChanged;

        public SerialService(SerialOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Debug.WriteLine($"SerialService: constructed (Port={_options.PortName}, Baud={_options.BaudRate}, AutoOpen={_options.AutoOpen})");
        }

        public bool IsOpen => _port?.IsOpen ?? false;

        public async Task<bool> OpenAsync()
        {
            // Public entry for initial open - marks as non-reconnect
            bool result = await TryOpenAsync(isReconnect: false);
            if (!result)
            {
                Debug.WriteLine("SerialService: Initial connect failed, starting reconnect loop.");
                StartReconnectLoop();
            }
            return result;
        }

        private void RaiseStatus(SerialStatusKind kind, string message, int? attempt = null)
        {
            var args = new SerialStatusEventArgs(kind, message, attempt);
            Debug.WriteLine($"SerialService: {args}");
            StatusChanged?.Invoke(this, args);
        }

        private async Task<bool> TryOpenAsync(bool isReconnect)
        {
            if (IsOpen)
            {
                Debug.WriteLine("SerialService: OpenAsync called but already open.");
                return true;
            }
            if (string.IsNullOrWhiteSpace(_options.PortName))
            {
                RaiseStatus(SerialStatusKind.Error, "OpenAsync failed - PortName not configured.");
                return false;
            }

            RaiseStatus(SerialStatusKind.Connecting,
                $"Attempting to open {_options.PortName} at {_options.BaudRate} baud ({(isReconnect ? "reconnect" : "initial connect")})");

            try
            {
                _port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _port.DataReceived += Port_DataReceived;
                _port.ErrorReceived += Port_ErrorReceived;
                _port.PinChanged += Port_PinChanged;

                _port.Open();

                RaiseStatus(SerialStatusKind.Connected,
                    $"Port {_options.PortName} opened ({(isReconnect ? "reconnected" : "initial connect")})");
                return true;
            }
            catch (Exception ex)
            {
                RaiseStatus(SerialStatusKind.Error,
                    $"Failed to open {_options.PortName}: {ex.Message}");
                try { if (_port is not null) { _port.DataReceived -= Port_DataReceived; _port.ErrorReceived -= Port_ErrorReceived; _port.PinChanged -= Port_PinChanged; _port.Dispose(); } } catch { }
                _port = null;
                return false;
            }
        }

        private void Port_PinChanged(object? sender, SerialPinChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"SerialService: PinChanged event: {e.EventType}");
                var port = _port;
                if (port is null) return;

                // If Carrier Detect (CD) or DSR changed and the corresponding holding line is false, treat as disconnect
                if ((e.EventType == SerialPinChange.CDChanged && !port.CDHolding) ||
                    (e.EventType == SerialPinChange.DsrChanged && !port.DsrHolding) ||
                    (e.EventType == SerialPinChange.CtsChanged && !port.CtsHolding))
                {
                    RaiseStatus(SerialStatusKind.Disconnected,
                        $"Physical disconnect detected on {_options.PortName} (pin: {e.EventType})");
                    StartReconnectLoop();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SerialService: PinChanged handler exception: {ex.Message}");
            }
        }

        private void Port_ErrorReceived(object? sender, SerialErrorReceivedEventArgs e)
        {
            var port = _port;
            var closed = port is not null && !port.IsOpen;
            RaiseStatus(SerialStatusKind.Error,
                $"Port error: {e.EventType}" + (closed ? $" — port {_options.PortName} appears closed" : string.Empty));
            StartReconnectLoop();
        }

        private void Port_DataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var port = _port;
                if (port is null) return;

                while (port.BytesToRead > 0)
                {
                    string? line = null;
                    try { line = port.ReadLine(); }
                    catch (TimeoutException) { break; }

                    if (!string.IsNullOrEmpty(line))
                    {
                        var trimmed = line.Trim();
                        RaiseStatus(SerialStatusKind.DataReceived, $"RX: {trimmed}");
                        LineReceived?.Invoke(this, trimmed);
                    }
                }

                // If after processing data the port is closed, log disconnect and start reconnect
                if (!port.IsOpen)
                {
                    RaiseStatus(SerialStatusKind.Disconnected,
                        $"Port {_options.PortName} closed unexpectedly during read");
                    StartReconnectLoop();
                }
            }
            catch (Exception ex)
            {
                RaiseStatus(SerialStatusKind.Error, $"DataReceived exception: {ex.Message}");
                StartReconnectLoop();
            }
        }

        public void Close()
        {
            Debug.WriteLine("SerialService: Close called, shutting down port and any reconnect attempts.");
            try { _reconnectCts?.Cancel(); } catch { }
            _reconnectCts = null;

            try
            {
                if (_port is not null)
                {
                    try { _port.DataReceived -= Port_DataReceived; } catch { }
                    try { _port.ErrorReceived -= Port_ErrorReceived; } catch { }
                    try { _port.PinChanged -= Port_PinChanged; } catch { }
                    try { _port.Close(); } catch (Exception ex) { Debug.WriteLine($"SerialService: Error closing port: {ex.Message}"); }
                    try { _port.Dispose(); } catch (Exception ex) { Debug.WriteLine($"SerialService: Error disposing port: {ex.Message}"); }

                    RaiseStatus(SerialStatusKind.Disconnected, $"Port {_options.PortName} closed.");
                }
            }
            finally
            {
                _port = null;
            }
        }

        private void StartReconnectLoop()
        {
            // Only start reconnect loop if we're not already doing one
            if (_reconnectTask is not null && !_reconnectTask.IsCompleted)
            {
                Debug.WriteLine("SerialService: Reconnect loop already running.");
                return;
            }

            Debug.WriteLine("SerialService: Starting reconnect loop.");
            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }

        private async Task ReconnectLoopAsync(CancellationToken token)
        {
            int attempt = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    attempt++;
                    RaiseStatus(SerialStatusKind.ReconnectAttempt,
                        $"Waiting 1s then reconnecting to {_options.PortName}...", attempt);

                    // Wait 1 second before attempting reconnection
                    await Task.Delay(1000, token);
                    if (token.IsCancellationRequested) break;

                    // Attempt to reconnect (mark as reconnect)
                    if (await TryOpenAsync(isReconnect: true))
                    {
                        RaiseStatus(SerialStatusKind.Reconnected,
                            $"Reconnected to {_options.PortName}", attempt);
                        // Successfully reconnected, exit reconnect loop
                        return;
                    }
                    else
                    {
                        RaiseStatus(SerialStatusKind.Error,
                            $"Reconnect attempt failed", attempt);
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("SerialService: Reconnect loop canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SerialService: Reconnect loop exception: {ex.Message}");
                }
            }

            Debug.WriteLine("SerialService: Reconnect loop exiting.");
        }

        public void Dispose()
        {
            Debug.WriteLine("SerialService: Dispose called.");
            Close();
        }
    }
}
