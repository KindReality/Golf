using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;

internal static class LedChecks
{
    internal static readonly string DeviceJson = """
        {"info":{"brand":"WLED","name":"Test lights","mac":"aa:bb:cc:dd:ee:ff","leds":{"count":15,"matrix":{"w":5,"h":3}}},
         "state":{"seg":[{"id":0,"n":"Top row","start":0,"stop":5,"startY":0,"stopY":1},
                           {"id":2,"n":"Panel","start":2,"stop":4,"startY":1,"stopY":3}]}}
        """;
    private sealed class Transport(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Check(request.RequestUri!.AbsolutePath=="/json","WLED inventory uses the read-only JSON API");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json)});
        }
    }
    internal static async Task Run()
    {
        using var client=new WledClient(new Transport(DeviceJson));
        var device=await client.ReadAsync("127.0.0.1:12345");
        Check(device.Mac=="aabbccddeeff" && device.Segments.Length==2,"WLED identity and multiple segments are read");
        const string settings="{\"if\":{\"live\":{\"en\":true,\"mso\":false,\"rlm\":true,\"timeout\":25,\"offset\":0,\"dmx\":{\"addr\":1}}}}";
        using(var config=JsonDocument.Parse(settings)) WledClient.ValidateStreaming(config.RootElement);
        foreach(var invalid in new[]{settings.Replace("\"mso\":false","\"mso\":true"),settings.Replace("\"rlm\":true","\"rlm\":false"),settings.Replace("\"timeout\":25","\"timeout\":650"),settings.Replace("\"offset\":0","\"offset\":1")})
            Reject(()=>{using var config=JsonDocument.Parse(invalid);WledClient.ValidateStreaming(config.RootElement);});
        var panel=device.Segments.Single(s=>s.Id==2);
        Check(panel.Width==2 && panel.Height==2 && panel.PixelIndices(device.MatrixWidth).SequenceEqual(new[]{7,8,12,13}),"Matrix segment maps each row into the whole device frame");
        var led=device.Perspective(panel,true);
        led.Validate("pixelMatrix");
        Check(led.FramesPerSecond==30 && led.SamplingThicknessPercent==10,"Per-perspective FPS and percentage defaults");
        foreach(var bad in new[]{led with{DeviceMac="bad"},led with{FramesPerSecond=0},led with{FramesPerSecond=121},led with{SamplingThicknessPercent=double.NaN},led with{Width=0},led with{Address="host/path"}})
            Reject(()=>bad.Validate("pixelMatrix"));
        var definition=new PerspectiveDefinition{Name="panel",Type="pixelMatrix",Led=led};
        var definitions=new ControllerSharedDefinitions{ControllerId=Guid.NewGuid().ToString("N"),ControllerName="test",Hostname=Environment.MachineName,Perspectives=[definition]};
        var wire=definitions.Serialize();
        var restored=JsonSerializer.Deserialize<ControllerSharedDefinitions>(wire,ControllerSharedDefinitions.Json)!;
        restored.Validate();
        Check(restored.Select(restored.Perspectives[0]).Led==led,"Controller announcements preserve complete LED settings and stable identities");
        Reject(()=>new PerspectiveDefinition{Type="media",Led=led}.Validate());
        Reject(()=>new PerspectiveDefinition{Type="pixelMatrix"}.Validate());
        Reject(()=>new ExperienceOptions{DisplayScreens=new(){["display"]=[new ScreenOptions{Perspective=definitions.Select(definition)}]}}.Validate());
        var pixels=Enumerable.Range(0,1300*3).Select(i=>(byte)(i%251)).ToArray();
        var packets=DdpFrames.Packets(pixels,17).ToArray();
        Check(packets.Length==3 && packets.All(p=>p.Length<=1450 && p[1]==2 && p[2]==0x0B && p[3]==1),"DDP uses RGB24, bounded datagrams and wrapped sequence numbers");
        Check(packets.Take(2).All(p=>(p[0]&1)==0) && (packets[^1][0]&1)==1,"Only final packet commits the composed device frame");
        var assembled=new byte[pixels.Length];
        foreach(var p in packets)
        {
            int offset=(int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)),length=BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8));
            Check(length==p.Length-10 && offset%3==0,"DDP offsets and payload lengths address complete RGB pixels");
            p.AsSpan(10).CopyTo(assembled.AsSpan(offset));
        }
        Check(assembled.SequenceEqual(pixels),"Multi-packet RGB frame reassembles exactly");
        Reject(()=>DdpFrames.Packets(new byte[4],1).ToArray());
        CheckDns();
        var line=LedGeometry.Initial(true); LedGeometry.Validate(line,true);
        Check(line.Vertices[0]==new ScreenPoint(0,.5) && line.Vertices[1]==new ScreenPoint(1,.5),"New arrays span the full display width");
        var band=LedGeometry.Band(line,10,1920,1080);
        Check(Math.Abs((band.Vertices[3].Y-band.Vertices[0].Y)*1079-192)<1e-6,"Array thickness is ten percent of the greater display dimension");
        Check(LedGeometry.Initial(false).Vertices.SequenceEqual(new PointOptions().Vertices),"New matrices occupy the full display");
        var rect=new PointOptions{Enabled=true,TopLeft=new(.25,.25),TopRight=new(.75,.25),BottomLeft=new(.25,.75),BottomRight=new(.75,.75)};
        var rotation=LedGeometry.Rotate(rect,.5,.1,.9,.5,1920,1080);
        LedGeometry.Validate(rotation,false);
        Check(rotation!=rect && Perpendicular(rotation,1920,1080),"Matrix rotation preserves a physical rectangle");
        var resized=LedGeometry.Resize(rotation,3,.70,.85,1920,1080);
        LedGeometry.Validate(resized,false);
        Check(Perpendicular(resized,1920,1080),"Rotated matrix resize remains rectangular");
        var matrixSurface=new Surface("matrix");var videoSurface=new Surface("video");
        PointOptions Quad(double left,double right)=>new(){Enabled=true,TopLeft=new(left,.1),TopRight=new(right,.1),BottomLeft=new(left,.4),BottomRight=new(right,.4)};
        var snapping=new PointSnapping(new(){[matrixSurface]=Quad(.1,.4),[videoSurface]=Quad(.5,.8)},new(){[matrixSurface]=false,[videoSurface]=false},(a,b)=>a==b || a.Surface!=b.Surface,new(){[matrixSurface]=true,[videoSurface]=false});
        var selected=new PointHandle(matrixSurface,1);snapping.Select(selected);
        snapping.Move(selected,.49,.1);
        Check(snapping.Join(selected,5,0,0) is not null,"J joins a matrix corner to another screen's point");
        snapping.Move(selected,.55,.16);
        Check(snapping.Drafts[matrixSurface].TopRight==snapping.Drafts[videoSurface].TopLeft && Perpendicular(snapping.Drafts[matrixSurface],1920,1080),"Shared matrix/video points move together while the matrix stays rectangular");
        Reject(()=>LedGeometry.Validate(line with{Vertices=[new(.5,.5),new(.5,.5)]},true));
        var folder=Path.Combine(Path.GetTempPath(),"LedChecks-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"appsettings.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["display"]=[]}},ConfigurationSchema.Json));
        var store=new PointConfigurationStore(path);
        var selection=definitions.Select(definition);
        var screen=await store.AddScreenAsync("display",type:"fastled",perspective:selection);
        var options=ExperienceOptions.Parse(File.ReadAllText(path));
        Check(screen.IsLed && screen.Points==LedGeometry.Initial(false),"First matrix screen occupies the whole display and persists");
        var changed=definitions with{Perspectives=[definition with{Led=led with{FramesPerSecond=45}}]};
        await store.ApplyControllerDefinitionsAsync(changed);
        Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("display")[0].Perspective!.Led!.FramesPerSecond==45,"Controller LED edits immediately update existing Experience screen settings");
        var arrayDefinition=new PerspectiveDefinition{Name="line",Type="pixelArray",Led=device.Perspective(device.Segments[0],false)};
        var array=await store.AddScreenAsync("display",type:"fastled",perspective:definitions.Select(arrayDefinition));
        Check(array.Points==LedGeometry.Initial(true),"An array added after another screen still spans the full width");
        var moved=PointEditing.Translate(array.Points,0,.1);
        await store.SaveAsync("display",array.Points,moved,array.Id);
        options=ExperienceOptions.Parse(File.ReadAllText(path));
        Check(options.ScreensFor("display")[1].Points.Vertices.Count==2 && options.ScreensFor("display")[1].Points==moved,"Two-point arrays support moving and atomic saving");
        Check(ScreenMediaCatalog.Empty.Match(screen) is null,"LED screens never acquire media-name playback bindings");
        Console.WriteLine("Passed LED definitions, WLED inventory, DDP framing, DNS-SD parsing, geometry and persistence.");
    }
    private static bool Perpendicular(PointOptions p,int w,int h)
    {
        var a=LedGeometry.Pixel(p.TopLeft,w,h); var b=LedGeometry.Pixel(p.TopRight,w,h); var c=LedGeometry.Pixel(p.BottomLeft,w,h);
        return Math.Abs((b.X-a.X)*(c.X-a.X)+(b.Y-a.Y)*(c.Y-a.Y))<1e-5;
    }
    private static void CheckDns()
    {
        using var stream=new MemoryStream(); stream.Write(new byte[]{0,0,0x84,0,0,0,0,3,0,0,0,0});
        void Name(string name) { foreach(var part in name.Split('.')) { stream.WriteByte((byte)part.Length); stream.Write(Encoding.ASCII.GetBytes(part)); } stream.WriteByte(0); }
        void Record(string name,int type,Action payload)
        {
            Name(name); stream.WriteByte(0);stream.WriteByte((byte)type);stream.Write(new byte[]{0,1,0,0,0,120});
            long at=stream.Position;stream.Write(new byte[2]);long start=stream.Position; payload();long end=stream.Position;
            stream.Position=at;stream.WriteByte((byte)((end-start)>>8));stream.WriteByte((byte)(end-start));stream.Position=end;
        }
        Record("_wled._tcp.local",12,()=>Name("lights._wled._tcp.local"));
        Record("lights._wled._tcp.local",33,()=>{stream.Write(new byte[]{0,0,0,0,0,80});Name("lights.local");});
        Record("lights.local",1,()=>stream.Write(new byte[]{192,168,1,106}));
        var catalog=new WledDiscovery.DnsCatalog();catalog.Add(stream.ToArray());
        Check(catalog.Addresses.SequenceEqual(new[]{"192.168.1.106"}),"DNS-SD correlates WLED PTR, SRV and address records");
        catalog.Add(new byte[3]);
        var loop=new byte[24];loop[2]=0x80;loop[7]=1;loop[12]=0xC0;loop[13]=12;
        Reject(()=>catalog.Add(loop));
    }
    private static void Check(bool condition,string name) { if(!condition) throw new Exception(name); }
    private sealed class Surface(string id) : IPointSurface
    {
        public string DisplayName=>"display";public string ScreenId=>id;public int PixelWidth=>1920;public int PixelHeight=>1080;
        public void ShowCalibration(int point){}public void FadeCalibration(){}public void FlashCalibration(int point,bool success){}public void FlashCalibrationPoints(int mask){}public void PreviewPoints(PointOptions? value){}
        public Task<bool> ConfirmCalibrationAsync(PointOptions expected)=>Task.FromResult(true);public void ReleaseCalibrationCapture(){}
    }
    private static void Reject(Action action) { try {action();} catch(ArgumentException) {return;} throw new Exception("Invalid input was accepted."); }
}
