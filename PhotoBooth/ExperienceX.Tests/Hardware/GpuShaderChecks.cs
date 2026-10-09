using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ExperienceX;

internal static class GpuShaderChecks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Row0, Row1, Row2, Effects, Aspect;
    }
    public static void Run()
    {
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        {
            byte[] Shader(string suffix)
            {
                var assembly = typeof(VideoRenderer).Assembly;
                using var s = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix)))!;
                using var output = new System.IO.MemoryStream();
                s.CopyTo(output);
                return output.ToArray();
            }
            // BGRA: black/red on top, blue/white below.
            byte[] pixels = [0, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 255, 255, 255];
            using var source = device.CreateTexture2D<byte>(pixels, Format.B8G8R8A8_UNorm, 2, 2, 1, 1, BindFlags.ShaderResource);
            using var srv = device.CreateShaderResourceView(source);
            using var target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 16, 16, 1, 1, BindFlags.RenderTarget));
            using var staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 16, 16, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            using var rtv = device.CreateRenderTargetView(target);
            using var vs = device.CreateVertexShader(Shader("VideoVS.cso"));
            using var ps = device.CreatePixelShader(Shader("VideoPS.cso"));
            using var sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
            using var buffer = device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer);
            using var videoBlend = GraphicsResources.CreateVideoBlendState(device);
            context.OMSetRenderTargets(rtv);
            context.RSSetViewport(0, 0, 16, 16);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vs);
            context.PSSetShader(ps);
            context.PSSetShaderResource(0, srv);
            context.PSSetSampler(0, sampler);
            context.PSSetConstantBuffer(0, buffer);
            byte[] Render(float opacity, bool key, KeystoneOptions? keystone = null, Color4? background = null)
            {
                context.ClearRenderTargetView(rtv, background ?? new Color4(0, 0, 0, 0));
                context.OMSetBlendState(videoBlend);
                var (r0, r1, r2) = VideoTransform.Inverse(keystone ?? new());
                var data = new Constants { Row0 = r0, Row1 = r1, Row2 = r2, Effects = new(opacity, 0.015f, 0.03f, key ? 1 : 0), Aspect = new(1, 1, 0, 0) };
                context.UpdateSubresource(in data, buffer);
                context.Draw(3, 0);
                context.CopyResource(staging, target);
                var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var result = new byte[16 * 16 * 4];
                    for (int y = 0; y < 16; y++)
                        Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, result, y * 64, 64);
                    return result;
                }
                finally { context.Unmap(staging, 0); }
            }
            int Alpha(byte[] b, int x, int y) => b[(y * 16 + x) * 4 + 3];
            var faded = Render(0.25f, true);
            if (Alpha(faded, 3, 3) != 0 || Math.Abs(Alpha(faded, 12, 3) - 64) > 1 || Math.Abs(faded[(3 * 16 + 12) * 4 + 2] - 64) > 1)
                throw new Exception("GPU black key or premultiplied fade produced incorrect pixels.");
            if (Render(0, true).Any(b => b != 0))
                throw new Exception("GPU zero-opacity surface was not completely transparent.");
            if (Alpha(Render(1, false), 3, 3) != 255)
                throw new Exception("Disabling black key did not preserve opaque black.");
            var warped = Render(1, false, new KeystoneOptions { Enabled = true, TopLeft = new(0.25, 0.25), TopRight = new(0.75, 0.25), BottomLeft = new(0.25, 0.75), BottomRight = new(0.75, 0.75) });
            if (Alpha(warped, 1, 1) != 0 || Alpha(warped, 8, 8) != 255)
                throw new Exception("GPU keystone did not clip outside its quadrilateral.");
            var backdrop = MonitorBackground.Parse("#204060");
            var idle = Render(0, true, background: backdrop);
            for (int p = 0; p < idle.Length; p += 4)
                if (idle[p] != 96 || idle[p + 1] != 64 || idle[p + 2] != 32 || idle[p + 3] != 255)
                    throw new Exception("Zero-opacity video did not preserve the opaque monitor background.");
            var overBackground = Render(0.25f, true, background: backdrop);
            int black = (3 * 16 + 3) * 4, red = (3 * 16 + 12) * 4;
            if (overBackground[black] != 96 || overBackground[black + 1] != 64 || overBackground[black + 2] != 32 || overBackground[black + 3] != 255 ||
                Math.Abs(overBackground[red] - 72) > 1 || Math.Abs(overBackground[red + 1] - 48) > 1 || Math.Abs(overBackground[red + 2] - 88) > 1 || overBackground[red + 3] != 255)
                throw new Exception("Premultiplied video fade or black-key transparency did not composite over the background correctly.");
            var outsideKeystone = Render(1, false, new KeystoneOptions { Enabled = true, TopLeft = new(.25, .25), TopRight = new(.75, .25), BottomLeft = new(.25, .75), BottomRight = new(.75, .75) }, backdrop);
            if (outsideKeystone[0] != 96 || outsideKeystone[1] != 64 || outsideKeystone[2] != 32 || outsideKeystone[3] != 255)
                throw new Exception("Keystone clipping erased the full-monitor background.");
            Console.WriteLine("GPU readback passed black key, premultiplied fades, transparent clear, and keystone clipping.");
            Console.WriteLine("GPU readback passed solid monitor backgrounds, video fades, keyed pixels and background outside keystone bounds.");
            Overlay(device, context, vs, Shader);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayData
    {
        public Vector4 P0, P1, Screen, Selection, Grid, Row0, Row1, Row2;
    }
    private static void Overlay(ID3D11Device device, ID3D11DeviceContext context, ID3D11VertexShader vs, Func<string, byte[]> shader)
    {
        const int w = 640, h = 360;
        using var target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1, BindFlags.RenderTarget));
        using var staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        using var rtv = device.CreateRenderTargetView(target);
        using var ps = device.CreatePixelShader(shader("CalibrationPS.cso"));
        using var constants = device.CreateBuffer((uint)Marshal.SizeOf<OverlayData>(), BindFlags.ConstantBuffer);
        context.OMSetRenderTargets(rtv);
        context.OMSetBlendState(null);
        context.RSSetViewport(0, 0, w, h);
        context.VSSetShader(vs);
        context.PSSetShader(ps);
        context.PSSetConstantBuffer(0, constants);
        byte[] Render(float opacity, float time, int flash = 0, float progress = -1, bool grid = false)
        {
            var data = new OverlayData { P0 = new(.15f, .15f, .85f, .15f), P1 = new(.15f, .85f, .85f, .85f), Screen = new(w, h, time, opacity), Selection = new(0, flash, progress, 1) };
            if (grid)
            {
                var inverse = VideoTransform.Inverse(new KeystoneOptions { Enabled = true, TopLeft = new(.15, .15), TopRight = new(.85, .15), BottomLeft = new(.15, .85), BottomRight = new(.85, .85) });
                data.Grid = new(1, 0, 0, 0);
                data.Row0 = inverse.Item1;
                data.Row1 = inverse.Item2;
                data.Row2 = inverse.Item3;
            }
            context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 0));
            context.UpdateSubresource(in data, constants);
            context.Draw(3, 0);
            context.CopyResource(staging, target);
            var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, bytes, y * w * 4, w * 4);
                return bytes;
            }
            finally { context.Unmap(staging, 0); }
        }
        int At(int x, int y, int channel) => (y * w + x) * 4 + channel;
        var image = Render(1, 1);
        if (image[At(96, 54, 0)] <= image[At(96, 54, 2)] || image[At(543, 54, 2)] <= image[At(543, 54, 0)])
            throw new Exception("Selected/unselected GPU orb colors are wrong.");
        if (image[At(320, 54, 0)] <= image[At(320, 54, 2)] || image[At(320, 180, 3)] > 2)
            throw new Exception("GPU calibration lines did not form the perimeter, or crossed the interior.");
        if (Render(0, 1).Any(v => v != 0))
            throw new Exception("Faded calibration overlay left visible pixels.");
        var testGrid = Render(0, 1, grid: true);
        if (testGrid[At(320, 180, 3)] < 20 || testGrid[At(20, 180, 3)] != 0)
            throw new Exception("Calibration grid failed to render without video, or escaped the keystone bounds.");
        var flash = Render(0, 1, 1, 0.5f);
        if (flash[At(96, 54, 3)] < 100 || flash[At(543, 305, 3)] != 0)
            throw new Exception("Save flash was missing or not local.");
        var multiple = Render(0, 1, 9, 0.5f);
        if (multiple[At(96, 54, 3)] < 100 || multiple[At(543, 305, 3)] < 100 || multiple[At(543, 54, 3)] != 0 || multiple[At(96, 305, 3)] != 0)
            throw new Exception("Remote save flash did not cover only the changed corners with the editing overlay hidden.");
        var later = Render(1, 2);
        if (image[At(104, 54, 3)] == later[At(104, 54, 3)] || image[At(320, 54, 3)] != later[At(320, 54, 3)])
            throw new Exception("Orbs did not pulse independently of the lines.");
        // Composite the production shader's premultiplied result over a dark preview backdrop.
        for (int p = 0; p < image.Length; p += 4)
        {
            var alpha = image[p + 3] / 255.0;
            for (int channel = 0; channel < 3; channel++)
                image[p + channel] = (byte)Math.Clamp(image[p + channel] + 22 * (1 - alpha), 0, 255);
            image[p + 3] = 255;
        }
        using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(image, y * w * 4, bits.Scan0 + y * bits.Stride, w * 4);
        }
        finally { bitmap.UnlockBits(bits); }
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.tools/calibration-preview.png"));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("GPU calibration readback passed azure/red orbs, perimeter lines, independent pulse, fade-to-zero, and local save flash.");
    }
}
