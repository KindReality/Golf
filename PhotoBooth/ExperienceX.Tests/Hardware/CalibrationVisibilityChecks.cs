using System.Runtime.InteropServices;
using ExperienceX;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Forms = System.Windows.Forms;

internal static class CalibrationVisibilityChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Check();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Production GPU readback passed visible corner orbs before clicking, after release/cancel, with grid disabled, and removal when configuration mode exits.");
    }

    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new Forms.Form { ShowInTaskbar = false };
            using var graphics = new GraphicsResources(window.Handle, 640, 360, "TEST");
            graphics.Initialize();
            using var overlay = new CalibrationOverlay();
            overlay.Initialize(graphics);
            using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            var options = new ExperienceOptions { ConfigurationMode = true, CalibrationGridEnabled = false };
            byte[] Render()
            {
                graphics.DrawVideo(0, options, default, 0, 0, new(0, 0, 0, 0));
                if (overlay.Visible(options))
                    overlay.Draw(graphics, options, "TEST", new KeystoneTransformCache());
                using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging, buffer);
                var mapped = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var pixels = new byte[640 * 360 * 4];
                    for (int y = 0; y < 360; y++)
                        Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, pixels, y * 640 * 4, 640 * 4);
                    return pixels;
                }
                finally { graphics.Context.Unmap(staging, 0); }
            }
            void CornersVisible(byte[] pixels)
            {
                foreach (var (x, y) in new[] { (1, 1), (638, 1), (1, 358), (638, 358) })
                {
                    int offset = (y * 640 + x) * 4;
                    if (pixels[offset + 3] < 100 || pixels[offset + 2] <= pixels[offset])
                        throw new Exception("An unselected corner orb was hidden or not red.");
                }
            }
            overlay.Configure(options);
            if (!overlay.Animating(options))
                throw new Exception("Idle configuration mode did not schedule throbbing corner animation.");
            CornersVisible(Render());
            overlay.Show(0);
            var selected = Render();
            if (selected[(1 * 640 + 1) * 4] <= selected[(1 * 640 + 1) * 4 + 2])
                throw new Exception("Selected orb was not azure.");
            overlay.Fade(); // Mouse release, cancel, and completed edits must leave all handles visible.
            CornersVisible(Render());
            Thread.Sleep(400);
            CornersVisible(Render());
            options = options with
            {
                ConfigurationMode = false
            };
            overlay.Configure(options);
            if (overlay.Animating(options) || Render().Any(value => value != 0))
                throw new Exception("Corner orbs remained visible after leaving configuration mode.");
        }
        finally { MediaFactory.MFShutdown(); }
    }
}
