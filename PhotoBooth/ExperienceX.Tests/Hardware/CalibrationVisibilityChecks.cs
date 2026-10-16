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
        Console.WriteLine("Production GPU readback passed selected video/mat grids, red labels and points, translucent blue unselected points, white selection flashes, green save flashes, polygon clipping and configuration exit.");
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
            void CornersVisible(byte[] pixels, bool selected = false)
            {
                foreach (var (x, y) in new[] { (3, 3), (636, 3), (3, 356), (636, 356) })
                {
                    int offset = (y * 640 + x) * 4;
                    if (pixels[offset + 3] < 70 || (selected ? pixels[offset + 2] <= pixels[offset] : pixels[offset] <= pixels[offset + 2]))
                        throw new Exception("A corner orb was hidden or did not match its screen's selection color.");
                    if (!selected && pixels[offset + 3] > 160)
                        throw new Exception("An unselected blue corner orb was not semi-transparent.");
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
            // Render-worker visibility updates arrive more than once during selection/dragging.
            // They must not cancel the selection flash or restart it continuously.
            overlay.Show(-1);
            Thread.Sleep(110);
            var selectionFlash = Render();
            void CheckFlash(byte[] pixels, bool saved)
            {
                foreach (var (x, y) in new[] { (18, 18), (621, 18), (18, 341), (621, 341) })
                {
                    int at=(y*640+x)*4;
                    int blue=pixels[at], green=pixels[at+1], red=pixels[at+2];
                    if(pixels[at+3]<45 || (saved ? green<red+15 || green<blue+15 || green>130 : Math.Abs(red-green)>30 || Math.Abs(red-blue)>30))
                        throw new Exception(saved ? "Save did not flash all points dark green." : "Selection did not flash all points white.");
                }
            }
            CheckFlash(selectionFlash, saved:false);
            overlay.SelectScreen(false);
            CornersVisible(Render());
            overlay.SelectScreen(true);
            Thread.Sleep(300);
            var filled = Render();
            CornersVisible(filled, selected:true);
            if (filled[centerOffset + 3] < 30 || filled[centerOffset + 3] > 60 || filled[centerOffset] <= filled[centerOffset + 2])
                throw new Exception("Selected screen did not have a mostly transparent blue fill.");
            var hintOffset=(330*640+320)*4;
            if(filled[hintOffset+3]<150 || unselected[hintOffset+3]>15)
                throw new Exception("Selected-video framing instructions were missing or visible without selection.");
            overlay.SuppressHelp(true);
            var submenu = Render();
            if(submenu[hintOffset+3]>60)
                throw new Exception("Normal framing instructions remained behind the submenu.");
            CornersVisible(submenu, selected:true);
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
            if(!Enumerable.Range(140,95).SelectMany(row=>Enumerable.Range(180,280).Select(x=>(row*640+x)*4))
                .Any(at=>caption[at+2]>100 && caption[at+2]>caption[at]*3))
                throw new Exception("Selected screen name/media caption was not red.");
            overlay.FlashPoint(0,true);Thread.Sleep(110);
            CheckFlash(Render(),saved:true);
            overlay.SelectScreen(true);overlay.Show(-1);Thread.Sleep(190);
            CornersVisible(Render(),selected:true);
            overlay.Flash(1,true);Thread.Sleep(110);
            CheckFlash(Render(),saved:true);
            Thread.Sleep(190);
            overlay.SetMediaCaption(null);Thread.Sleep(110);Render();
            var videoOptions=options;
            options=options with{DisplayScreens=new(){["TEST"]=[new(){Type="mat"}]}};
            var matHint=Render();
            if(matHint[hintOffset+3]<150)throw new Exception("Selected mat did not show its action HUD.");
            byte[] HintArea(byte[] pixels)=>Enumerable.Range(240,96).SelectMany(row=>pixels.Skip((row*640+100)*4).Take(440*4)).ToArray();
            if(HintArea(matHint).SequenceEqual(HintArea(filled)))throw new Exception("Switching to a mat reused the video-only HUD.");
            SavePreview(matHint,"selected-mat-hint.png");
            overlay.SuppressHelp(true);
            options=options with{CalibrationGridEnabled=true};
            var matGrid=Render();
            if(Enumerable.Range(50,90).Sum(row=>Enumerable.Range(60,520).Count(x=>
                matGrid[(row*640+x)*4+3]>matHint[(row*640+x)*4+3]+25))<300)
                throw new Exception("Selected mat did not show its calibration grid.");
            SavePreview(matGrid,"selected-mat-grid.png");
            var concave=new PointOptions{Enabled=true,Vertices=[new(.1,.1),new(.5,.45),new(.9,.1),new(.9,.9),new(.1,.9)]};
            options=options with{DisplayScreens=new(){["TEST"]=[new(){Type="mat",Points=concave}]}};
            var concaveGrid=Render();
            if(concaveGrid[(55*640+320)*4+3]>2 || concaveGrid[(110*640+166)*4+3]<55)
                throw new Exception("Mat grid escaped a concave outline or failed to fill its interior.");
            var triangle=concave with{Vertices=[new(.1,.1),new(.9,.1),new(.5,.9)]};
            options=options with{DisplayScreens=new(){["TEST"]=[new(){Type="mat",Points=triangle}]}};
            var triangleGrid=Render();
            if(triangleGrid[(110*640+60)*4+3]>2 || triangleGrid[(110*640+268)*4+3]<55)
                throw new Exception("Triangular mat grid did not stay within its outline.");
            options=videoOptions with{DisplayScreens=new(){["TEST"]=[new(){Type="mat"}]}};
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
