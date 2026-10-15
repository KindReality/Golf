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
        Console.WriteLine("Production GPU readback passed faint unselected video fills, stronger selected fills, polygon clipping, mat behavior, persistent corner orbs and removal when configuration mode exits.");
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
                    overlay.Draw(graphics, options, "TEST", new PointTransformCache(), "screen1");
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
            var unselected = Render();
            CornersVisible(unselected);
            var centerOffset = (120 * 640 + 200) * 4;
            if (unselected[centerOffset + 3] < 8 || unselected[centerOffset + 3] > 15 || unselected[centerOffset] <= unselected[centerOffset + 2])
                throw new Exception("Unselected video screen did not have a faint blue fill while idle.");
            overlay.SelectScreen(true);
            var filled = Render();
            if (filled[centerOffset + 3] < 30 || filled[centerOffset + 3] > 60 || filled[centerOffset] <= filled[centerOffset + 2])
                throw new Exception("Selected screen did not have a mostly transparent blue fill.");
            var hintOffset=(330*640+320)*4;
            if(filled[hintOffset+3]<150 || unselected[hintOffset+3]>15)
                throw new Exception("Selected-video framing instructions were missing or visible without selection.");
            overlay.SuppressHelp(true);
            var submenu = Render();
            if(submenu[hintOffset+3]>60)
                throw new Exception("Normal framing instructions remained behind the submenu.");
            CornersVisible(submenu);
            overlay.SuppressHelp(false);
            if(Render()[hintOffset+3]<150)
                throw new Exception("Normal framing instructions did not return after closing the submenu.");
            void SavePreview(byte[] pixels, string name)
            {
                using var bitmap=new System.Drawing.Bitmap(640,360,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data=bitmap.LockBits(new(0,0,640,360),System.Drawing.Imaging.ImageLockMode.WriteOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try{for(int y=0;y<360;y++)Marshal.Copy(pixels,y*640*4,data.Scan0+y*data.Stride,640*4);}
                finally{bitmap.UnlockBits(data);}
                var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.tools/"+name));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);bitmap.Save(path);
            }
            SavePreview(filled,"selected-video-framing-hint.png");
            overlay.SetMediaCaption("Folder: Ghosts/Hauntings/");Thread.Sleep(110);
            var caption=Render();
            if(caption.Skip((210*640+210)*4).Take(220*4).SequenceEqual(filled.Skip((210*640+210)*4).Take(220*4)))
                throw new Exception("Media/folder caption did not appear beneath the selected screen name.");
            SavePreview(caption,"selected-media-folder-caption.png");
            overlay.SetMediaCaption(null);Thread.Sleep(110);Render();
            var videoOptions=options;
            options=options with{DisplayScreens=new(){["TEST"]=[new(){Type="mat"}]}};
            var matHint=Render();
            if(matHint[hintOffset+3]<150)throw new Exception("Selected mat did not show its action HUD.");
            byte[] HintArea(byte[] pixels)=>Enumerable.Range(240,96).SelectMany(row=>pixels.Skip((row*640+100)*4).Take(440*4)).ToArray();
            if(HintArea(matHint).SequenceEqual(HintArea(filled)))throw new Exception("Switching to a mat reused the video-only HUD.");
            SavePreview(matHint,"selected-mat-hint.png");
            overlay.SuppressHelp(true);
            if(Render()[hintOffset+3]>60)throw new Exception("Mat action HUD remained visible behind a submenu.");
            overlay.SuppressHelp(false);
            if(Render()[hintOffset+3]<150)throw new Exception("Mat action HUD did not return after the submenu.");
            options=videoOptions;
            if(!HintArea(Render()).SequenceEqual(HintArea(filled)))throw new Exception("Switching back to video did not restore its own HUD.");
            overlay.SelectScreen(false);
            if(Render()[hintOffset+3]>15)throw new Exception("Inline framing hint remained after deselection.");
            overlay.Show(0);
            var selected = Render();
            if (selected[(1 * 640 + 1) * 4] <= selected[(1 * 640 + 1) * 4 + 2])
                throw new Exception("Selected orb was not azure.");
            overlay.Fade(); // Mouse release, cancel, and completed edits must leave all handles visible.
            CornersVisible(Render());
            Thread.Sleep(400);
            CornersVisible(Render());
            options = options with { Points = new() { Enabled = true, TopLeft = new(.25, .25), TopRight = new(.75, .25), BottomLeft = new(.25, .75), BottomRight = new(.75, .75) } };
            var clipped = Render();
            if (clipped[3] != 0 || clipped[centerOffset + 3] < 8)
                throw new Exception("Unselected fill did not stay inside the video's points.");
            options = options with { DisplayScreens = new() { ["TEST"] = [new() { Type = "mat", Points = options.Points }] } };
            if (Render()[centerOffset + 3] != 0)
                throw new Exception("The unselected video tint was applied to a mat screen.");
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
