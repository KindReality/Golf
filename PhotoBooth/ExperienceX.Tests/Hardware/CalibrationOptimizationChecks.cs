using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ExperienceX;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Forms = System.Windows.Forms;

internal static class CalibrationOptimizationChecks
{
    internal static byte[] ReferenceShader()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExperienceX.Tests.Fixtures.CalibrationReferencePS.cso")!;
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Vector4 Screen, Selection, Grid, Row0, Row1, Row2, Label, Join, Hint; }

    internal static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Check(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
    }
    private static void Check()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var window = new Forms.Form { ShowInTaskbar = false };
            using var graphics = new GraphicsResources(window.Handle, 640, 360, "TEST");
            graphics.Initialize();
            double time = 10;
            using var overlay = new CalibrationOverlay(() => time);
            overlay.Initialize(graphics);
            using var reference = graphics.Device.CreatePixelShader(ReferenceShader());
            using var constants = graphics.Device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer);
            using var blend = GraphicsResources.CreateVideoBlendState(graphics.Device);
            var shape = new PointOptions { Enabled = true, Vertices = [new(.15,.15),new(.45,.15),new(.45,.55),new(.15,.55)] };
            var definition = new ScreenOptions { Id = "a", Name = "shape", Points = shape };
            var options = new ExperienceOptions { ConfigurationMode = true, DisplayScreens = new() { ["TEST"] = [definition] } };
            var transforms = new PointTransformCache();
            int selected = -1, join = -1; bool flashing = false;
            byte[] Read()
            {
                using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                    (uint)graphics.Width, (uint)graphics.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                graphics.Context.CopyResource(staging, buffer);
                var mapped = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var pixels = new byte[graphics.Width * graphics.Height * 4];
                    for (int y = 0; y < graphics.Height; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, pixels, y * graphics.Width * 4, graphics.Width * 4);
                    return pixels;
                }
                finally { graphics.Context.Unmap(staging, 0); }
            }
            void Clear() => graphics.BeginFrame(new(0,0,0,0));
            byte[] Optimized()
            {
                Clear(); overlay.Draw(graphics, options, "TEST", transforms, "a", definition); return Read();
            }
            byte[] Original()
            {
                Clear();
                var vertices = definition.Points.Vertices.Select((p,i) => new Vector4((float)p.X,(float)p.Y,flashing ? 1 : 0,PointEditing.BoundaryIndex(definition.Points,i))).ToArray();
                using var points = graphics.Device.CreateBuffer<Vector4>(vertices, BindFlags.ShaderResource,
                    ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, structureByteStride:16);
                using var view = graphics.Device.CreateShaderResourceView(points);
                var center = ScreenNameLabel.Center(definition.Points, graphics.Width, graphics.Height);
                var data = new Constants { Screen = new(graphics.Width,graphics.Height,(float)(time%1000),options.ConfigurationMode ? 1 : 0),
                    Selection = new(selected,-1,flashing ? .5f : -1,1), Label = new(center.X,center.Y,options.ConfigurationMode && !definition.IsMat && !definition.IsVisualization ? .04f : 0,vertices.Length), Join = new(join,0,0,0) };
                if(definition.IsLedArray){data.Grid.X=-2;data.Row0.X=(float)(Math.Max(graphics.Width,graphics.Height)*definition.Perspective!.Led!.SamplingThicknessPercent/100);}
                graphics.Context.RSSetViewport(0,0,graphics.Width,graphics.Height);
                graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                graphics.Context.IASetInputLayout(null); graphics.Context.VSSetShader(graphics.VertexShader);
                graphics.Context.PSSetShader(reference); graphics.Context.PSSetConstantBuffer(0,constants);
                graphics.Context.UpdateSubresource(in data,constants); graphics.Context.PSSetShaderResource(1,view);
                graphics.Context.OMSetBlendState(blend); graphics.Context.Draw(3,0);
                graphics.Context.PSUnsetShaderResource(1); graphics.Context.OMSetBlendState(null);
                return Read();
            }
            void Compare(string name)
            {
                var expected = Original(); var actual = Optimized();
                int maximum = 0; long sum = 0;
                for(int i=0;i<actual.Length;i++){var delta=Math.Abs(actual[i]-expected[i]);maximum=Math.Max(maximum,delta);sum+=delta;}
                // Intermediate 8-bit premultiplied blending rounds once per point, unlike
                // the old single shader. Even crowded flashes must stay within 3/255.
                if(maximum>3 || sum/(double)actual.Length>.2) throw new Exception($"{name}: reference mismatch, maximum {maximum}, average {sum/(double)actual.Length:F4}.");
                Console.WriteLine($"PASS overlay reference pixels: {name}; maximum error {maximum}/255.");
            }
            Compare("small video and clipped glow");
            time += .1; Compare("pulsing cached video");
            if(overlay.StaticRebuilds!=1)throw new Exception("Pulsing rebuilt the stationary outline/fill.");
            selected=0;overlay.Show(0);Compare("point number");
            join=1;overlay.ShowJoinTarget(1);Compare("join halo and glyph");
            selected=-1;join=-1;overlay.Show(-1);overlay.ShowJoinTarget(-1);
            definition=definition with{Type="mat",Points=new(){Enabled=true,Vertices=[new(0,0),new(.8,.1),new(.5,.4),new(.9,.9),new(.1,.9)]}};
            options=options with{DisplayScreens=new(){["TEST"]=[definition]}};
            Compare("concave mat and display-edge point");
            definition=definition with{Points=new(){Enabled=true,Vertices=[new(.2,.2),new(.8,.2),new(.5,.8)]}};
            options=options with{DisplayScreens=new(){["TEST"]=[definition]}};
            Compare("triangular mat");
            definition=definition with{Points=new(){Enabled=true,Vertices=[new(.4,.4),new(.41,.4),new(.415,.405),new(.41,.41),new(.4,.41),new(.395,.405)]}};
            options=options with{DisplayScreens=new(){["TEST"]=[definition]}};
            Compare("overlapping point glows");
            definition=definition with{Type="fastled",Perspective=new(){Type="pixelArray",Led=new(){SamplingThicknessPercent=20}},Points=new(){Enabled=true,Vertices=[new(.1,.2),new(.8,.75)]}};
            options=options with{DisplayScreens=new(){["TEST"]=[definition]}};
            Compare("rotated LED sampling band and endpoints");
            options=options with{ConfigurationMode=false};overlay.Configure(options);overlay.FlashPoint(0,true);time+=.14;flashing=true;
            Compare("remote save flash outside configuration mode");
            time+=1;flashing=false;
            if(Optimized().Any(v=>v!=0))throw new Exception("Save flash left stale cached pixels.");

            options=options with{ConfigurationMode=true}; overlay.Configure(options);
            using var layer = new CalibrationLayer(()=>time);
            void DrawLayer(bool changed=false){Clear();layer.Draw(graphics,changed,overlay.Visible(options),()=>overlay.Draw(graphics,options,"TEST",transforms,"a",definition));}
            DrawLayer();var builds=layer.Rebuilds;
            for(int i=0;i<10;i++)DrawLayer();
            if(layer.Rebuilds!=builds)throw new Exception("An unchanged animation frame rebuilt its layer.");
            time+=1.0/30;DrawLayer();
            if(layer.Rebuilds!=builds+1)throw new Exception("30-FPS animation failed to refresh.");
            definition=definition with{Points=new(){Enabled=true,Vertices=[new(.6,.6),new(.95,.6),new(.8,.95)]}};
            options=options with{DisplayScreens=new(){["TEST"]=[definition]}};
            DrawLayer(true);
            if(layer.Rebuilds!=builds+2 || Read()[(72*640+128)*4+3]!=0)throw new Exception("An edit was delayed or retained old point pixels.");
            overlay.SelectScreen(true);DrawLayer(true);
            graphics.Resize(800,450); DrawLayer();
            if(layer.Rebuilds!=builds+4)throw new Exception("Resize did not invalidate the calibration layer.");
            options=options with{ConfigurationMode=false};overlay.Configure(options);DrawLayer(true);
            if(Read().Any(v=>v!=0))throw new Exception("Configuration exit retained the composed overlay.");
            definition=definition with{Perspective=new(){Type="pixelMatrix",Led=new()},Points=new(){Enabled=true,Vertices=[new(.3,.3),new(.7,.3),new(.7,.7),new(.3,.7)]}};
            options=options with{ConfigurationMode=true,DisplayScreens=new(){["TEST"]=[definition]}};
            overlay.Configure(options);overlay.SelectScreen(true);time+=.3;
            var matrix=Optimized();var handle=LedGeometry.RotationHandle(definition.Points,graphics.Width,graphics.Height);
            var ring=((int)(handle.Y*(graphics.Height-1))*graphics.Width+(int)(handle.X*(graphics.Width-1)+9))*4;
            if(matrix[ring+3]<100 || matrix[ring]<=matrix[ring+2])throw new Exception("LED rotation handle was clipped outside its polygon bounds.");
            Console.WriteLine("PASS static caching, 30-FPS animation, immediate edits/selection, resize and no stale configuration pixels.");
            Benchmark(graphics);
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }
    private static unsafe void Benchmark(GraphicsResources graphics)
    {
        graphics.Resize(1280,720);
        double time=50;
        using var layer=new CalibrationLayer(()=>time);
        using var shader=graphics.Device.CreatePixelShader(ReferenceShader());
        using var constants=graphics.Device.CreateBuffer((uint)Marshal.SizeOf<Constants>(),BindFlags.ConstantBuffer);
        using var blend=GraphicsResources.CreateVideoBlendState(graphics.Device);
        var definitions=Enumerable.Range(0,16).Select(i=>new ScreenOptions{Id="bench"+i,Name="bench"+i,Points=new(){Enabled=true,Vertices=[
            new((i%4)/4.0,(i/4)/4.0),new((i%4+1)/4.0,(i/4)/4.0),
            new((i%4+1)/4.0,(i/4+1)/4.0),new((i%4)/4.0,(i/4+1)/4.0)]}}).ToArray();
        var options=new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["TEST"]=[..definitions]}};
        var overlays=new List<CalibrationOverlay>();
        var buffers=new List<ID3D11Buffer>();var views=new List<ID3D11ShaderResourceView>();
        var transforms=definitions.Select(_=>new PointTransformCache()).ToArray();
        try
        {
            foreach(var definition in definitions)
            {
                var overlay=new CalibrationOverlay(()=>time);overlays.Add(overlay);overlay.Initialize(graphics);
                var points=definition.Points.Vertices.Select((p,i)=>new Vector4((float)p.X,(float)p.Y,0,PointEditing.BoundaryIndex(definition.Points,i))).ToArray();
                var buffer=graphics.Device.CreateBuffer<Vector4>(points,BindFlags.ShaderResource,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferStructured,structureByteStride:16);
                buffers.Add(buffer);views.Add(graphics.Device.CreateShaderResourceView(buffer));
            }
            void OriginalFrame()
            {
                graphics.BeginFrame(new(0,0,0,0));graphics.Context.OMSetBlendState(blend);
                graphics.Context.RSSetViewport(0,0,1280,720);graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                graphics.Context.IASetInputLayout(null);graphics.Context.VSSetShader(graphics.VertexShader);graphics.Context.PSSetShader(shader);
                graphics.Context.PSSetConstantBuffer(0,constants);graphics.Context.PSUnsetShaderResource(0);
                for(int i=0;i<definitions.Length;i++)
                {
                    var center=ScreenNameLabel.Center(definitions[i].Points,1280,720);
                    var data=new Constants{Screen=new(1280,720,(float)time,1),Selection=new(-1,0,-1,0),Label=new(center.X,center.Y,.04f,4),Join=new(-1,0,0,0)};
                    graphics.Context.UpdateSubresource(in data,constants);graphics.Context.PSSetShaderResource(1,views[i]);graphics.Context.Draw(3,0);
                }
                graphics.Context.PSUnsetShaderResource(1);graphics.Context.OMSetBlendState(null);
            }
            void OptimizedFrame()
            {
                graphics.BeginFrame(new(0,0,0,0));
                layer.Draw(graphics,false,true,()=>{for(int i=0;i<definitions.Length;i++)overlays[i].Draw(graphics,options,"TEST",transforms[i],definitions[i].Id,definitions[i]);});
            }
            T ReadQuery<T>(ID3D11Query query) where T:unmanaged
            {
                var wait=Stopwatch.StartNew();T value=default;
                while(true)
                {
                    var result=graphics.Context.GetData(query,(nint)(&value),(uint)sizeof(T),AsyncGetDataFlags.None);result.CheckError();
                    if(result.Code==0)return value;
                    if(wait.Elapsed.TotalSeconds>5)throw new TimeoutException("GPU benchmark query timed out.");
                    Thread.Sleep(1);
                }
            }
            (ulong Pixels,double Milliseconds) Measure(Action draw)
            {
                using var pipeline=graphics.Device.CreateQuery(new(QueryType.PipelineStatistics));
                using var disjoint=graphics.Device.CreateQuery(new(QueryType.TimestampDisjoint));
                using var first=graphics.Device.CreateQuery(new(QueryType.Timestamp));
                using var last=graphics.Device.CreateQuery(new(QueryType.Timestamp));
                graphics.Context.Begin(pipeline);graphics.Context.Begin(disjoint);graphics.Context.End(first);
                for(int frame=0;frame<24;frame++){time=100+frame/60.0;draw();}
                graphics.Context.End(last);graphics.Context.End(disjoint);graphics.Context.End(pipeline);
                var stats=ReadQuery<QueryDataPipelineStatistics>(pipeline);var frequency=ReadQuery<QueryDataTimestampDisjoint>(disjoint);
                var start=ReadQuery<ulong>(first);var end=ReadQuery<ulong>(last);
                return(stats.PSInvocations,frequency.Disjoint || frequency.Frequency==0 ? double.NaN : (end-start)*1000.0/frequency.Frequency/24);
            }
            OriginalFrame();OptimizedFrame(); // Compile/allocate/cache before measurement.
            var original=Measure(OriginalFrame);var optimized=Measure(OptimizedFrame);
            if(optimized.Pixels>=original.Pixels*.4)throw new Exception("Overlay optimization failed to reduce pixel shader work by at least 60%.");
            var result=new{Width=1280,Height=720,Screens=16,Frames=24,OriginalPixelInvocations=original.Pixels,OptimizedPixelInvocations=optimized.Pixels,
                PixelWorkReductionPercent=100*(1-optimized.Pixels/(double)original.Pixels),OriginalGpuMillisecondsPerFrame=original.Milliseconds,OptimizedGpuMillisecondsPerFrame=optimized.Milliseconds};
            var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.tools/calibration-benchmark.json"));
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS 16-screen GPU benchmark: pixel shader work reduced {result.PixelWorkReductionPercent:F1}%; GPU time {original.Milliseconds:F3} -> {optimized.Milliseconds:F3} ms/frame (overlay only).");
        }
        finally{foreach(var view in views)view.Dispose();foreach(var buffer in buffers)buffer.Dispose();foreach(var overlay in overlays)overlay.Dispose();}
    }
}
