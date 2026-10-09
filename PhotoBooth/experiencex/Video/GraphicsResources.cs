using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PhotoBooth.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Created and disposed on the render worker. No UI thread may touch these resources.
internal sealed class GraphicsResources(nint _hwnd, int _width, int _height, string _monitor) : IDisposable
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
    private ID3D11Buffer _constants = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _videoBlend = null!;
    private IMFDXGIDeviceManager _deviceManager = null!;
    private ID3D11Texture2D? _videoTexture;
    private ID3D11ShaderResourceView? _videoView;
    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;
    public IDXGISwapChain2 SwapChain => _swapChain;
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
        public Vector4 Row0, Row1, Row2, Effects, Aspect;
    }
    public void Initialize()
    {
        _ownerThread = Environment.CurrentManagedThreadId;
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device, out _context).CheckError();
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
        _constants = _device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _videoBlend = CreateVideoBlendState(_device);
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(_device).CheckError();
        CreateTarget();
        Telemetry.Info("DirectXRendererReady", new
        {
            MonitorName = _monitor,
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
        AssertOwnerThread();
        if (_width == width && _height == height)
            return;
        _width = width;
        _height = height;
        _context.UnsetRenderTargets();
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
        AssertOwnerThread();
        _context.OMSetRenderTargets(_rtv!);
        _context.ClearRenderTargetView(_rtv!, background);
        _context.OMSetBlendState(_videoBlend);
        if (_videoView is not null && opacity > 0)
        {
            var (r0, r1, r2) = transform;
            var videoAspect = (float)_videoWidth / _videoHeight;
            var targetAspect = (float)_width / _height;
            var data = new Constants
            {
                Row0 = r0,
                Row1 = r1,
                Row2 = r2,
                Effects = new((float)opacity, (float)options.BlackKey.Threshold, (float)options.BlackKey.Softness, options.BlackKey.Enabled ? 1 : 0),
                Aspect = new(Math.Min(1, videoAspect / targetAspect), Math.Min(1, targetAspect / videoAspect), 0, 0)
            };
            _context.UpdateSubresource(in data, _constants);
            _context.RSSetViewport(0, 0, _width, _height);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetConstantBuffer(0, _constants);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetShaderResource(0, _videoView);
            _context.Draw(3, 0);
        }
    }
    public void CommitAndWait()
    {
        _composition.Commit().CheckError();
        _composition.WaitForCommitCompletion().CheckError();
    }
    [Conditional("DEBUG")]
    private void AssertOwnerThread()
    {
        if (_ownerThread != 0 && Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Graphics resources must remain on the render worker.");
    }
    public void Dispose()
    {
        AssertOwnerThread();
        IDisposable?[] resources = [_videoView, _videoTexture, _rtv, _videoBlend, _sampler, _constants, _ps, _vs, _latency, _visual, _target, _composition, _swapChain, _deviceManager, _context, _device];
        foreach (var resource in resources)
            try
            {
                resource?.Dispose();
            }
            catch (Exception ex) { Telemetry.Warning("GraphicsResourceCleanupFailed", new { MonitorName = _monitor, Reason = ex.Message }); }
    }
}
