using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PhotoBooth.Diagnostics;

namespace PhotoBooth.Networking;

internal sealed class UdpBroadcastListener(Func<BroadcastOptions> options,
    Func<BroadcastEnvelope, CancellationToken, Task> received) : IAsyncDisposable
{
    private readonly RequestDeduplicator _duplicates = new();
    private readonly List<Task> _loops = [];
    private Binding? _binding;
    private bool _stopping;
    public int Port => _binding is null ? 0 : ((IPEndPoint)_binding.Client.Client.LocalEndPoint!).Port;

    public void Bind(int port)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var replacement = new Binding(port);
        _binding?.Stop();
        _binding = replacement;
        _loops.RemoveAll(task => task.IsCompleted);
        _loops.Add(ListenAsync(replacement));
        Telemetry.Info("UdpListening", new { Address = "0.0.0.0", Port, DeviceName = options().EffectiveDeviceName, Broadcast = true });
    }

    private async Task ListenAsync(Binding binding)
    {
        using var owner = binding;
        var token = binding.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var packet = await binding.Client.ReceiveAsync(token).ConfigureAwait(false);
                BroadcastEnvelope message;
                try { message = BroadcastEnvelope.Parse(packet.Buffer); }
                catch (Exception ex) when (ex is ArgumentException or JsonException)
                {
                    Telemetry.Warning("UdpRequestInvalid", new { Sender = packet.RemoteEndPoint.ToString(), Bytes = packet.Buffer.Length, Reason = ex.Message });
                    continue;
                }
                var current = options();
                if (!message.Targets(current.EffectiveDeviceName))
                {
                    Telemetry.Debug("UdpTargetIgnored", new { message.Device, DeviceName = current.EffectiveDeviceName }, message.RequestId);
                    continue;
                }
                if (message.IsExpired(DateTimeOffset.UtcNow))
                {
                    Telemetry.Warning("UdpRequestExpired", new { message.Source, message.ExpiresAt }, message.RequestId);
                    continue;
                }
                if (!_duplicates.Accept(message.Source.ToUpperInvariant(), message.RequestId, current.RequestDeduplicationSeconds))
                {
                    Telemetry.Info("UdpDuplicateSuppressed", new { message.Source, Sender = packet.RemoteEndPoint.ToString() }, message.RequestId);
                    continue;
                }
                if (!token.IsCancellationRequested)
                    await received(message, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Telemetry.Error("UdpReceiveFailed", ex);
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _binding?.Stop();
        await Task.WhenAll(_loops).ConfigureAwait(false);
    }

    private sealed class Binding : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public UdpClient Client { get; } = new(AddressFamily.InterNetwork);
        private int _stopped;
        public Binding(int port)
        {
            try
            {
                // Cooperating applications on the same PC each receive broadcast datagrams.
                Client.ExclusiveAddressUse = false;
                Client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch { Client.Dispose(); Cancellation.Dispose(); throw; }
        }
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            Cancellation.Cancel();
            Client.Dispose();
        }
        public void Dispose() { Stop(); Cancellation.Dispose(); }
    }
}
