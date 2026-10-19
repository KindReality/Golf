using Controller.Configuration;
using Controller.Hardware;
using Controller.Scheduling;
using PhotoBooth.Networking;

namespace Controller.Networking;

internal sealed class CommandDispatcher(ArduinoConnections arduinos,
    Func<BroadcastEnvelope, CancellationToken, Task> local, CancellationToken shutdown) : ICommandDispatcher
{
    public async Task<DispatchReceipt> SendAsync(PlaylistCommand step, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
    {
        var bytes = BroadcastEnvelope.Create(options, step.Type, step.Device!, step.Payload, requestId, chain, step.Context, step.Priority);
        if (step.Transport == "udp")
        {
            long firstSent = 0;
            // A scene command may cancel its own playlist. Once sending starts, complete its
            // broadcast independently and deliver the identical envelope locally, even offline.
            var controllerCommand = step.Type == "command" && ControllerOptions.Text(step.Payload, "command") is "scene" or "playlist";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(controllerCommand ? shutdown : token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestLifetimeSeconds));
            try
            {
                await UdpBroadcastSender.SendAsync(bytes, options, deadline.Token,
                    at => Interlocked.CompareExchange(ref firstSent, at, 0)).ConfigureAwait(false);
            }
            finally
            {
                if (controllerCommand && !shutdown.IsCancellationRequested)
                    await local(BroadcastEnvelope.Parse(bytes), shutdown).ConfigureAwait(false);
            }
            return new(firstSent);
        }
        var written = await arduinos.SendAsync(step.Device!, bytes, token,
            options.Arduinos.FirstOrDefault(a => ControllerOptions.Same(a.Name, step.Device))).ConfigureAwait(false);
        return new(written);
    }
}
