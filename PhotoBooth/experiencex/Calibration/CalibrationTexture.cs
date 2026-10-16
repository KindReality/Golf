using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExperienceX;

// Render-thread-owned premultiplied surface. Static per-screen caches are cropped;
// the shared animation layer is display-sized and composites once over each video frame.
internal sealed class CalibrationTexture : IDisposable
{
    private ID3D11Texture2D? _texture;
    private ID3D11RenderTargetView? _target;
    private ID3D11ShaderResourceView? _view;
    private int _width, _height;
    internal bool Ensure(GraphicsResources graphics, int width, int height)
    {
        if (_texture is not null && _width == width && _height == height) return false;
        Dispose();
        try
        {
            _texture = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            _target = graphics.Device.CreateRenderTargetView(_texture);
            _view = graphics.Device.CreateShaderResourceView(_texture);
            _width = width; _height = height;
        }
        catch { Dispose(); throw; }
        return true;
    }
    internal void Render(GraphicsResources graphics, Action draw)
    {
        var previous = graphics.CurrentTarget;
        graphics.Context.PSUnsetShaderResource(0);
        try
        {
            graphics.BindTarget(_target!);
            graphics.Context.ClearRenderTargetView(_target!, new(0, 0, 0, 0));
            draw();
        }
        finally
        {
            graphics.Context.PSUnsetShaderResource(0);
            graphics.BindTarget(previous);
        }
    }
    internal void Composite(GraphicsResources graphics, System.Drawing.Rectangle bounds) => graphics.CompositeCalibration(_view!, bounds);
    public void Dispose()
    {
        _view?.Dispose(); _view = null;
        _target?.Dispose(); _target = null;
        _texture?.Dispose(); _texture = null;
    }
}
