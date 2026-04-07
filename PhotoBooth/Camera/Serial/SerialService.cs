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
                        // if port error occurred, attempt to stop loop
                        break;
                    }

                    await Task.Delay(10, token).ContinueWith(_ => { });
                }
            }
            finally
            {
                try { Close(); } catch { }
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
