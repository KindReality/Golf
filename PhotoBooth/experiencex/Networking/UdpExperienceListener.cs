using System.Text.Json;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace ExperienceX;

internal sealed class UdpExperienceListener : IAsyncDisposable
{
    private readonly UdpBroadcastListener _listener;
    internal int Port => _listener.Port;

    public UdpExperienceListener(Dispatcher dispatcher, Func<ExperienceOptions> options,
        Action<ExperienceRequest> received, Action<BroadcastEnvelope>? eventReceived = null)
    {
        _listener = new(() => options(), async (message, token) =>
        {
            // Controllers own discovery commands; clients only consume the discovery events.
            if (ControllerDiscovery.IsRequest(message)) return;
            if (message.Type == "event")
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !message.Targets(options().EffectiveDeviceName) || message.IsExpired(DateTimeOffset.UtcNow))
                        return;
                    Telemetry.Info("UdpEventReceived", new
                    {
                        message.Source,
                        message.Device,
                        message.Event,
                        message.HopCount
                    }, message.RequestId);
                    eventReceived?.Invoke(message);
                }, DispatcherPriority.Normal, token);
                return;
            }
            ExperienceRequest request;
            try
            {
                request = ExperienceRequest.Parse(message.Body) with
                {
                    RequestId = message.RequestId,
                    Source = message.Source
                };
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                Telemetry.Warning("UdpRequestInvalid", new
                {
                    message.Source,
                    Reason = ex.Message
                }, message.RequestId);
                return;
            }
            await dispatcher.InvokeAsync(() =>
            {
                // Recheck after dispatch: configuration or the deadline may have changed while queued.
                if (token.IsCancellationRequested || !message.Targets(options().EffectiveDeviceName) || message.IsExpired(DateTimeOffset.UtcNow))
                    return;
                Telemetry.Info("UdpRequestReceived", new
                {
                    message.Device,
                    request.Experience,
                    request.Value,
                    request.Screen,
                    request.DisplayName,
                    request.AudioDevice,
                    request.AudioDevices,
                    request.Channel,
                    request.Mute,
                    request.Loop,
                    request.Command,
                    request.PlaybackRequestId,
                    Volume = request.VolumePercent,
                    message.Source
                }, message.RequestId);
                received(request);
            }, DispatcherPriority.Normal, token);
        });
    }

    public void Bind(int port) => _listener.Bind(port);
    public void Bind(int commandsPort, int eventsPort) => _listener.Bind(commandsPort, eventsPort);
    public ValueTask DisposeAsync() => _listener.DisposeAsync();
}
