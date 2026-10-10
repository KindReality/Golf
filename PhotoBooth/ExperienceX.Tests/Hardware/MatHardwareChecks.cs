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
        Console.WriteLine("GPU mat readback passed opaque black over content, transparent cutouts, concave geometry, idle masks, video-command rejection, and editing/flash of point 38.");
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new Forms.Form { ShowInTaskbar = false };
            using var graphics = new GraphicsResources(window.Handle, 640, 360, "TEST");
            graphics.Initialize();
            using var mat = new MatLayer(graphics);
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
            var many = new PointOptions { Enabled = true, Vertices = Enumerable.Range(0, 40).Select(i => new ScreenPoint(.5 + .4 * Math.Cos(i * Math.PI / 20), .5 + .4 * Math.Sin(i * Math.PI / 20))).ToArray() };
            var options = new ExperienceOptions { ConfigurationMode = true, CalibrationGridEnabled = false, DisplayScreens = new() { ["TEST"] = [new() { Id = "mask", Type = "mat", Points = many }] } };
            using var track = new DisplayScreenTrack(graphics, "TEST", options.ScreensFor("TEST")[0], options, () => { }, () => { });
            try { track.Open("no-file-needed", new()); throw new Exception("A mat accepted a playback request."); } catch (ArgumentException) { }
            using var overlay = new CalibrationOverlay(); overlay.Initialize(graphics); overlay.Configure(options);
            overlay.SelectScreen(true); overlay.Show(37);
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(many); overlay.Draw(graphics, options, "TEST", new(), "mask");
            pixels = Read();
            var p = many.Vertices[37]; var x = (int)(p.X * 639); var y = (int)(p.Y * 359);
            if (pixels[At(x, y, 0)] <= pixels[At(x, y, 2)]) throw new Exception($"Selected point 38 was not drawn in azure at {x},{y}: BGRA={pixels[At(x,y,0)]},{pixels[At(x,y,1)]},{pixels[At(x,y,2)]},{pixels[At(x,y,3)]}.");
            var other = many.Vertices[15];
            if (pixels[At((int)(other.X * 639), (int)(other.Y * 359), 2)] <= pixels[At((int)(other.X * 639), (int)(other.Y * 359), 0)])
                throw new Exception("Unselected added points were not visible in red.");
            options = options with { ConfigurationMode = false }; overlay.Configure(options); overlay.FlashPoint(37, true);
            Thread.Sleep(100);
            graphics.BeginFrame(new(0, 0, 0, 0)); mat.Draw(many); overlay.Draw(graphics, options, "TEST", new(), "mask"); pixels = Read();
            if (pixels[At(x, y, 0)] < 100 || pixels[At(x, y, 2)] < 100)
                throw new Exception("Save confirmation for point 38 was truncated by a 32-bit corner mask.");
        }
        finally { MediaFactory.MFShutdown(); }
    }
}
