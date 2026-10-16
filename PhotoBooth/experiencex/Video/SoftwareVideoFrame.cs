using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Vortice.WIC;

namespace ExperienceX;

// Diagnostic CPU decode/processing path. The bitmap is reused; only the renderer uploads it.
// Do not hold the WIC read lock while Media Foundation writes the next frame.
internal sealed class SoftwareVideoFrame : IDisposable
{
    private readonly IWICImagingFactory _factory = new();
    private readonly IWICBitmap _bitmap;
    private readonly int _width, _height;
    internal SoftwareVideoFrame(int width, int height)
    {
        _width = width; _height = height;
        try { _bitmap = _factory.CreateBitmap((uint)width, (uint)height, PixelFormat.Format32bppBGRA, BitmapCreateCacheOption.CacheOnLoad); }
        catch { _factory.Dispose(); throw; }
    }
    internal void Transfer(IMFMediaEngine engine, GraphicsResources graphics, ID3D11Texture2D texture, RendererProgress? progress, string? screenId)
    {
        using (var operation = progress?.Begin("TransferSoftwareVideoBitmap", screenId))
            engine.TransferVideoFrame(_bitmap, null, new Vortice.RawRect(0, 0, _width, _height), null);
        using var upload = progress?.Begin("UploadSoftwareVideoTexture", screenId);
        using var pixels = _bitmap.Lock(BitmapLockFlags.Read);
        var data = pixels.Data;
        // UpdateSubresource copies the CPU data before returning; releasing the WIC lock is safe.
        graphics.Context.UpdateSubresource(texture, 0, null, data.DataPointer, data.Pitch, 0);
    }
    public void Dispose()
    {
        try { _bitmap.Dispose(); }
        finally { _factory.Dispose(); }
    }
}
