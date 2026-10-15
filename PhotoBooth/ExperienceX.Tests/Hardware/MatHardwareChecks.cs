using System.Runtime.InteropServices;
using ExperienceX;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Forms = System.Windows.Forms;

internal static class MatHardwareChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Check(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("GPU mat readback passed transparent media cutouts, background preservation, normal/negative geometry, z-order, G-Force boundary protection, resize, and editing/flash of point 38.");
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new Forms.Form { ShowInTaskbar = false };
            using var graphics = new GraphicsResources(window.Handle, 640, 360, "TEST");
            graphics.Initialize();
            using var mat = new MaskLayer(graphics);
            using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            byte[] Read()
            {
                using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging, buffer);
                var mapped = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var result = new byte[640 * 360 * 4];
                    for (int y = 0; y < 360; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, result, y * 640 * 4, 640 * 4);
                    return result;
                }
                finally { graphics.Context.Unmap(staging, 0); }
            }
            int At(int x, int y, int channel) => (y * 640 + x) * 4 + channel;
            using var greenTexture = graphics.Device.CreateTexture2D<byte>([0,255,0,255], Format.B8G8R8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource);
            using var greenView = graphics.Device.CreateShaderResourceView(greenTexture);
            void Fill(double opacity = 1) => graphics.DrawLayer(greenView, opacity, new(), VideoTransform.Inverse(new()), sourceAlpha: true);
            var shape = new PointOptions { Enabled = true, Vertices = [new(.1, .1), new(.9, .1), new(.9, .9), new(.5, .5), new(.1, .9)] };
            graphics.BeginFrame(new(1, 0, 0, 1));
            mat.Draw(shape);
            var pixels = Read();
            if (pixels[At(320, 108, 3)] != 255 || pixels[At(320, 108, 2)] != 0 || pixels[At(320, 288, 2)] != 255)
                throw new Exception("Mat did not cover content in opaque black or filled its concave cutout.");
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(shape);
            pixels = Read();
            if (pixels[At(320, 108, 3)] != 255 || pixels[At(320, 288, 3)] != 0 || pixels[At(20, 20, 3)] != 0)
                throw new Exception("Idle mat lost black opacity on a transparent display or erased pixels outside its polygon.");
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(shape, inverse: true); pixels = Read();
            if (pixels[At(320, 108, 3)] != 0 || pixels[At(320, 288, 3)] != 255 || pixels[At(20, 20, 3)] != 255 || pixels[At(20, 20, 2)] != 0)
                throw new Exception("Inverse mat failed to leave its concave polygon transparent and black everything outside.");
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(new(), inverse: true); pixels = Read();
            if (pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0)) throw new Exception("Initial full-display inverse mat blocked content.");
            var many = new PointOptions { Enabled = true, Vertices = Enumerable.Range(0, 40).Select(i => new ScreenPoint(.5 + .4 * Math.Cos(i * Math.PI / 20), .5 + .4 * Math.Sin(i * Math.PI / 20))).ToArray() };
            var options = new ExperienceOptions { ConfigurationMode = true, CalibrationGridEnabled = false, DisplayScreens = new() { ["TEST"] = [new() { Id = "mask", Type = "mat", Points = many }] } };
            var negative = options.ScreensFor("TEST")[0] with { Points = shape, IsNegative = true };
            using (var negativeTrack = new DisplayScreenTrack(graphics, "TEST", negative, options, () => { }, () => { }))
            {
                graphics.BeginFrame(new(0, 0, 0, 0)); graphics.BeginScreenLayers(); Fill(); negativeTrack.DrawMask(); graphics.FinishScreenLayers(); pixels = Read();
                if (pixels[At(320, 108, 1)] != 255 || pixels[At(320, 108, 3)] != 255 || pixels[At(320, 288, 3)] != 0 || pixels[At(20, 20, 3)] != 0)
                    throw new Exception("Negative mat did not retain media inside its polygon and reveal the background outside.");
            }
            using var track = new DisplayScreenTrack(graphics, "TEST", options.ScreensFor("TEST")[0], options, () => { }, () => { });
            try { track.Open("no-file-needed", new()); throw new Exception("A mat accepted a playback request."); } catch (ArgumentException) { }
            var imagePath = Path.Combine(Path.GetTempPath(), "ExperienceX-mat-layer-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var image = new System.Drawing.Bitmap(2, 2))
                {
                    using var paint = System.Drawing.Graphics.FromImage(image); paint.Clear(System.Drawing.Color.Lime);
                    image.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                using var media = new DisplayScreenTrack(graphics, "TEST", new() { Id = "media", Name = "green" }, options with { TransitionInSeconds = .001 }, () => { }, () => { });
                media.ApplyMediaBinding(new(imagePath, IsImage: true), true);
                Thread.Sleep(20); media.Tick();
                graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(shape, inverse: true); media.DrawVideo(); pixels = Read();
                if (pixels[At(20, 20, 1)] < 250) throw new Exception("The bottom inverse mat obscured media above it.");
                graphics.BeginFrame(new(0, 0, 0, 0)); graphics.BeginScreenLayers(); track.DrawVideo(); track.DrawMask(); media.DrawVideo(); media.DrawMask(); graphics.FinishScreenLayers();
                pixels = Read();
                if (pixels[At(320, 180, 1)] < 250 || pixels[At(320, 180, 3)] != 255) throw new Exception("Media above a mat did not cover it.");
                graphics.BeginFrame(new(0, 0, 0, 0)); graphics.BeginScreenLayers(); media.DrawVideo(); media.DrawMask(); track.DrawVideo(); track.DrawMask(); graphics.FinishScreenLayers();
                pixels = Read();
                if (pixels[At(320, 180, 0)] != 0 || pixels[At(320, 180, 1)] != 0 || pixels[At(320, 180, 2)] != 0 || pixels[At(320, 180, 3)] != 0) throw new Exception("A mat above media did not clear both RGB and alpha.");
            }
            finally { File.Delete(imagePath); }
            // Erasing ordered media must preserve the base, including the separate visualization boundary mask.
            foreach (var background in new[] { new Vortice.Mathematics.Color4(0,0,0,0), new Vortice.Mathematics.Color4(0,0,1,1), new Vortice.Mathematics.Color4(1,1,1,1) })
            {
                graphics.BeginFrame(background); graphics.BeginScreenLayers(); Fill(.5); mat.Draw(shape, cutout: true); graphics.FinishScreenLayers(); pixels = Read();
                if (pixels[At(320,108,0)] != (byte)(background.B*255) || pixels[At(320,108,1)] != (byte)(background.G*255) ||
                    pixels[At(320,108,2)] != (byte)(background.R*255) || pixels[At(320,108,3)] != (byte)(background.A*255))
                    throw new Exception("A cutout erased or altered the display background.");
                if (Math.Abs(pixels[At(320,288,3)]-(128+background.A*127))>1 || Math.Abs(pixels[At(320,288,1)]-(128+background.G*127))>1)
                    throw new Exception("Compositing masked media reapplied chroma key, brightness or opacity.");
                graphics.BeginFrame(background); graphics.BeginScreenLayers(); track.DrawMask(); graphics.FinishScreenLayers(); pixels=Read();
                if (pixels[At(320,180,3)]!=(byte)(background.A*255) || pixels[At(320,180,0)]!=(byte)(background.B*255))
                    throw new Exception("An idle mat covered the background despite no media playing.");
            }
            graphics.BeginFrame(new(0,0,0,0)); mat.Draw(shape,inverse:true); graphics.BeginScreenLayers(); Fill(); mat.Draw(new(),cutout:true); graphics.FinishScreenLayers(); pixels=Read();
            if (pixels[At(320,108,3)]!=0 || pixels[At(20,20,3)]!=255 || pixels[At(20,20,1)]!=0)
                throw new Exception("A regular cutout erased the G-Force boundary mask or failed to reveal the visualization opening.");
            graphics.BeginFrame(new(0,0,0,0)); graphics.BeginScreenLayers(); Fill(); mat.Draw(shape,cutout:true); Fill(.5); graphics.FinishScreenLayers(); pixels=Read();
            if (Math.Abs(pixels[At(320,108,3)]-128)>1 || pixels[At(320,288,3)]!=255)
                throw new Exception("Cutout ordering lost translucent media above it or retained media below it.");
            graphics.Resize(320,180); graphics.BeginFrame(new(0,0,0,0)); graphics.BeginScreenLayers(); Fill(); graphics.FinishScreenLayers();
            graphics.Resize(640,360); graphics.BeginFrame(new(0,0,0,0)); graphics.BeginScreenLayers(); Fill(); mat.Draw(shape,cutout:true); graphics.FinishScreenLayers(); pixels=Read();
            if(pixels[At(320,108,3)]!=0 || pixels[At(20,20,1)]!=255) throw new Exception("Resize failed to recreate the media compositing target.");
            using var overlay = new CalibrationOverlay(); overlay.Initialize(graphics); overlay.Configure(options);
            overlay.SelectScreen(true); overlay.Show(37);
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(many); overlay.Draw(graphics, options, "TEST", new(), "mask");
            pixels = Read();
            var p = many.Vertices[37]; var x = (int)(p.X * 639); var y = (int)(p.Y * 359);
            if (pixels[At(x, y, 0)] <= pixels[At(x, y, 2)]) throw new Exception($"Selected point 38 was not drawn in azure at {x},{y}: BGRA={pixels[At(x,y,0)]},{pixels[At(x,y,1)]},{pixels[At(x,y,2)]},{pixels[At(x,y,3)]}.");
            var other = many.Vertices[15];
            if (pixels[At((int)(other.X * 639), (int)(other.Y * 359), 2)] <= pixels[At((int)(other.X * 639), (int)(other.Y * 359), 0)])
                throw new Exception("Unselected added points were not visible in red.");
            var maskOptions = options with { DisplayScreens = new() { ["TEST"] = [options.ScreensFor("TEST")[0] with { Type = "visualization" }] } };
            overlay.Configure(maskOptions); overlay.SelectScreen(false); graphics.BeginFrame(new(0, 0, 0, 0)); overlay.Draw(graphics, maskOptions, "TEST", new(), "mask"); pixels = Read();
            if (pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0)) throw new Exception("Unselected G-Force mask exposed editing handles.");
            overlay.SelectScreen(true); graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(many, inverse: true); overlay.Draw(graphics, maskOptions, "TEST", new(), "mask"); pixels = Read();
            if (pixels[At(x, y, 0)] <= pixels[At(x, y, 2)]) throw new Exception("Selected G-Force mask did not expose editable polygon points.");
            overlay.Configure(options);
            options = options with { ConfigurationMode = false }; overlay.Configure(options); overlay.FlashPoint(37, true);
            Thread.Sleep(100);
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(many); overlay.Draw(graphics, options, "TEST", new(), "mask"); pixels = Read();
            if (pixels[At(x, y, 0)] < 100 || pixels[At(x, y, 2)] < 100)
                throw new Exception("Save confirmation for point 38 was truncated by a 32-bit corner mask.");
        }
        finally { MediaFactory.MFShutdown(); }
    }
}
