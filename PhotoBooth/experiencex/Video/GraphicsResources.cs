using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PhotoBooth.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Created and disposed on the render worker. No UI thread may touch these resources.
internal sealed class GraphicsResources(nint _hwnd, int _width, int _height, string _display, RendererProgress? progress = null, bool debugLayer = false) : IDisposable
{
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain2 _swapChain = null!;
    private EventWaitHandle? _latency;
    private IDCompositionDevice _composition = null!;
    private IDCompositionTarget _target = null!;
    private IDCompositionVisual _visual = null!;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader _vs = null!;
    public ID3D11VertexShader VertexShader => _vs;
    private ID3D11PixelShader _ps = null!;
    private ID3D11PixelShader _calibrationCompositePs = null!;
    internal ID3D11RenderTargetView CurrentTarget { get; private set; } = null!;
    private ID3D11Buffer _constants = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _videoBlend = null!;
    private ID3D11BlendState _cutoutBlend = null!;
    private ID3D11Texture2D? _screenLayers;
    private ID3D11RenderTargetView? _screenLayersTarget;
    private ID3D11ShaderResourceView? _screenLayersView;
    private static readonly ExperienceOptions CompositeOptions = new();
    private static readonly BlackKeyOptions CompositeKey = new() { Enabled = false };
    private static readonly (Vector4, Vector4, Vector4) FullDisplay = VideoTransform.Inverse(new());
    private ID3D11VertexShader _maskVs = null!;
    private ID3D11VertexShader _polygonVideoVs = null!;
    private ID3D11PixelShader _maskPs = null!;
    private ID3D11InputLayout _polygonLayout = null!;
    private ID3D11RasterizerState _polygonRasterizer = null!;
    private IMFDXGIDeviceManager _deviceManager = null!;
    private ID3D11InfoQueue? _infoQueue;
    private long _nextDebugReport;
    private readonly HashSet<MessageId> _reportedMessages = [];
    internal bool CanPresent => progress?.IsSuspended != true;
    internal RendererProgress.OperationScope? Operation(string name) => progress?.Begin(name, progress.OperationScreenId);
    private ID3D11Texture2D? _videoTexture;
    private ID3D11ShaderResourceView? _videoView;
    public ID3D11Device Device { get { AssertOwnerThread(); return _device; } }
    public ID3D11DeviceContext Context { get { AssertOwnerThread(); return _context; } }
    public IDXGISwapChain2 SwapChain { get { AssertOwnerThread(); return _swapChain; } }
    public EventWaitHandle? Latency => _latency;
    public IDCompositionDevice Composition => _composition;
    public ID3D11SamplerState Sampler => _sampler;
    public IMFDXGIDeviceManager DeviceManager => _deviceManager;
    public ID3D11Texture2D? VideoTexture => _videoTexture;
    public ID3D11ShaderResourceView? VideoView => _videoView;
    private int _ownerThread;
    public int Width => _width;
    public int Height => _height;
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Row0, Row1, Row2, Effects, Aspect, Crop, Rotation, SourceBounds;
    }
    public void Initialize()
    {
        _ownerThread = Environment.CurrentManagedThreadId;
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            flags | (debugLayer ? DeviceCreationFlags.Debug : 0), [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context);
        if (debugLayer && unchecked((uint)result.Code) == 0x887A002D)
        {
            Telemetry.Warning("GraphicsDebugLayerUnavailable", new { DisplayName = _display, Reason = "Windows Graphics Tools is not installed; using the normal device." });
            result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context);
            debugLayer = false;
        }
        result.CheckError();
        if (debugLayer) { _infoQueue = _device.QueryInterface<ID3D11InfoQueue>(); _infoQueue.MessageCountLimit = 128; }
        using (var multithread = _device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);
        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        };
        using var swap1 = factory.CreateSwapChainForComposition(_device, description, null);
        _swapChain = swap1.QueryInterface<IDXGISwapChain2>();
        _swapChain.MaximumFrameLatency = 1;
        _latency = new EventWaitHandle(false, EventResetMode.AutoReset);
        _latency.SafeWaitHandle = new SafeWaitHandle(_swapChain.FrameLatencyWaitableObject, true);
        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        _composition.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
        _visual = _composition.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();
        _target.SetRoot(_visual).CheckError();
        _composition.Commit().CheckError();
        _vs = _device.CreateVertexShader(Shader("VideoVS.cso"));
        _ps = _device.CreatePixelShader(Shader("VideoPS.cso"));
        _calibrationCompositePs = _device.CreatePixelShader(Shader("CalibrationCompositePS.cso"));
        _constants = _device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _videoBlend = CreateVideoBlendState(_device);
        var cutout = BlendDescription.Opaque;
        cutout.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.Zero, DestinationBlend = Blend.Zero, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero, DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _cutoutBlend = _device.CreateBlendState(cutout);
        var maskShader = Shader("MaskVS.cso");
        _maskVs = _device.CreateVertexShader(maskShader);
        _polygonVideoVs = _device.CreateVertexShader(Shader("PolygonVS.cso"));
        _maskPs = _device.CreatePixelShader(Shader("MaskPS.cso"));
        _polygonLayout = _device.CreateInputLayout([new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0)], maskShader);
        _polygonRasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(_device).CheckError();
        CreateTarget();
        Telemetry.Info("DirectXRendererReady", new
        {
            DisplayName = _display,
            Adapter = adapter.Description.Description,
            Renderer = "D3D11 + DirectComposition + Media Foundation",
            FrameLatency = 1,
            PremultipliedAlpha = true
        });
    }
    internal static byte[] Shader(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name)))!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
    private void CreateTarget()
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(buffer);
    }

    public void Resize(int width, int height)
    {
        using var operation = progress?.Begin("ResizeBuffers");
        AssertOwnerThread();
        if (_width == width && _height == height)
            return;
        _width = width;
        _height = height;
        _context.UnsetRenderTargets();
        ReleaseScreenLayers();
        _rtv?.Dispose();
        _rtv = null;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        CreateTarget();
    }
    public void CreateVideoTexture(int width, int height)
    {
        AssertOwnerThread();
        _videoView?.Dispose();
        _videoTexture?.Dispose();
        _videoTexture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _videoView = _device.CreateShaderResourceView(_videoTexture);
    }
    internal static ID3D11BlendState CreateVideoBlendState(ID3D11Device device)
    {
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
        return device.CreateBlendState(blend);
    }
    public void DrawVideo(double opacity, ExperienceOptions options, (Vector4, Vector4, Vector4) transform, int _videoWidth, int _videoHeight, Color4 background)
    {
        BeginFrame(background);
        DrawLayer(_videoView, opacity, options, transform);
    }
    public void BeginFrame(Color4 background)
    {
        AssertOwnerThread();
        using (var operation = Operation("BindDisplayTarget")) BindTarget(_rtv!);
        using (var operation = Operation("ClearDisplayTarget")) _context.ClearRenderTargetView(_rtv!, background);
    }
    // Mats erase only the ordered media layers. Background and the owned G-Force mask stay below this target.
    internal void BeginScreenLayers()
    {
        using var operation = Operation("PrepareScreenLayers");
        AssertOwnerThread();
        _context.PSUnsetShaderResource(0);
        if (_screenLayers is null)
        {
            _screenLayers = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)_width, (uint)_height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            _screenLayersTarget = _device.CreateRenderTargetView(_screenLayers);
            _screenLayersView = _device.CreateShaderResourceView(_screenLayers);
        }
        BindTarget(_screenLayersTarget!);
        _context.ClearRenderTargetView(_screenLayersTarget!, new Color4(0, 0, 0, 0));
    }
    internal void FinishScreenLayers()
    {
        using var operation = Operation("CompositeScreenLayers");
        AssertOwnerThread();
        BindTarget(_rtv!);
        DrawLayer(_screenLayersView, 1, CompositeOptions, FullDisplay, CompositeKey, sourceAlpha: true);
        _context.PSUnsetShaderResource(0);
    }
    private void ReleaseScreenLayers()
    {
        _context.PSUnsetShaderResource(0);
        _screenLayersView?.Dispose(); _screenLayersView = null;
        _screenLayersTarget?.Dispose(); _screenLayersTarget = null;
        _screenLayers?.Dispose(); _screenLayers = null;
    }
    internal void BindTarget(ID3D11RenderTargetView target)
    {
        AssertOwnerThread();
        _context.OMSetRenderTargets(target);
        CurrentTarget = target;
    }
    internal void CompositeCalibration(ID3D11ShaderResourceView view, System.Drawing.Rectangle bounds)
    {
        AssertOwnerThread();
        _context.RSSetViewport(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_calibrationCompositePs);
        _context.PSSetShaderResource(0, view);
        _context.PSSetSampler(0, _sampler);
        _context.OMSetBlendState(_videoBlend);
        try { _context.Draw(3, 0); }
        finally { _context.PSUnsetShaderResource(0); _context.OMSetBlendState(null); }
    }
    public void DrawLayer(ID3D11ShaderResourceView? view, double opacity, ExperienceOptions options, (Vector4, Vector4, Vector4) transform, BlackKeyOptions? blackKey = null, bool sourceAlpha = false, double brightness = 100, PhotoBooth.Networking.PerspectiveRectangle? perspective = null, VideoFramingOptions? framing = null, double screenAspect = 1, PolygonMesh? outline = null)
    {
        AssertOwnerThread();

        using (var operation = Operation("BindVideoBlendState")) _context.OMSetBlendState(_videoBlend);
        if (view is not null && opacity > 0)
        {
            var key = blackKey ?? options.BlackKey;
            var (r0, r1, r2) = transform;
            var bounds = perspective ?? new();
            var crop = framing?.Crop(bounds) ?? bounds;
            var data = new Constants
            {
                Row0 = r0,
                Row1 = r1,
                Row2 = r2,
                Effects = new((float)opacity, (float)key.Threshold, (float)key.Softness, key.Enabled ? 1 : 0),
                Aspect = new(1, 1, 0, 0),
                Crop = new((float)(crop.X / 100), (float)(crop.Y / 100), (float)(crop.Width / 100), (float)(crop.Height / 100)),
                Rotation = framing?.SamplingRotation(screenAspect) ?? new(1, 0, 0, 1),
                SourceBounds = new((float)(bounds.X / 100), (float)(bounds.Y / 100), (float)((bounds.X + bounds.Width) / 100), (float)((bounds.Y + bounds.Height) / 100))
            };
            data.Aspect.Z = sourceAlpha ? 1 : 0;
            data.Aspect.W = (float)(brightness / 100);
            using (var operation = Operation("UploadVideoConstants")) _context.UpdateSubresource(in data, _constants);
            using var pipelineOperation = Operation("BindVideoPipeline");
            _context.RSSetViewport(0, 0, _width, _height);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(outline is null ? null : _polygonLayout);
            _context.VSSetShader(outline is null ? _vs : _polygonVideoVs);
            _context.PSSetShader(_ps);
            _context.PSSetConstantBuffer(0, _constants);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetShaderResource(0, view);
            if (outline is null) { using var operation = Operation("DrawVideoTriangles"); _context.Draw(3, 0); }
            else
            {
                _context.RSSetState(_polygonRasterizer);
                _context.IASetVertexBuffer(0,outline.Vertices!,8);
                using (var operation = Operation("DrawVideoPolygon")) _context.Draw((uint)outline.Count,0);
                _context.IASetVertexBuffer(0,null!,0);
                _context.IASetInputLayout(null);
                _context.RSSetState(null);
            }
        }
    }
    internal void DrawMask(ID3D11Buffer vertices, int count, bool cutout)
    {
        using var operation = Operation("DrawMaskTriangles");
        AssertOwnerThread();
        _context.PSUnsetShaderResource(0);
        _context.OMSetBlendState(cutout ? _cutoutBlend : null);
        _context.RSSetState(_polygonRasterizer);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_polygonLayout);
        _context.IASetVertexBuffer(0, vertices, 8);
        _context.VSSetShader(_maskVs);
        _context.PSSetShader(_maskPs);
        _context.Draw((uint)count, 0);
        _context.IASetVertexBuffer(0, null!, 0);
        _context.IASetInputLayout(null);
        _context.RSSetState(null);
    }
    public void CommitAndWait()
    {
        AssertOwnerThread();
        using (var operation = progress?.Begin("CompositionCommit")) _composition.Commit().CheckError();
        using (var operation = progress?.Begin("CompositionCommitWait")) _composition.WaitForCommitCompletion().CheckError();
    }
    private void AssertOwnerThread()
    {
        if (_ownerThread != 0 && Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Graphics resources must remain on the render worker.");
    }
    internal void ReportDebugMessages()
    {
        AssertOwnerThread();
        if (_infoQueue is null || Environment.TickCount64 < _nextDebugReport) return;
        _nextDebugReport = Environment.TickCount64 + 1000;
        using var operation = Operation("ReadGraphicsValidationMessages");
        var count = Math.Min(_infoQueue.NumStoredMessages, 128UL);
        for (ulong i = 0; i < count; i++)
        {
            var message = _infoQueue.GetMessage(i);
            if (message.Severity is MessageSeverity.Warning or MessageSeverity.Error or MessageSeverity.Corruption && _reportedMessages.Add(message.Id))
                Telemetry.Warning("GraphicsValidationMessage", new { DisplayName = _display, Severity = message.Severity.ToString(), Id = message.Id.ToString(), message.Description });
        }
        _infoQueue.ClearStoredMessages();
    }
    public void Dispose()
    {
        AssertOwnerThread();
        IDisposable?[] resources = [_infoQueue, _screenLayersView, _screenLayersTarget, _screenLayers, _videoView, _videoTexture, _rtv, _polygonRasterizer, _polygonLayout, _maskPs, _maskVs, _polygonVideoVs, _calibrationCompositePs, _cutoutBlend, _videoBlend, _sampler, _constants, _ps, _vs, _latency, _visual, _target, _composition, _swapChain, _deviceManager, _context, _device];
        foreach (var resource in resources)
            try
            {
                resource?.Dispose();
            }
            catch (Exception ex) { Telemetry.Warning("GraphicsResourceCleanupFailed", new { DisplayName = _display, Reason = ex.Message }); }
    }
}
