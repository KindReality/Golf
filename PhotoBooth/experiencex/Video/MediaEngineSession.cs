using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Media callbacks publish flags only. Engine operations and disposal stay on the render worker.
internal sealed class MediaEngineSession(Action wake, RendererProgress? progress = null, string? screenId = null) : IDisposable
{
    private IMFMediaEngine? _engine;
    private volatile int _canPlay, _ended, _error;
    private int _errorCode, _errorHresult;
    private long _generation;
    private SoftwareVideoFrame? _softwareFrame;
    internal bool SoftwareVideoDecoding { get; private set; }
    public IMFMediaEngine? Engine => _engine;
    public bool IsOpen => _engine is not null;
    public bool Ready => _canPlay == 2;
    public bool Ended => _ended != 0;
    public double Position { get { using var operation = progress?.Begin("MediaPosition", screenId); return _engine?.CurrentTime ?? 0; } }
    public double Duration { get { using var operation = progress?.Begin("MediaDuration", screenId); return _engine?.Duration ?? double.NaN; } }
    public void Open(string path, ExperienceRequest request, IMFDXGIDeviceManager manager, bool softwareVideoDecoding = false)
    {
        using var operation = progress?.Begin("MediaOpen", screenId);
        MediaPath.EnsureLocalPath(path);
        SoftwareVideoDecoding = softwareVideoDecoding;
        _canPlay = 0;
        _ended = 0;
        _error = 0;
        _errorCode = 0;
        _errorHresult = 0;
        var generation = ++_generation;
        using var factory = new IMFMediaEngineClassFactory();
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.VideoOutputFormat = Format.B8G8R8A8_UNorm;
        // MF_MEDIA_ENGINE_DXGI_MANAGER enables accelerated decode/processing. Omitting it
        // selects Microsoft's software frame-server path without changing our D3D renderer.
        if (!softwareVideoDecoding) attributes.DxgiManager = manager;
        _engine = factory.CreateInstance(MediaEngineCreateFlags.None, attributes, (kind, p1, p2) =>
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;
            if (kind == MediaEngineEvent.CanPlay)
                Interlocked.CompareExchange(ref _canPlay, 1, 0);
            if (kind == MediaEngineEvent.Ended)
                _ended = 1;
            if (kind == MediaEngineEvent.Error)
            {
                _errorCode = unchecked((int)p1);
                _errorHresult = p2;
                _error = 1;
            }
            try
            {
                wake();
            }
            catch (ObjectDisposedException) { }
        });
        _engine.Muted = request.Mute;
        _engine.Loop = request.Loop;
        using (var step = progress?.Begin("MediaSetSource", screenId)) _engine.SetSource(new Uri(path).AbsoluteUri).CheckError();
        using (var step = progress?.Begin("MediaLoad", screenId)) _engine.Load();
    }
    public void StartIfReady(Action<int, int> createTexture)
    {
        if (_error != 0)
            throw new COMException($"Media Foundation playback error {_errorCode} (HRESULT 0x{_errorHresult:X8}).", _errorHresult);
        if (_canPlay != 1)
            return;
        _canPlay = 2;
        var size = _engine!.NativeVideoSize;
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("Media has no video stream.");
        createTexture(size.Width, size.Height);
        using var operation = progress?.Begin("MediaPlay", screenId);
        _engine.Play().CheckError();
    }
    public bool TryGetFrame(out long timestamp)
    {
        timestamp = 0;
        if (!Ready) return false;
        // The convenience wrapper treats S_FALSE as success, although it means no new frame.
        using var operation = progress?.Begin("VideoStreamTick", screenId);
        var result = _engine!.OnVideoStreamTick_(out timestamp);
        result.CheckError();
        return result.Code == 0;
    }
    public void TransferFrame(GraphicsResources graphics, ID3D11Texture2D texture, int width, int height)
    {
        using var operation = progress?.Begin("TransferVideoFrame", screenId);
        if (SoftwareVideoDecoding)
        {
            _softwareFrame ??= new(width, height);
            _softwareFrame.Transfer(_engine!, graphics, texture, progress, screenId);
        }
        else
        {
            using var surface = texture.QueryInterface<IDXGISurface>();
            _engine!.TransferVideoFrame(surface, null, new Vortice.RawRect(0, 0, width, height), null);
        }
    }
    public void Dispose()
    {
        using var operation = progress?.Begin("MediaShutdown", screenId);
        Interlocked.Increment(ref _generation);
        var engine = _engine;
        _engine = null;
        if (engine is not null)
            try
            {
                var result = engine.Shutdown();
                if (result.Failure)
                    PhotoBooth.Diagnostics.Telemetry.Warning("MediaEngineShutdownFailed", new { ScreenId = screenId, HResult = $"0x{result.Code:X8}" });
            }
            finally
            {
                try { engine.Dispose(); }
                finally { _softwareFrame?.Dispose(); _softwareFrame = null; }
            }
    }
}
