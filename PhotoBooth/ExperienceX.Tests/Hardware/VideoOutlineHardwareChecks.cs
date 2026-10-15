using System.Runtime.InteropServices;
using ExperienceX;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Forms = System.Windows.Forms;

internal static class VideoOutlineHardwareChecks
{
    internal static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try { Check(); done.SetResult(); } catch(Exception ex) { done.SetException(ex); }
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        var path=Path.Combine(Path.GetTempPath(),"ExperienceX-video-outline-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            using(var image=new System.Drawing.Bitmap(64,64))
            {
                for(int y=0;y<64;y++) for(int x=0;x<64;x++) image.SetPixel(x,y,System.Drawing.Color.FromArgb(64+x*3,64+y*3,100));
                image.Save(path,System.Drawing.Imaging.ImageFormat.Png);
            }
            using var window=new Forms.Form{ShowInTaskbar=false};
            using var graphics=new GraphicsResources(window.Handle,256,256,"OUTLINE-TEST"); graphics.Initialize();
            using var staging=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,256,256,1,1,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read));
            var options=new ExperienceOptions{TransitionInSeconds=.001,BlackKey=new(){Enabled=false}};
            var quad=new PointOptions{Enabled=true,TopLeft=new(.2,.2),TopRight=new(.8,.2),BottomRight=new(.8,.8),BottomLeft=new(.2,.8)};
            var screen=new ScreenOptions{Id="outline",Points=quad};
            using var track=new DisplayScreenTrack(graphics,"TEST",screen,options,()=>{},()=>{});
            track.ApplyMediaBinding(new(path,IsImage:true),true); Thread.Sleep(20); track.Tick();
            var request=track.RequestId;
            byte[] Render(PointOptions points,VideoFramingOptions? framing=null)
            {
                track.Configure(screen with{Points=points,Framing=framing??new()},options);
                graphics.BeginFrame(new(0,0,0,0)); track.DrawVideo();
                using var buffer=graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging,buffer);
                var map=graphics.Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var result=new byte[256*256*4];
                    for(int y=0;y<256;y++)Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,result,y*256*4,256*4);
                    return result;
                }
                finally {graphics.Context.Unmap(staging,0);}
            }
            int Alpha(byte[] pixels,int x,int y)=>pixels[(y*256+x)*4+3];
            var before=Render(quad);
            var outline=PointEditing.Insert(quad,0,new(.5,.2),video:true);
            var inserted=Render(outline);
            for(int y=60;y<195;y+=12)for(int x=60;x<195;x+=12)for(int c=0;c<4;c++)
                if(Math.Abs(before[(y*256+x)*4+c]-inserted[(y*256+x)*4+c])>1)
                    throw new Exception("Adding a collinear video outline point changed the picture or keystone mapping.");
            var outward=PointEditing.Move(outline,1,.5,.05);
            var expanded=Render(outward);
            if(Alpha(before,128,25)!=0||Alpha(expanded,128,25)!=255||Alpha(expanded,20,20)!=0)
                throw new Exception("Video did not expand to cover an outward outline point, or escaped its outline.");
            var wider=PointEditing.Insert(outward,2,new(.8,.5),video:true);
            wider=PointEditing.Move(wider,3,.95,.5);
            wider.Validate();
            var widePixels=Render(wider);
            if(Alpha(before,231,128)!=0||Alpha(widePixels,231,128)!=255||Alpha(widePixels,128,25)!=255)
                throw new Exception("Video failed to expand both horizontally and vertically for its outline.");
            var concave=PointEditing.Move(outline,1,.5,.5);
            var notched=Render(concave);
            if(Alpha(notched,128,76)!=0||Alpha(notched,77,153)!=255)
                throw new Exception("Concave video outline did not clip its notch or removed its interior.");
            foreach(var framing in new[] { new VideoFramingOptions(),new(){RotationDegrees=45},new(){Zoom=2,CenterX=.65,CenterY=.35,RotationDegrees=90} })
            {
                var rendered=Render(outward,framing);
                for(int y=5;y<250;y+=4)for(int x=5;x<250;x+=4)
                    if(ScreenSelection.Contains(outward,x/256d,y/256d)&&PolygonGeometry.ClosestEdge(outward,x/256d,y/256d,256,256).Distance>3&&Alpha(rendered,x,y)!=255)
                        throw new Exception("Zoom/rotation left an uncovered area inside the video outline.");
            }
            var skew=quad with{TopRight=new(.75,.3),BottomLeft=new(.3,.85)};
            var skewOutline=PointEditing.Insert(skew,0,new((skew.TopLeft.X+skew.TopRight.X)/2,(skew.TopLeft.Y+skew.TopRight.Y)/2),video:true);
            var skewBefore=Render(skew);var skewAfter=Render(skewOutline);
            for(int y=110;y<170;y+=10)for(int x=110;x<170;x+=10)for(int c=0;c<4;c++)
                if(Math.Abs(skewBefore[(y*256+x)*4+c]-skewAfter[(y*256+x)*4+c])>1)
                    throw new Exception("Adding outline points changed an existing skewed four-corner keystone.");
            using(var overlay=new CalibrationOverlay())
            {
                overlay.Initialize(graphics);
                var editing=options with{ConfigurationMode=true,CalibrationGridEnabled=true,DisplayScreens=new(){["TEST"]=[screen with{Points=concave}]}};
                overlay.Configure(editing); overlay.SelectScreen(true); overlay.SuppressHelp(true);
                graphics.BeginFrame(new(0,0,0,0)); overlay.Draw(graphics,editing,"TEST",new PointTransformCache(),screen.Id);
                using var buffer=graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                graphics.Context.CopyResource(staging,buffer);
                var map=graphics.Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                try
                {
                    if(Marshal.ReadByte(map.DataPointer+76*(int)map.RowPitch+128*4+3)>2)
                        throw new Exception("Configuration grid escaped the concave video outline.");
                }
                finally {graphics.Context.Unmap(staging,0);}
            }
            if(track.RequestId!=request||!track.IsOpen)throw new Exception("Outline edits restarted media playback.");
            Console.WriteLine("PASS production GPU polygon video: preserved keystone, outward coverage, concave clipping, zoom/rotation coverage and uninterrupted media.");
        }
        finally { File.Delete(path); MediaFactory.MFShutdown().CheckError(); }
    }
}
