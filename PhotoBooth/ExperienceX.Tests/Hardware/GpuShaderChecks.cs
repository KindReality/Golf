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
        public Vector4 Row0, Row1, Row2, Effects, Aspect, Crop, Rotation, SourceBounds;
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
            byte[] pixels = [0, 0, 0, 255, 0, 0, 255, 128, 255, 0, 0, 255, 255, 255, 255, 255];
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
            byte[] Render(float opacity, bool key, PointOptions? keystone = null, Color4? background = null, Vector4? contentScale = null, float brightness = 1, Vector4? crop = null, Vector4? rotation = null, Vector4? sourceBounds = null)
            {
                context.ClearRenderTargetView(rtv, background ?? new Color4(0, 0, 0, 0));
                context.OMSetBlendState(videoBlend);
                var (r0, r1, r2) = VideoTransform.Inverse(keystone ?? new());
                var data = new Constants { Crop = crop ?? new(0,0,1,1), Row0 = r0, Row1 = r1, Row2 = r2, Effects = new(opacity, 0.015f, 0.03f, key ? 1 : 0), Aspect = contentScale ?? new(1, 1, 0, 0) };
                data.Rotation = rotation ?? new(1, 0, 0, 1);
                data.SourceBounds = sourceBounds ?? new(0, 0, 1, 1);
                data.Aspect.W = brightness;
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
            var cropped = Render(1, false, crop: new(0,.5f,.5f,.5f));
            for(int p=0;p<cropped.Length;p+=4)
                if(cropped[p]<250 || cropped[p+1]>2 || cropped[p+2]>2 || cropped[p+3]<250)
                    throw new Exception("Perspective crop sampled outside the selected bottom-left source quadrant.");
            Console.WriteLine("PASS GPU perspective crop fills output with only the selected source quadrant.");
            var framedCrop = new VideoFramingOptions { Zoom = 2, CenterY = .75 }.Crop(new() { X = 50, Width = 50 });
            var framed = Render(1, false, crop: new((float)(framedCrop.X/100),(float)(framedCrop.Y/100),(float)(framedCrop.Width/100),(float)(framedCrop.Height/100)));
            if (framed.Any(channel => channel < 250)) throw new Exception("Zoom/pan escaped its perspective instead of sampling only the bottom-right source quadrant.");
            Console.WriteLine("PASS GPU zoom/pan composes with the selected perspective and fills the screen.");
            int Alpha(byte[] b, int x, int y) => b[(y * 16 + x) * 4 + 3];
            var quarterTurn = new VideoFramingOptions { RotationDegrees = 90 };
            var rotated = Render(1, false, rotation: quarterTurn.SamplingRotation());
            int At(int x, int y, int channel) => (y * 16 + x) * 4 + channel;
            if (rotated[At(3,3,0)] != 255 || rotated[At(3,3,2)] != 0 ||
                rotated[At(12,3,0)] != 0 || rotated[At(12,3,2)] != 0 ||
                rotated[At(12,12,2)] != 255 || rotated[At(12,12,0)] != 0 ||
                rotated[At(3,12,0)] != 255 || rotated[At(3,12,2)] != 255)
                throw new Exception("90-degree clockwise rotation did not put the source quadrants in their expected positions.");
            var angled = Render(1, false, rotation: new VideoFramingOptions { RotationDegrees = 45 }.SamplingRotation());
            if (Alpha(angled,0,0) != 0 || Alpha(angled,8,8) != 255)
                throw new Exception("Rotated media stretched its edge pixels instead of leaving uncovered areas transparent.");
            var wideRotation = Render(1, false, rotation: quarterTurn.SamplingRotation(2));
            if (Alpha(wideRotation,3,8) != 0 || Alpha(wideRotation,12,8) != 0 || Alpha(wideRotation,8,3) != 255 || Alpha(wideRotation,8,12) != 255)
                throw new Exception("Rotation distorted a rectangular screen instead of preserving its pixel proportions.");
            var rotatedCrop = Render(1, false, crop: new(0,.5f,.5f,.5f), rotation: new VideoFramingOptions { RotationDegrees = 45 }.SamplingRotation(), sourceBounds: new(0,.5f,.5f,1));
            for (int p=0;p<rotatedCrop.Length;p+=4)
                if (rotatedCrop[p+3] != 0 && (rotatedCrop[p] != 255 || rotatedCrop[p+1] != 0 || rotatedCrop[p+2] != 0))
                    throw new Exception("Rotation sampled media outside its selected perspective.");
            var keyedRotation = Render(.25f, true, rotation: quarterTurn.SamplingRotation(), brightness: .5f);
            if (Alpha(keyedRotation,12,3) != 0 || Math.Abs(Alpha(keyedRotation,3,3)-64)>1 || Math.Abs(keyedRotation[At(3,3,0)]-32)>1)
                throw new Exception("Rotation changed chroma key, fade alpha or brightness.");
            var fixedPoints = Render(1, false, new PointOptions { Enabled=true, TopLeft=new(.25,.25), TopRight=new(.75,.25), BottomLeft=new(.25,.75), BottomRight=new(.75,.75) }, rotation: quarterTurn.SamplingRotation());
            if (Alpha(fixedPoints,1,1)!=0 || Alpha(fixedPoints,8,8)!=255)
                throw new Exception("Media rotation changed screen clipping.");
            Console.WriteLine("PASS GPU media rotation: clockwise quadrants, rectangular pixel proportions, fixed screen clipping, transparent corners, perspective bounds, brightness, key and fade.");
            byte[] imagePixels = [0, 0, 128, 128, 0, 0, 0, 0, 0, 0, 128, 128, 0, 0, 0, 0];
            using var imageSource = device.CreateTexture2D<byte>(imagePixels, Format.B8G8R8A8_UNorm, 2, 2, 1, 1, BindFlags.ShaderResource);
            using var imageView = device.CreateShaderResourceView(imageSource);
            context.PSSetShaderResource(0, imageView);
            var imageAlpha = Render(1, false, contentScale: new(1, 1, 1, 0));
            if (Math.Abs(Alpha(imageAlpha, 3, 3) - 128) > 1 || Math.Abs(imageAlpha[(3 * 16 + 3) * 4 + 2] - 128) > 1 ||
                Math.Abs(Alpha(imageAlpha, 8, 3) - imageAlpha[(3 * 16 + 8) * 4 + 2]) > 1)
                throw new Exception("Image source alpha was lost or the image texture was not premultiplied correctly.");
            context.PSSetShaderResource(0, srv);
            if (Alpha(Render(1, false), 12, 3) != 255)
                throw new Exception("Image alpha support changed opaque video rendering.");
            Console.WriteLine("GPU readback passed image alpha and premultiplied color while preserving video alpha behavior.");
            var dimVideo = Render(1, true, brightness: .5f);
            var zeroVideo = Render(1, true, brightness: 0);
            if (Math.Abs(dimVideo[(3 * 16 + 12) * 4 + 2] - 128) > 1 || Alpha(dimVideo, 12, 3) != 255 ||
                Alpha(dimVideo, 3, 3) != 0 || zeroVideo[(3 * 16 + 12) * 4 + 2] != 0 || Alpha(zeroVideo, 12, 3) != 255)
                throw new Exception("Brightness did not dim RGB while preserving video alpha and the original chroma key.");
            context.PSSetShaderResource(0, imageView);
            var dimImage = Render(.5f, false, contentScale: new(1, 1, 1, 0), brightness: .5f);
            if (Math.Abs(dimImage[(3 * 16 + 3) * 4 + 2] - 32) > 1 || Math.Abs(Alpha(dimImage, 3, 3) - 64) > 1 || Alpha(dimImage, 12, 3) != 0)
                throw new Exception("Image brightness changed source alpha or broke premultiplied fading.");
            context.PSSetShaderResource(0, srv);
            var dimBackdrop = DisplayBackground.Parse("#204060");
            var dimOverBackground = Render(.25f, true, background: dimBackdrop, brightness: .5f);
            int dimRed = (3 * 16 + 12) * 4;
            if (Math.Abs(dimOverBackground[dimRed + 2] - 56) > 1 || dimOverBackground[dimRed + 3] != 255 || dimOverBackground[0] != 96)
                throw new Exception("Screen brightness dimmed the display background or changed fade compositing.");
            Console.WriteLine("GPU readback passed video/image brightness, zero brightness, unchanged chroma key/alpha/fades and independent display background.");
            var stretch = Render(1, false, contentScale: new(1,1,0,0));
            var narrowScreen = new PointOptions { Enabled = true, TopLeft = new(.25, 0), TopRight = new(.75, 0), BottomLeft = new(.25, 1), BottomRight = new(.75, 1) };
            var narrowStretch = Render(1, false, narrowScreen, contentScale: new(1,1,0,0));
            if (Alpha(stretch,8,1)!=255 || Alpha(narrowStretch,8,4)!=255) throw new Exception("Stretch did not fill the configured screen.");
            Console.WriteLine("GPU readback passed stretch-only coverage within screen points.");
            var faded = Render(0.25f, true);
            if (Alpha(faded, 3, 3) != 0 || Math.Abs(Alpha(faded, 12, 3) - 64) > 1 || Math.Abs(faded[(3 * 16 + 12) * 4 + 2] - 64) > 1)
                throw new Exception("GPU black key or premultiplied fade produced incorrect pixels.");
            if (Render(0, true).Any(b => b != 0))
                throw new Exception("GPU zero-opacity surface was not completely transparent.");
            if (Alpha(Render(1, false), 3, 3) != 255)
                throw new Exception("Disabling black key did not preserve opaque black.");
            var warped = Render(1, false, new PointOptions { Enabled = true, TopLeft = new(0.25, 0.25), TopRight = new(0.75, 0.25), BottomLeft = new(0.25, 0.75), BottomRight = new(0.75, 0.75) });
            if (Alpha(warped, 1, 1) != 0 || Alpha(warped, 8, 8) != 255)
                throw new Exception("GPU keystone did not clip outside its quadrilateral.");
            var backdrop = DisplayBackground.Parse("#204060");
            var idle = Render(0, true, background: backdrop);
            for (int p = 0; p < idle.Length; p += 4)
                if (idle[p] != 96 || idle[p + 1] != 64 || idle[p + 2] != 32 || idle[p + 3] != 255)
                    throw new Exception("Zero-opacity video did not preserve the opaque display background.");
            var overBackground = Render(0.25f, true, background: backdrop);
            int black = (3 * 16 + 3) * 4, red = (3 * 16 + 12) * 4;
            if (overBackground[black] != 96 || overBackground[black + 1] != 64 || overBackground[black + 2] != 32 || overBackground[black + 3] != 255 ||
                Math.Abs(overBackground[red] - 72) > 1 || Math.Abs(overBackground[red + 1] - 48) > 1 || Math.Abs(overBackground[red + 2] - 88) > 1 || overBackground[red + 3] != 255)
                throw new Exception("Premultiplied video fade or black-key transparency did not composite over the background correctly.");
            var outsideKeystone = Render(1, false, new PointOptions { Enabled = true, TopLeft = new(.25, .25), TopRight = new(.75, .25), BottomLeft = new(.25, .75), BottomRight = new(.75, .75) }, backdrop);
            if (outsideKeystone[0] != 96 || outsideKeystone[1] != 64 || outsideKeystone[2] != 32 || outsideKeystone[3] != 255)
                throw new Exception("Points clipping erased the full-display background.");
            Console.WriteLine("GPU readback passed black key, premultiplied fades, transparent clear, and keystone clipping.");
            Console.WriteLine("GPU readback passed solid display backgrounds, video fades, keyed pixels and background outside keystone bounds.");
            Overlay(device, context, vs, Shader);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayData
    {
        public Vector4 Screen, Selection, Grid, Row0, Row1, Row2, Label, Join, Hint;
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
        byte[] Render(float opacity, float time, int flash = 0, float progress = -1, bool grid = false, int join = -1)
        {
            Vector4[] points = [new(.15f, .15f, (flash & 1) != 0 ? 1 : 0, 0), new(.85f, .15f, (flash & 2) != 0 ? 1 : 0, 1),
                new(.85f, .85f, (flash & 8) != 0 ? 1 : 0, 3), new(.15f, .85f, (flash & 4) != 0 ? 1 : 0, 2)];
            using var vertices = device.CreateBuffer<Vector4>(points, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured, structureByteStride: 16);
            using var pointsView = device.CreateShaderResourceView(vertices);
            context.PSSetShaderResource(1, pointsView);
            var data = new OverlayData { Screen = new(w, h, time, opacity), Selection = new(0, flash, progress, 1), Label = new(0, 0, 0, 4), Join = new(join,0,0,0) };
            if (grid)
            {
                var inverse = VideoTransform.Inverse(new PointOptions { Enabled = true, TopLeft = new(.15, .15), TopRight = new(.85, .15), BottomLeft = new(.15, .85), BottomRight = new(.85, .85) });
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
            finally { context.Unmap(staging, 0); context.PSUnsetShaderResource(1); }
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
        var joinHint = Render(1, 1, join: 1);
        if (joinHint[At(546,20,3)] < 180 || image[At(546,20,3)] > 10)
            throw new Exception("J hint did not appear above the nearby target point.");
        var joinLater = Render(1, 1.4f, join: 1);
        if (joinHint[At(560,65,3)] == joinLater[At(560,65,3)] || joinHint[At(320,54,3)] != joinLater[At(320,54,3)])
            throw new Exception("Join target halo did not pulse independently of the perimeter.");
        if (Render(0,1,join:1).Any(v=>v!=0))throw new Exception("Join prompt remained visible outside editing mode.");
        image = joinHint;
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
        Console.WriteLine("GPU calibration readback passed azure/red orbs, J target label and pulse, perimeter lines, independent pulse, fade-to-zero, and local save flash.");
    }
}
