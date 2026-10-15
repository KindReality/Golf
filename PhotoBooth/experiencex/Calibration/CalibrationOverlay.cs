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
    private ID3D11Texture2D? _hintTexture;
    private ID3D11ShaderResourceView? _hintView;
    private (int Width,int Height,string Type) _hintDisplay;
    private int _hintWidth,_hintHeight;
    private string? _labelText;
    private double _labelUpdatedAt;
    private int _labelWidth, _labelHeight;
    private int _labelOffset;
    private string? _mediaCaption;
    internal void SetMediaCaption(string? caption) => _mediaCaption=caption;
    public PointOptions? Preview
    {
        get; private set;
    }
    private int _selectedPoint = -1, _flashPointMask;
    private int _joinPoint = -1;
    private int? _singleFlash;
    private ID3D11Buffer? _pointsBuffer;
    private ID3D11ShaderResourceView? _pointsView;
    private PointOptions? _pointsShape;
    private long _flashRevision, _pointsFlashRevision = -1;
    private bool _selectedScreen, _helpSuppressed;
    internal void SuppressHelp(bool value) => _helpSuppressed = value;
    private double _flashAt = -1;
    private bool _flashSuccess;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayConstants
    {
        public Vector4 Screen, Selection, Grid, Row0, Row1, Row2, Label, Join, Hint;
    }
    internal bool SelectedScreen => _selectedScreen;
    public bool Animating(ExperienceOptions options) => options.ConfigurationMode || (_flashAt >= 0 && Now - _flashAt < 0.28);
    public bool Visible(ExperienceOptions options) => Animating(options);
    public void Configure(ExperienceOptions options)
    {
        if (!options.ConfigurationMode)
        {
            _selectedPoint = -1;
            _joinPoint = -1;
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
    public void ShowJoinTarget(int point) => _joinPoint = point;
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
        var definition = _options.ScreensFor(_display).FirstOrDefault(s => s.Id == screenId);
        var isVisualization = definition?.IsVisualization == true;
        var isMat = definition?.IsMat == true;
        var isLed = definition?.IsLed == true;
        UpdatePoints(graphics, k);
        var now = Now;
        var opacity = _options.ConfigurationMode && (!isVisualization || _selectedScreen) ? 1 : 0;
        var flash = _flashAt >= 0 && now >= _flashAt && now - _flashAt <= 0.28 ? (now - _flashAt) / 0.28 : -1;
        if (isVisualization && !_selectedScreen && flash < 0) return;
        var selectedScreen = _selectedScreen && _options.ConfigurationMode;
        var showTools = selectedScreen && _options.CalibrationGridEnabled && !isMat && !isVisualization && !isLed;
        if (selectedScreen)
            UpdateLabel(graphics, _options, _display, screenId, now);
        bool showHint = !_helpSuppressed && selectedScreen && (definition?.IsVideo == true || isMat);
        if (showHint) UpdateHint(graphics, definition!.Type);
        var inverse = isMat || isVisualization || isLed ? default : transforms.Get(k with
        {
            Enabled = true
        });
        var center = ScreenNameLabel.Center(k, graphics.Width, graphics.Height);
        var data = new OverlayConstants
        {
            Screen = new(graphics.Width, graphics.Height, (float)(now % 1000), (float)opacity),
            Selection = new(_selectedPoint, _flashPointMask, (float)flash, _flashSuccess ? 1 : 0),
            Grid = new(showTools ? 1 : 0, _labelWidth, _labelHeight, selectedScreen ? 1 : 0),
            Label = new(center.X, center.Y+_labelOffset, selectedScreen ? .16f : _options.ConfigurationMode && !isMat && !isVisualization ? .04f : 0, k.Vertices.Count),
            Row0 = inverse.Item1,
            Row1 = inverse.Item2,
            Row2 = inverse.Item3,
            Join = new(_options.ConfigurationMode && !_helpSuppressed ? _joinPoint : -1, 0, 0, 0),
            Hint = showHint ? new((graphics.Width-_hintWidth)/2f,Math.Max(0,graphics.Height-_hintHeight-18),_hintWidth,_hintHeight) : default
        };
        if (isLed && definition?.IsLedArray == false && selectedScreen)
        {
            var handle = LedGeometry.RotationHandle(k, graphics.Width, graphics.Height);
            data.Grid.X = -1;
            data.Row0 = new((float)handle.X, (float)handle.Y, (float)((k.TopLeft.X+k.TopRight.X)/2), (float)((k.TopLeft.Y+k.TopRight.Y)/2));
        }
        else if (definition?.IsLedArray == true)
        {
            data.Grid.X=-2;
            data.Row0.X=(float)(Math.Max(graphics.Width,graphics.Height)*definition.Perspective!.Led!.SamplingThicknessPercent/100);
        }
        graphics.Context.UpdateSubresource(in data, _overlayConstants);
        graphics.Context.RSSetViewport(0, 0, graphics.Width, graphics.Height);
        graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        graphics.Context.IASetInputLayout(null);
        graphics.Context.VSSetShader(graphics.VertexShader);
        graphics.Context.PSSetShader(_overlayPs);
        graphics.Context.PSSetConstantBuffer(0, _overlayConstants);
        graphics.Context.PSSetShaderResource(1, _pointsView!);
        if (showHint && _hintView is not null) graphics.Context.PSSetShaderResource(2,_hintView);
        else graphics.Context.PSUnsetShaderResource(2);
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
        graphics.Context.PSUnsetShaderResource(2);
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
        var cacheKey = $"{text}|{_mediaCaption}|{graphics.Width}|{graphics.Height}";
        if (cacheKey == _labelText)
            return;
        var data = ScreenNameLabel.Create(text, graphics.Width, graphics.Height,_mediaCaption);
        _labelOffset=data.OffsetY;
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

    private unsafe void UpdateHint(GraphicsResources graphics, string type)
    {
        if (_hintView is not null && _hintDisplay == (graphics.Width,graphics.Height,type)) return;
        var data=ScreenActionHint.Create(graphics.Width,graphics.Height,type);
        graphics.Context.PSUnsetShaderResource(2); _hintView?.Dispose(); _hintTexture?.Dispose();
        fixed(byte* pixels=data.Pixels)
            _hintTexture=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)data.Width,(uint)data.Height,1,1,BindFlags.ShaderResource),new SubresourceData((nint)pixels,(uint)(data.Width*4)));
        _hintView=graphics.Device.CreateShaderResourceView(_hintTexture);
        _hintDisplay=(graphics.Width,graphics.Height,type);_hintWidth=data.Width;_hintHeight=data.Height;
    }
    public void Dispose()
    {
        IDisposable?[] resources = [_overlayPs, _overlayConstants, _overlayBlend, _labelView, _labelTexture, _hintView, _hintTexture, _pointsView, _pointsBuffer];
        foreach (var resource in resources)
            resource?.Dispose();
    }
}
