using System.Runtime.InteropServices;
using ExperienceX;
using PhotoBooth.Networking;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Forms = System.Windows.Forms;

internal static class ZoomRotationGpuChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Check(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new Forms.Form { ShowInTaskbar = false };
            using var graphics = new GraphicsResources(window.Handle, 16, 16, "ZOOM-ROTATION-TEST");
            graphics.Initialize();
            // A coordinate ramp lets readback identify which source detail is visible:
            // red = source X, green = source Y. Production uses linear filtering.
            var ramp = new byte[256 * 256 * 4];
            for (int y=0;y<256;y++) for (int x=0;x<256;x++)
            {
                int p=(y*256+x)*4;
                ramp[p]=64; ramp[p+1]=(byte)y; ramp[p+2]=(byte)x; ramp[p+3]=255;
            }
            using var texture = graphics.Device.CreateTexture2D<byte>(ramp, Format.B8G8R8A8_UNorm, 256, 256, 1, 1, BindFlags.ShaderResource);
            using var view = graphics.Device.CreateShaderResourceView(texture);
            using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 16, 16, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            byte[] Render(VideoFramingOptions framing, double aspect=1, PerspectiveRectangle? perspective=null)
            {
                graphics.BeginFrame(new(0,0,0,0));
                // Exercise the actual production constant-buffer setup and shader together.
                graphics.DrawLayer(view, 1, new(), VideoTransform.Inverse(new()), new() { Enabled=false },
                    perspective: perspective, framing: framing, screenAspect: aspect);
                using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging, buffer);
                var map = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var pixels = new byte[16*16*4];
                    for(int y=0;y<16;y++) Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,pixels,y*64,64);
                    return pixels;
                }
                finally { graphics.Context.Unmap(staging,0); }
            }
            void Detail(byte[] image, int x, int y, double red, double green)
            {
                int p=(y*16+x)*4;
                if(Math.Abs(image[p+2]-red)>1 || Math.Abs(image[p+1]-green)>1 || image[p+3]!=255)
                    throw new Exception($"Combined zoom/rotation sampled the wrong source at {x},{y}: RGB/A {image[p+2]},{image[p+1]},{image[p]}/{image[p+3]}; expected R/G {red},{green}.");
            }
            var framing = new VideoFramingOptions { Zoom=2, CenterX=.625, CenterY=.375, RotationDegrees=90 };
            var rotated = Render(framing);
            Detail(rotated,3,3,123.5,131.5); Detail(rotated,12,3,123.5,59.5);
            Detail(rotated,12,12,195.5,59.5); Detail(rotated,3,12,195.5,131.5);
            var diagonal = Render(new() { Zoom=2, RotationDegrees=45 });
            Detail(diagonal,0,0,42.647,127.5); Detail(diagonal,15,0,127.5,42.647);
            Detail(diagonal,15,15,212.353,127.5); Detail(diagonal,0,15,127.5,212.353);
            var wide = Render(new() { Zoom=2, RotationDegrees=90 }, aspect:2);
            Detail(wide,3,3,109.5,199.5); Detail(wide,12,3,109.5,55.5);
            Detail(wide,12,12,145.5,55.5); Detail(wide,3,12,145.5,199.5);
            var cropped = Render(framing, perspective:new() { X=25,Width=50 });
            Detail(cropped,3,3,125.5,131.5); Detail(cropped,12,3,125.5,59.5);
            Detail(cropped,12,12,161.5,59.5); Detail(cropped,3,12,161.5,131.5);
            var unzoomed = Render(new() { RotationDegrees=45 });
            if(unzoomed[3]!=0 || diagonal[3]!=255)
                throw new Exception("Zoom did not fill the exposed corner of rotated media.");
            Console.WriteLine("PASS production GPU readback: combined zoom, pan and rotation at 45/90 degrees; rectangular proportions; perspective crop; zoom fills exposed corners.");
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }
}
