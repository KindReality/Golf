using System.Runtime.InteropServices;
using ExperienceX;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

internal static class ScreenChromaKeyHardwareChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Check(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Passed production GPU draw path: two screens with independent chroma key, live toggles, transparent backgrounds and solid backgrounds.");
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new System.Windows.Forms.Form();
            using var graphics = new GraphicsResources(window.Handle, 64, 64, "TEST");
            graphics.Initialize();
            using var texture = graphics.Device.CreateTexture2D<byte>(new byte[] { 0, 0, 0, 255 }, Format.B8G8R8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource);
            using var view = graphics.Device.CreateShaderResourceView(texture);
            using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            var left = new ScreenOptions { BlackKey = new() { Enabled = true }, Points = new() { Enabled = true, TopLeft = new(0, 0), TopRight = new(.5, 0), BottomLeft = new(0, 1), BottomRight = new(.5, 1) } };
            var right = new ScreenOptions { Id = "right", Name = "screen2", BlackKey = new() { Enabled = false }, Points = new() { Enabled = true, TopLeft = new(.5, 0), TopRight = new(1, 0), BottomLeft = new(.5, 1), BottomRight = new(1, 1) } };
            var options = new ExperienceOptions { DisplayBackgrounds = new() { ["TEST"] = new() { IsTransparent = true } } };
            byte[] Render()
            {
                graphics.BeginFrame(DisplayBackground.For("TEST", options));
                foreach (var screen in new[] { left, right })
                    graphics.DrawLayer(view, 1, options, VideoTransform.Inverse(screen.Points), screen.EffectiveBlackKey(options));
                using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging, buffer);
                var map = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var result = new byte[64 * 64 * 4];
                    for (int y = 0; y < 64; y++) Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, result, y * 256, 256);
                    return result;
                }
                finally { graphics.Context.Unmap(staging, 0); }
            }
            int At(int x, int channel) => (32 * 64 + x) * 4 + channel;
            var transparent = Render();
            if (transparent[At(16, 3)] != 0 || transparent[At(48, 3)] != 255)
                throw new Exception("Screen chroma key did not independently remove black pixels.");
            options = options with { DisplayBackgrounds = new() { ["TEST"] = new() { BackgroundColor = "#204060" } } };
            var solid = Render();
            if (solid[At(16, 0)] != 96 || solid[At(16, 1)] != 64 || solid[At(16, 2)] != 32 || solid[At(16, 3)] != 255 || solid[At(48, 0)] != 0 || solid[At(48, 3)] != 255)
                throw new Exception("Keyed pixels did not reveal the solid window background while unkeyed pixels remained black.");
            left = left with { BlackKey = new() { Enabled = false } };
            right = right with { BlackKey = new() { Enabled = true } };
            var toggled = Render();
            if (toggled[At(16, 0)] != 0 || toggled[At(48, 0)] != 96)
                throw new Exception("Changing screen chroma key did not apply to the next frame.");
        }
        finally { MediaFactory.MFShutdown(); }
    }
}
