using System.Diagnostics;
using System.Windows.Media;
using PhotoBooth.Diagnostics;

namespace Experience;

internal sealed class PlaybackPerformanceProbe
{
    private readonly string? _requestId;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    // Allocate once, never grow a collection inside a rendering callback.
    private readonly double[] _gaps = new double[20000];
    private int _count;
    private int _omitted;
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private readonly long _allocated = GC.GetTotalAllocatedBytes(false);
    private TimeSpan? _previous;
    private bool _stopped;

    public PlaybackPerformanceProbe(string? requestId)
    {
        _requestId = requestId;
        CompositionTarget.Rendering += OnRendering;
        Telemetry.Info("PlaybackRenderingCapabilities", new { RenderingTier = RenderCapability.Tier >> 16,
            PixelShader20Supported = RenderCapability.IsPixelShaderVersionSupported(2, 0) }, requestId);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        if (_previous.HasValue)
        {
            var gap = (frame.RenderingTime - _previous.Value).TotalMilliseconds;
            if (gap <= 0) return;
            if (_count < _gaps.Length) _gaps[_count++] = gap;
            else _omitted++;
        }
        _previous = frame.RenderingTime;
    }

    public void Stop(double videoPositionSeconds)
    {
        if (_stopped) return;
        _stopped = true;
        CompositionTarget.Rendering -= OnRendering;
        _elapsed.Stop();
        var elapsed = _elapsed.Elapsed.TotalSeconds;
        var allocationBytes = GC.GetTotalAllocatedBytes(false) - _allocated;
        var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - _collections[i]).ToArray();
        // Rendering is unsubscribed: this buffer now belongs exclusively to the worker.
        _ = Task.Run(() =>
        {
            Array.Sort(_gaps, 0, _count);
            var samples = _gaps.AsSpan(0, _count);
            double sum = 0;
            var slow = 0;
            foreach (var gap in samples) { sum += gap; if (gap > 50) slow++; }
            Telemetry.Info("PlaybackPerformance", new
            {
                ElapsedSeconds = elapsed, VideoPositionSeconds = videoPositionSeconds,
                RenderCallbackSamples = _count, OmittedCallbackSamples = _omitted,
                MeanCallbackGapMs = _count > 0 ? (double?)(sum / _count) : null,
                P95CallbackGapMs = _count > 0 ? (double?)samples[(int)Math.Floor((_count - 1) * 0.95)] : null,
                MaximumCallbackGapMs = _count > 0 ? (double?)samples[^1] : null,
                CallbackGapsOver50Ms = slow,
                ProcessManagedAllocationBytes = allocationBytes, Gen0Collections = collections[0],
                Gen1Collections = collections[1], Gen2Collections = collections[2],
                Measurement = "WPF rendering callbacks, not presented or decoded video frames"
            }, _requestId);
        });
    }
}
