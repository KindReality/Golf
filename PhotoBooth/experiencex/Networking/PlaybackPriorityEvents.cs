using System.Threading.Channels;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace ExperienceX;

// Render workers only enqueue. Network I/O cannot block rendering or decoder work.
internal sealed class PlaybackPriorityEvents : IAsyncDisposable
{
    private sealed record Notice(ExperienceRequest Request, string Display, string Screen, CommandPriority ActivePriority);
    private readonly Func<ExperienceOptions> _options;
    private readonly Func<byte[], ExperienceOptions, CancellationToken, Task> _send;
    private readonly Channel<Notice> _queue = Channel.CreateBounded<Notice>(new BoundedChannelOptions(256)
        { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly RequestDeduplicator _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    internal PlaybackPriorityEvents(Func<ExperienceOptions> options, Func<byte[], ExperienceOptions, CancellationToken, Task>? send = null)
    {
        _options = options; _send = send ?? ((bytes, settings, token) => UdpBroadcastSender.SendAsync(bytes, settings, token));
        _worker = Task.Run(RunAsync);
    }
    internal void Report(ExperienceRequest request, string display, string screen, CommandPriority activePriority)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.RequestId) ||
            activePriority <= request.EffectivePriority || _stop.IsCancellationRequested) return;
        if (!_seen.Accept(request.Source + "|" + display + "|" + screen, request.RequestId, 60)) return;
        if (!_queue.Writer.TryWrite(new(request, display, screen, activePriority)))
            Telemetry.Warning("PlaybackPriorityEventQueueFull", new { Display = display, Screen = screen }, request.RequestId);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var notice in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var settings = _options(); var request = notice.Request;
                    var label = settings.DisplayAliases.FirstOrDefault(p => p.Value.Equals(notice.Display, StringComparison.OrdinalIgnoreCase)).Key ?? notice.Display;
                    var chain = request.Chain;
                    if (chain is not null)
                    {
                        try { chain = chain.Enter($"{settings.EffectiveDeviceName}/event:{PlaybackPriorityEvent.EventName}"); }
                        catch (InvalidOperationException)
                        {
                            // Still announce the conflict, but do not reopen an exhausted
                            // event chain. Receivers cannot trigger further work from it.
                            chain = chain with { HopCount = BroadcastEnvelope.MaximumHops };
                        }
                    }
                    var bytes = PlaybackPriorityEvent.Create(settings, request.Source!, request.RequestId, request.IsFade ? request.Command! : request.Value,
                        notice.Display, label, notice.Screen, request.EffectivePriority, notice.ActivePriority, request.Loop, request.Context, chain);
                    await _send(bytes, settings, _stop.Token).ConfigureAwait(false);
                    Telemetry.Info("PlaybackPriorityEventSent", new { request.RequestId, notice.Display, notice.Screen, request.Value, request.Command,
                        RequestedPriority = request.EffectivePriority, notice.ActivePriority, Deferred = request.Loop });
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { Telemetry.Error("PlaybackPriorityEventFailed", ex, new { notice.Request.RequestId, notice.Display, notice.Screen }); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete(); _stop.Cancel();
        await _worker.ConfigureAwait(false); _stop.Dispose();
    }
}
