using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Media callbacks publish flags only. Engine operations and disposal stay on the render worker.
internal sealed class MediaEngineSession(Action wake) : IDisposable
{
    private IMFMediaEngine? _engine;
    private volatile int _canPlay, _ended, _error;
    private int _errorCode, _errorHresult;
    private long _generation;
    public IMFMediaEngine? Engine => _engine;
    public bool IsOpen => _engine is not null;
    public bool Ready => _canPlay == 2;
    public bool Ended => _ended != 0;
    public double Position => _engine?.CurrentTime ?? 0;
    public double Duration => _engine?.Duration ?? double.NaN;
    public void Open(string path, ExperienceRequest request, IMFDXGIDeviceManager manager)
    {
        _canPlay = 0;
        _ended = 0;
        _error = 0;
        _errorCode = 0;
        _errorHresult = 0;
        var generation = ++_generation;
        using var factory = new IMFMediaEngineClassFactory();
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.VideoOutputFormat = Format.B8G8R8A8_UNorm;
        attributes.DxgiManager = manager;
        _engine = factory.CreateInstance(MediaEngineCreateFlags.None, attributes, (kind, p1, p2) =>
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;
            if (kind == MediaEngineEvent.CanPlay)
                _canPlay = 1;
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
        _engine.SetSource(new Uri(path).AbsoluteUri).CheckError();
        _engine.Load();
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
        _engine.Play().CheckError();
    }
    public bool TryGetFrame(out long timestamp)
    {
        timestamp = 0;
        return Ready && _engine!.OnVideoStreamTick(out timestamp);
    }
    public void TransferFrame(IDXGISurface surface, int width, int height) => _engine!.TransferVideoFrame(surface, null, new Vortice.RawRect(0, 0, width, height), null);
    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        var engine = _engine;
        _engine = null;
        if (engine is not null)
            try
            {
                engine.Shutdown();
            }
            finally { engine.Dispose(); }
    }
}
