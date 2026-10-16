using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExperienceX;

internal sealed class CalibrationOverlay(Func<double>? clock = null) : IDisposable
{
    private ID3D11PixelShader _staticPs = null!, _pointPs = null!;
    private ID3D11Buffer _overlayConstants = null!;
    private ID3D11Buffer _regionConstants = null!;
    private ID3D11BlendState _overlayBlend = null!;
    private readonly CalibrationTexture _staticCache = new();
    private OverlayConstants? _staticData;
    private PointOptions? _staticShape, _gridShape, _gridBounds;
    private string? _staticLabelText;
    private (int Width,int Height,string Type) _staticHintDisplay;
    private System.Drawing.Rectangle _staticBounds;
    internal int StaticRebuilds { get; private set; }
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
    private enum PointFlash { Failed, Saved, Selected }
    private PointFlash _flashKind;
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private double Now => _clock();
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
            _staticCache.Dispose(); _staticData = null;
            _selectedPoint = -1;
            _joinPoint = -1;
            _selectedScreen = false;
            Preview = null;
            if (_flashKind == PointFlash.Selected) _flashAt = -1;
        }
    }
    public void Show(int point)
    {
        _selectedPoint = point;
    }
    public void Fade()
    {
        _selectedPoint = -1;
    }
    public void SelectScreen(bool selected)
    {
        if (selected && !_selectedScreen) StartFlash(-1, null, PointFlash.Selected);
        if (!selected && _flashKind == PointFlash.Selected) _flashAt = -1;
        _selectedScreen = selected;
    }
    public void ShowJoinTarget(int point) => _joinPoint = point;
    public void SetPreview(PointOptions? preview) => Preview = preview;
    public void Flash(int mask, bool success)
    {
        StartFlash(success ? -1 : mask, null, success ? PointFlash.Saved : PointFlash.Failed);
    }
    public void FlashPoint(int point, bool success)
    {
        StartFlash(success ? -1 : 0, success ? null : point, success ? PointFlash.Saved : PointFlash.Failed);
    }
    private void StartFlash(int mask, int? point, PointFlash kind)
    {
        _singleFlash = point;
        _flashPointMask = mask;
        _flashKind = kind;
        _flashAt = Now;
        _flashRevision++;
    }
    public void Initialize(GraphicsResources graphics)
    {
        _staticPs = graphics.Device.CreatePixelShader(GraphicsResources.Shader("CalibrationStaticPS.cso"));
        _pointPs = graphics.Device.CreatePixelShader(GraphicsResources.Shader("CalibrationPointPS.cso"));
        _overlayConstants = graphics.Device.CreateBuffer((uint)Marshal.SizeOf<OverlayConstants>(), BindFlags.ConstantBuffer);
        _regionConstants = graphics.Device.CreateBuffer(16, BindFlags.ConstantBuffer);
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
    public void Draw(GraphicsResources graphics, ExperienceOptions _options, string _display, PointTransformCache transforms, string? screenId = null, ScreenOptions? definition = null)
    {
        definition ??= _options.ScreensFor(_display).FirstOrDefault(s => s.Id == screenId);
        var k = Preview ?? definition?.Points ?? _options.PointsFor(_display, screenId);
        var isVisualization = definition?.IsVisualization == true;
        var isMat = definition?.IsMat == true;
        var isLed = definition?.IsLed == true;
        UpdatePoints(graphics, k);
        var now = Now;
        var opacity = _options.ConfigurationMode && (!isVisualization || _selectedScreen) ? 1 : 0;
        var flash = _flashAt >= 0 && now >= _flashAt && now - _flashAt <= 0.28 ? (now - _flashAt) / 0.28 : -1;
        if (isVisualization && !_selectedScreen && flash < 0) return;
        var selectedScreen = _selectedScreen && _options.ConfigurationMode;
        var showTools = selectedScreen && _options.CalibrationGridEnabled && !isLed;
        if (selectedScreen)
            UpdateLabel(graphics, _options, _display, screenId, now, definition);
        bool showHint = !_helpSuppressed && selectedScreen && (definition?.IsVideo == true || isMat);
        if (showHint) UpdateHint(graphics, definition!.Type);
        // Masks can have any number of points, including concave outlines. Keep their grid
        // rectangular within the polygon's bounds without treating its vertices as keystone anchors.
        var gridPoints = k;
        if (showTools && (isMat || isVisualization))
        {
            if (_gridShape != k)
            {
                var left = k.Vertices.Min(p => p.X); var right = k.Vertices.Max(p => p.X);
                var top = k.Vertices.Min(p => p.Y); var bottom = k.Vertices.Max(p => p.Y);
                _gridBounds = new() { Enabled = true, TopLeft = new(left, top), TopRight = new(right, top),
                    BottomRight = new(right, bottom), BottomLeft = new(left, bottom) };
                _gridShape = k;
            }
            gridPoints = _gridBounds!;
        }
        var inverse = !showTools ? default : transforms.Get(gridPoints.Enabled ? gridPoints : gridPoints with { Enabled = true });
        var center = ScreenNameLabel.Center(k, graphics.Width, graphics.Height);
        var data = new OverlayConstants
        {
            Screen = new(graphics.Width, graphics.Height, (float)(now % 1000), (float)opacity),
            Selection = new(_selectedPoint, _flashPointMask, (float)flash, (float)_flashKind),
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
        var fixedData = data;
        fixedData.Screen.Z = 0; fixedData.Selection = default; fixedData.Join = default;
        if (opacity > 0 || data.Label.Z > 0 || showTools || showHint)
        {
            var bounds = StaticBounds(graphics, k, data);
            bool resized = _staticCache.Ensure(graphics, bounds.Width, bounds.Height);
            if (resized || _staticShape != k || _staticData is null || !_staticData.Value.Equals(fixedData) ||
                _staticBounds != bounds || _staticLabelText != _labelText || _staticHintDisplay != _hintDisplay)
            {
                _staticData = null;
                using var operation = graphics.Operation("RefreshStaticCalibration");
                _staticCache.Render(graphics, () =>
                {
                    graphics.Context.UpdateSubresource(in fixedData, _overlayConstants);
                    Bind(graphics, _staticPs, new(0, 0, bounds.Width, bounds.Height));
                    var region = new Vector4(bounds.Left, bounds.Top, 0, 0);
                    graphics.Context.UpdateSubresource(in region, _regionConstants);
                    graphics.Context.Draw(3, 0);
                });
                _staticData = fixedData; _staticShape = k; _staticBounds = bounds;
                _staticLabelText = _labelText; _staticHintDisplay = _hintDisplay; StaticRebuilds++;
            }
            _staticCache.Composite(graphics, bounds);
        }
        graphics.Context.UpdateSubresource(in data, _overlayConstants);
        try
        {
            Bind(graphics, _pointPs, new(0, 0, graphics.Width, graphics.Height));
            // Each orb evaluates only its own point in a small pixel rectangle. Boundary order
            // preserves the original premultiplied overlap, including coincident points.
            for (int boundary = 0; boundary < k.Vertices.Count; boundary++)
            {
                var point = k.Vertices[boundary];
                var index = PointEditing.BoundaryIndex(k, boundary);
                bool flashes = flash >= 0 && (_singleFlash.HasValue ? _singleFlash == index : _flashPointMask == -1 || index < 32 && (_flashPointMask & (1 << index)) != 0);
                if (opacity == 0 && !flashes) continue;
                var bounds = PointBounds(graphics, point, index, flashes, data);
                graphics.Context.RSSetViewport(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                var region = new Vector4(0, 0, boundary, 0);
                graphics.Context.UpdateSubresource(in region, _regionConstants);
                using var operation = graphics.Operation("DrawCalibrationPoint");
                graphics.Context.Draw(3, 0);
            }
        }
        finally
        {
            graphics.Context.OMSetBlendState(null);
            graphics.Context.PSUnsetShaderResource(0);
            graphics.Context.PSUnsetShaderResource(1);
            graphics.Context.PSUnsetShaderResource(2);
        }
    }
    private void Bind(GraphicsResources graphics, ID3D11PixelShader shader, System.Drawing.Rectangle bounds)
    {
        graphics.Context.RSSetViewport(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        graphics.Context.IASetInputLayout(null);
        graphics.Context.VSSetShader(graphics.VertexShader);
        graphics.Context.PSSetShader(shader);
        graphics.Context.PSSetConstantBuffer(0, _overlayConstants);
        graphics.Context.PSSetConstantBuffer(1, _regionConstants);
        graphics.Context.PSSetShaderResource(1, _pointsView!);
        if (_hintView is not null) graphics.Context.PSSetShaderResource(2,_hintView);
        else graphics.Context.PSUnsetShaderResource(2);
        if (_labelView is not null)
            graphics.Context.PSSetShaderResource(0, _labelView);
        else
            graphics.Context.PSUnsetShaderResource(0);
        graphics.Context.PSSetSampler(0, graphics.Sampler);
        graphics.Context.OMSetBlendState(_overlayBlend);
    }
    private static System.Drawing.Rectangle Clip(GraphicsResources g, double left, double top, double right, double bottom)
    {
        int x = (int)Math.Clamp(Math.Floor(left), 0, g.Width - 1), y = (int)Math.Clamp(Math.Floor(top), 0, g.Height - 1);
        int r = (int)Math.Clamp(Math.Ceiling(right), x + 1, g.Width), b = (int)Math.Clamp(Math.Ceiling(bottom), y + 1, g.Height);
        return System.Drawing.Rectangle.FromLTRB(x, y, r, b);
    }
    private static System.Drawing.Rectangle StaticBounds(GraphicsResources g, PointOptions points, OverlayConstants data)
    {
        // Glows below half an 8-bit alpha step can be clipped without visible hard edges.
        double padding = 20 + (data.Grid.X == -2 ? data.Row0.X / 2 : 0);
        double left = points.Vertices.Min(p => p.X) * (g.Width - 1) - padding, right = points.Vertices.Max(p => p.X) * (g.Width - 1) + padding;
        double top = points.Vertices.Min(p => p.Y) * (g.Height - 1) - padding, bottom = points.Vertices.Max(p => p.Y) * (g.Height - 1) + padding;
        if (data.Grid.X == -1)
        {
            left = Math.Min(left, data.Row0.X * (g.Width - 1) - 20); right = Math.Max(right, data.Row0.X * (g.Width - 1) + 20);
            top = Math.Min(top, data.Row0.Y * (g.Height - 1) - 20); bottom = Math.Max(bottom, data.Row0.Y * (g.Height - 1) + 20);
        }
        if (data.Grid.W > 0)
        {
            left = Math.Min(left, data.Label.X - data.Grid.Y / 2); right = Math.Max(right, data.Label.X + data.Grid.Y / 2);
            top = Math.Min(top, data.Label.Y - data.Grid.Z / 2); bottom = Math.Max(bottom, data.Label.Y + data.Grid.Z / 2);
        }
        if (data.Hint.W > 0)
        {
            left = Math.Min(left, data.Hint.X); right = Math.Max(right, data.Hint.X + data.Hint.Z);
            top = Math.Min(top, data.Hint.Y); bottom = Math.Max(bottom, data.Hint.Y + data.Hint.W);
        }
        return Clip(g, left, top, right, bottom);
    }
    private System.Drawing.Rectangle PointBounds(GraphicsResources g, ScreenPoint point, int index, bool flashes, OverlayConstants data)
    {
        double x = point.X * (g.Width - 1), y = point.Y * (g.Height - 1);
        double radius = Math.Max(8, Math.Min(g.Width, g.Height) * .014) * 1.12;
        double padding = Math.Max(radius * 5, flashes ? 144 : index == _joinPoint ? 112 : 0);
        double left = x - padding, right = x + padding, top = y - padding, bottom = y + padding;
        if (index == _selectedPoint)
        {
            var labelX = Math.Clamp(x + 24, 12, g.Width - 12); var labelY = Math.Clamp(y - 22, 12, g.Height - 12);
            left = Math.Min(left, labelX - 9 * (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            right = Math.Max(right, labelX + 5); top = Math.Min(top, labelY - 9); bottom = Math.Max(bottom, labelY + 9);
        }
        if (index == (int)data.Join.X && data.Screen.W > 0)
        {
            var scale = Math.Max(1, Math.Min(g.Width, g.Height) / 720.0);
            var atX = Math.Clamp(x, 12 * scale, g.Width - 12 * scale);
            var atY = Math.Clamp(y - Math.Max(34, radius * 2.2), 12 * scale, g.Height - 12 * scale);
            left = Math.Min(left, atX - 8 * scale); right = Math.Max(right, atX + 8 * scale);
            top = Math.Min(top, atY - 10 * scale); bottom = Math.Max(bottom, atY + 10 * scale);
        }
        return Clip(g, left, top, right, bottom);
    }
    private void UpdatePoints(GraphicsResources graphics, PointOptions shape)
    {
        if (_pointsShape == shape && _pointsFlashRevision == _flashRevision) return;
        using var operation = graphics.Operation("CreateOverlayPointBuffer");
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
    private unsafe void UpdateLabel(GraphicsResources graphics, ExperienceOptions _options, string _display, string? screenId, double now, ScreenOptions? definition)
    {
        // Text is static between changes and capped at ten updates/second during a drag.
        if (_labelView is not null && now - _labelUpdatedAt < 0.1)
            return;
        var text = definition?.Name ?? _options.ScreensFor(_display).FirstOrDefault(s => screenId is null || s.Id == screenId)?.Name ?? "screen1";
        var cacheKey = $"{text}|{_mediaCaption}|{graphics.Width}|{graphics.Height}";
        if (cacheKey == _labelText)
            return;
        using var operation = graphics.Operation("CreateOverlayLabelTexture");
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
        using var operation = graphics.Operation("CreateOverlayHintTexture");
        var data=ScreenActionHint.Create(graphics.Width,graphics.Height,type);
        graphics.Context.PSUnsetShaderResource(2); _hintView?.Dispose(); _hintTexture?.Dispose();
        fixed(byte* pixels=data.Pixels)
            _hintTexture=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)data.Width,(uint)data.Height,1,1,BindFlags.ShaderResource),new SubresourceData((nint)pixels,(uint)(data.Width*4)));
        _hintView=graphics.Device.CreateShaderResourceView(_hintTexture);
        _hintDisplay=(graphics.Width,graphics.Height,type);_hintWidth=data.Width;_hintHeight=data.Height;
    }
    public void Dispose()
    {
        IDisposable?[] resources = [_staticCache, _staticPs, _pointPs, _regionConstants, _overlayConstants, _overlayBlend, _labelView, _labelTexture, _hintView, _hintTexture, _pointsView, _pointsBuffer];
        foreach (var resource in resources)
            resource?.Dispose();
    }
}
