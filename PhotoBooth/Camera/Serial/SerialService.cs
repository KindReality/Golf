using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SaftApp.Serial
{
    public class SerialService : ISerialService
    {
        private readonly SerialOptions _options;
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _reconnectCts;
        private Task? _reconnectTask;

        public event EventHandler<string>? LineReceived;

        public SerialService(SerialOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public bool IsOpen => _port?.IsOpen ?? false;

        public async Task<bool> OpenAsync()
        {
            if (IsOpen) return true;
            if (string.IsNullOrWhiteSpace(_options.PortName)) return false;

            try
            {
                _port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _port.Open();

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));

                return true;
            }
            catch (Exception)
            {
                try { _port?.Dispose(); } catch { }
                _port = null;
                return false;
            }
        }

        public void Close()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            try { _reconnectCts?.Cancel(); } catch { }
            _reconnectCts = null;
            try { _port?.Close(); } catch { }
            try { _port?.Dispose(); } catch { }
            _port = null;
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            if (_port is null) return;

            var sb = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested && _port.IsOpen)
                {
                    try
                    {
                        string? line = _port.ReadLine();
                        if (!string.IsNullOrEmpty(line))
                        {
                            LineReceived?.Invoke(this, line.Trim());
                        }
                    }
                    catch (TimeoutException)
                    {
                        // ignore and continue
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // if port error occurred, attempt to stop loop and reconnect
                        break;
                    }

                    await Task.Delay(10, token).ContinueWith(_ => { });
                }
            }
            finally
            {
                try { _port?.Close(); } catch { }
                try { _port?.Dispose(); } catch { }
                _port = null;
                
                // Port was closed, start reconnection task
                StartReconnectLoop();
            }
        }

        private void StartReconnectLoop()
        {
            // Only start reconnect loop if we're not already doing one
            if (_reconnectTask is not null && !_reconnectTask.IsCompleted)
                return;

            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }

        private async Task ReconnectLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Wait 1 second before attempting reconnection
                    await Task.Delay(1000, token);

                    if (token.IsCancellationRequested) break;

                    // Attempt to reconnect
                    if (await OpenAsync())
                    {
                        // Successfully reconnected, exit reconnect loop
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Log or ignore connection errors and continue attempting
                }
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
