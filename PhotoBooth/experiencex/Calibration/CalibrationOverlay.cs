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
    public PointOptions? Preview
    {
        get; private set;
    }
    private int _selectedPoint = -1, _flashPointMask;
    private int? _singleFlash;
    private ID3D11Buffer? _pointsBuffer;
    private ID3D11ShaderResourceView? _pointsView;
    private PointOptions? _pointsShape;
    private long _flashRevision, _pointsFlashRevision = -1;
    private bool _selectedScreen;
    private double _flashAt = -1;
    private bool _flashSuccess;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayConstants
    {
        public Vector4 Screen, Selection, Grid, Row0, Row1, Row2, Label;
    }
    internal bool SelectedScreen => _selectedScreen;
    public bool Animating(ExperienceOptions options) => options.ConfigurationMode || (_flashAt >= 0 && Now - _flashAt < 0.28);
    public bool Visible(ExperienceOptions options) => Animating(options);
    public void Configure(ExperienceOptions options)
    {
        if (!options.ConfigurationMode)
        {
            _selectedPoint = -1;
            _selectedScreen = false;
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
    public void SelectScreen(bool selected) => _selectedScreen = selected;
    public void SetPreview(PointOptions? preview) => Preview = preview;
    public void Flash(int mask, bool success)
    {
        _singleFlash = null;
        _flashPointMask = mask;
        _flashSuccess = success;
        _flashAt = Now;
        _flashRevision++;
    }
    public void FlashPoint(int point, bool success)
    {
        _singleFlash = point; _flashPointMask = 0; _flashSuccess = success; _flashAt = Now; _flashRevision++;
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
    public void Draw(GraphicsResources graphics, ExperienceOptions _options, string _display, PointTransformCache transforms, string? screenId = null)
    {
        var k = Preview ?? _options.PointsFor(_display, screenId);
        var isVisualization = _options.ScreensFor(_display).FirstOrDefault(s => s.Id == screenId)?.IsVisualization == true;
        var isMat = _options.ScreensFor(_display).FirstOrDefault(s => s.Id == screenId)?.IsMat == true;
        UpdatePoints(graphics, k);
        var now = Now;
        var opacity = _options.ConfigurationMode ? 1 : 0;
        var flash = _flashAt >= 0 && now >= _flashAt && now - _flashAt <= 0.28 ? (now - _flashAt) / 0.28 : -1;
        var selectedScreen = _selectedScreen && _options.ConfigurationMode;
        var showTools = selectedScreen && _options.CalibrationGridEnabled && !isMat && !isVisualization;
        if (selectedScreen)
            UpdateLabel(graphics, _options, _display, screenId, now);
        var inverse = isMat ? default : transforms.Get(k with
        {
            Enabled = true
        });
        var center = ScreenNameLabel.Center(k, graphics.Width, graphics.Height);
        var data = new OverlayConstants
        {
            Screen = new(graphics.Width, graphics.Height, (float)(now % 1000), (float)opacity),
            Selection = new(isVisualization ? -2 : _selectedPoint, _flashPointMask, (float)flash, _flashSuccess ? 1 : 0),
            Grid = new(showTools ? 1 : 0, _labelWidth, _labelHeight, selectedScreen ? 1 : 0),
            Label = new(center.X, center.Y, selectedScreen ? .16f : _options.ConfigurationMode && !isMat ? .04f : 0, k.Vertices.Count),
            Row0 = inverse.Item1,
            Row1 = inverse.Item2,
            Row2 = inverse.Item3
        };
        graphics.Context.UpdateSubresource(in data, _overlayConstants);
        graphics.Context.RSSetViewport(0, 0, graphics.Width, graphics.Height);
        graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        graphics.Context.IASetInputLayout(null);
        graphics.Context.VSSetShader(graphics.VertexShader);
        graphics.Context.PSSetShader(_overlayPs);
        graphics.Context.PSSetConstantBuffer(0, _overlayConstants);
        graphics.Context.PSSetShaderResource(1, _pointsView!);
        if (_labelView is not null)
            graphics.Context.PSSetShaderResource(0, _labelView);
        else
            graphics.Context.PSUnsetShaderResource(0);
        graphics.Context.PSSetSampler(0, graphics.Sampler);
        graphics.Context.OMSetBlendState(_overlayBlend);
        graphics.Context.Draw(3, 0);
        graphics.Context.OMSetBlendState(null);
        graphics.Context.PSUnsetShaderResource(0);
        graphics.Context.PSUnsetShaderResource(1);
        // One final redraw after a save flash ends removes it without hiding the editing handles.
    }
    private void UpdatePoints(GraphicsResources graphics, PointOptions shape)
    {
        if (_pointsShape == shape && _pointsFlashRevision == _flashRevision) return;
        var vertices = shape.Vertices.Select((p, boundary) =>
        {
            var index = PointEditing.BoundaryIndex(shape, boundary);
            var flash = _singleFlash.HasValue ? _singleFlash == index : _flashPointMask == -1 || (index < 32 && (_flashPointMask & (1 << index)) != 0);
            return new Vector4((float)p.X, (float)p.Y, flash ? 1 : 0, index);
        }).ToArray();
        var replacement = graphics.Device.CreateBuffer<Vector4>(vertices, BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, structureByteStride: 16);
        ID3D11ShaderResourceView view;
        try { view = graphics.Device.CreateShaderResourceView(replacement); }
        catch { replacement.Dispose(); throw; }
        graphics.Context.PSUnsetShaderResource(1);
        _pointsView?.Dispose(); _pointsBuffer?.Dispose();
        _pointsBuffer = replacement; _pointsView = view; _pointsShape = shape; _pointsFlashRevision = _flashRevision;
    }
    private unsafe void UpdateLabel(GraphicsResources graphics, ExperienceOptions _options, string _display, string? screenId, double now)
    {
        // Text is static between changes and capped at ten updates/second during a drag.
        if (_labelView is not null && now - _labelUpdatedAt < 0.1)
            return;
        var text = _options.ScreensFor(_display).FirstOrDefault(s => screenId is null || s.Id == screenId)?.Name ?? "screen1";
        var cacheKey = $"{text}|{graphics.Width}|{graphics.Height}";
        if (cacheKey == _labelText)
            return;
        var data = ScreenNameLabel.Create(text, graphics.Width, graphics.Height);
        graphics.Context.PSUnsetShaderResource(0);
        _labelView?.Dispose();
        _labelTexture?.Dispose();
        fixed (byte* pixels = data.Pixels)
            _labelTexture = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)data.Width, (uint)data.Height, 1, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)pixels, (uint)(data.Width * 4)));
        _labelView = graphics.Device.CreateShaderResourceView(_labelTexture);
        _labelWidth = data.Width;
        _labelHeight = data.Height;
        _labelText = cacheKey;
        _labelUpdatedAt = now;
    }

    public void Dispose()
    {
        IDisposable?[] resources = [_overlayPs, _overlayConstants, _overlayBlend, _labelView, _labelTexture, _pointsView, _pointsBuffer];
        foreach (var resource in resources)
            resource?.Dispose();
    }
}
