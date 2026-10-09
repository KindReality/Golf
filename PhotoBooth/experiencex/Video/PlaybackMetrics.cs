using System.Diagnostics;
using PhotoBooth.Diagnostics;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Per-playback counters, owned exclusively by the render worker.
internal sealed class PlaybackMetrics
{
    public long TransferredFrames, TransferCalls, SubmittedPresents, MaxTimestampGap;
    public long LastTimestamp = -1;
    public double MaxPresentGapMilliseconds;
    private long _lastPresent;
    private readonly Stopwatch _sample = new();
    public bool ReportDue => _sample.Elapsed.TotalSeconds >= 5;
    public void Reset()
    {
        TransferredFrames = 0;
        TransferCalls = 0;
        SubmittedPresents = 0;
        MaxTimestampGap = 0;
        LastTimestamp = -1;
        _lastPresent = 0;
        MaxPresentGapMilliseconds = 0;
        _sample.Restart();
    }
    public void BeginPlayback()
    {
        _lastPresent = 0;
        MaxPresentGapMilliseconds = 0;
        _sample.Restart();
    }
    public void Transferred(long timestamp)
    {
        TransferCalls++;
        if (TransferredFrames > 0)
            MaxTimestampGap = Math.Max(MaxTimestampGap, timestamp - LastTimestamp);
        LastTimestamp = timestamp;
        TransferredFrames++;
    }
    public void Presented()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPresent != 0)
            MaxPresentGapMilliseconds = Math.Max(MaxPresentGapMilliseconds, Stopwatch.GetElapsedTime(_lastPresent, now).TotalMilliseconds);
        _lastPresent = now;
        SubmittedPresents++;
    }
    public void Report(GraphicsResources graphics, MediaEngineSession media, string monitor, string? requestId, double opacity, long transformComputations)
    {
        _sample.Restart();
        var result = graphics.SwapChain.GetFrameStatistics(out var stats);
        long? decodedFramesRendered = null, decoderFramesDropped = null;
        try
        {
            using var extended = media.Engine?.QueryInterface<IMFMediaEngineEx>();
            if (extended is not null)
            {
                decodedFramesRendered = Convert.ToInt64(extended.GetStatistics(MediaEngineStatistic.FramesRendered).Value);
                decoderFramesDropped = Convert.ToInt64(extended.GetStatistics(MediaEngineStatistic.FramesDropped).Value);
            }
        }
        catch (Exception ex) { Telemetry.Warning("DecoderStatisticsUnavailable", new { Reason = ex.Message }, requestId); }
        Telemetry.Info("DirectXPlaybackPerformance", new
        {
            MonitorName = monitor,
            TransferredVideoFrames = TransferredFrames,
            MediaEngineFramesRendered = decodedFramesRendered,
            MediaEngineFramesDropped = decoderFramesDropped,
            TransferCalls = TransferCalls,
            SubmittedPresents = SubmittedPresents,
            TransformComputations = transformComputations,
            MaxVideoTimestampGapMilliseconds = MaxTimestampGap / 10000.0,
            MaxPresentSubmissionGapMilliseconds = MaxPresentGapMilliseconds,
            FrameStatisticsAvailable = result.Success,
            PresentCount = stats.PresentCount,
            PresentRefreshCount = stats.PresentRefreshCount,
            SyncRefreshCount = stats.SyncRefreshCount,
            MediaPositionSeconds = media.Engine?.CurrentTime,
            SurfaceOpacity = opacity
        }, requestId);
    }
}
