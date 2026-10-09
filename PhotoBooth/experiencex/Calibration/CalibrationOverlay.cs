using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExperienceX;

internal sealed class CalibrationOverlay : IDisposable
{
    private ID3D11PixelShader _overlayPs = null!;
    private ID3D11Buffer _overlayConstants = null!;
    private ID3D11BlendState _overlayBlend = null!;
    private ID3D11Texture2D? _labelTexture;
    private ID3D11ShaderResourceView? _labelView;
    private string? _labelText;
    private double _labelUpdatedAt;
    private int _labelWidth, _labelHeight;
    public KeystoneOptions? Preview
    {
        get; private set;
    }
    private int _selectedPoint = -1, _flashPointMask;
    private double _flashAt = -1;
    private bool _flashSuccess;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayConstants
    {
        public Vector4 Points0, Points1, Screen, Selection, Grid, Row0, Row1, Row2;
    }
    public bool Animating(ExperienceOptions options) => options.ConfigurationMode || (_flashAt >= 0 && Now - _flashAt < 0.28);
    public bool Visible(ExperienceOptions options) => Animating(options);
    public void Configure(ExperienceOptions options)
    {
        if (!options.ConfigurationMode)
        {
            _selectedPoint = -1;
            Preview = null;
        }
    }
    public void Show(int point)
    {
        _selectedPoint = point;
        _flashAt = -1;
    }
    public void Fade()
    {
        _selectedPoint = -1;
    }
    public void SetPreview(KeystoneOptions? preview) => Preview = preview;
    public void Flash(int mask, bool success)
    {
        _flashPointMask = mask;
        _flashSuccess = success;
        _flashAt = Now;
    }
    public void Initialize(GraphicsResources graphics)
    {
        _overlayPs = graphics.Device.CreatePixelShader(GraphicsResources.Shader("CalibrationPS.cso"));
        _overlayConstants = graphics.Device.CreateBuffer((uint)Marshal.SizeOf<OverlayConstants>(), BindFlags.ConstantBuffer);
        var blend = BlendDescription.Opaque;
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _overlayBlend = graphics.Device.CreateBlendState(blend);
    }
    public void Draw(GraphicsResources graphics, ExperienceOptions _options, string _monitor, KeystoneTransformCache transforms)
    {
        var k = Preview ?? _options.KeystoneFor(_monitor);
        var now = Now;
        var opacity = _options.ConfigurationMode ? 1 : 0;
        var flash = _flashAt >= 0 && now >= _flashAt && now - _flashAt <= 0.28 ? (now - _flashAt) / 0.28 : -1;
        var showTools = _options.ConfigurationMode && _options.CalibrationGridEnabled;
        if (showTools)
            UpdateLabel(graphics, _options, _monitor, k, now);
        var inverse = transforms.Get(k);
        var data = new OverlayConstants
        {
            Points0 = new((float)k.TopLeft.X, (float)k.TopLeft.Y, (float)k.TopRight.X, (float)k.TopRight.Y),
            Points1 = new((float)k.BottomLeft.X, (float)k.BottomLeft.Y, (float)k.BottomRight.X, (float)k.BottomRight.Y),
            Screen = new(graphics.Width, graphics.Height, (float)(now % 1000), (float)opacity),
            Selection = new(_selectedPoint, _flashPointMask, (float)flash, _flashSuccess ? 1 : 0),
            Grid = new(showTools ? 1 : 0, _labelWidth, _labelHeight, showTools ? 1 : 0),
            Row0 = inverse.Item1,
            Row1 = inverse.Item2,
            Row2 = inverse.Item3
        };
        graphics.Context.UpdateSubresource(in data, _overlayConstants);
        graphics.Context.RSSetViewport(0, 0, graphics.Width, graphics.Height);
        graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        graphics.Context.VSSetShader(graphics.VertexShader);
        graphics.Context.PSSetShader(_overlayPs);
        graphics.Context.PSSetConstantBuffer(0, _overlayConstants);
        if (_labelView is not null)
            graphics.Context.PSSetShaderResource(0, _labelView);
        else
            graphics.Context.PSUnsetShaderResource(0);
        graphics.Context.PSSetSampler(0, graphics.Sampler);
        graphics.Context.OMSetBlendState(_overlayBlend);
        graphics.Context.Draw(3, 0);
        graphics.Context.OMSetBlendState(null);
        graphics.Context.PSUnsetShaderResource(0);
        // One final redraw after a save flash ends removes it without hiding the editing handles.
    }
    private unsafe void UpdateLabel(GraphicsResources graphics, ExperienceOptions _options, string _monitor, KeystoneOptions k, double now)
    {
        // Text is static between changes and capped at ten updates/second during a drag.
        if (_labelView is not null && now - _labelUpdatedAt < 0.1)
            return;
        var text = CalibrationLabel.Text(_options, _monitor, k, graphics.Width, graphics.Height);
        if (text == _labelText)
            return;
        var data = CalibrationLabel.Create(text);
        graphics.Context.PSUnsetShaderResource(0);
        _labelView?.Dispose();
        _labelTexture?.Dispose();
        fixed (byte* pixels = data.Pixels)
            _labelTexture = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)data.Width, (uint)data.Height, 1, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)pixels, (uint)(data.Width * 4)));
        _labelView = graphics.Device.CreateShaderResourceView(_labelTexture);
        _labelWidth = data.Width;
        _labelHeight = data.Height;
        _labelText = text;
        _labelUpdatedAt = now;
    }

    public void Dispose()
    {
        IDisposable?[] resources = [_overlayPs, _overlayConstants, _overlayBlend, _labelView, _labelTexture];
        foreach (var resource in resources)
            resource?.Dispose();
    }
}
