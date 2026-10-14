using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoBooth.Networking;
using ExperienceX;
using Forms=System.Windows.Forms;

// Real application executables, separate processes and isolated hostname profiles.
internal static class LocalControllerExperienceChecks
{
    private static readonly List<string> Results=[];
    public static async Task Run(bool focusOnly=false, bool liveOnly=false)
    {
        var workspace=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../.."));
        var root=Path.Combine(workspace,".tools","local-controller-experience-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        var fakeDrive=Path.Combine(root,"OneDrive");var media=Path.Combine(root,"Media");Directory.CreateDirectory(media);
        var fixture=Environment.GetEnvironmentVariable("EXPERIENCEX_TEST_VIDEO") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Home Technologies","Controller","Media","Eye.mp4");
        var definitionsPort=int.Parse(Environment.GetEnvironmentVariable("CONTROLLER_TEST_DEFINITIONS_PORT") ?? "54244");
        File.Copy(fixture,Path.Combine(media,"Eye.mp4"));
        if (liveOnly)
        {
            using var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(media,"Bell.wav"), new NAudio.Wave.WaveFormat(44100,16,2));
            writer.Write(new byte[44100*4*2],0,44100*4*2); // Silent native routing fixture.
        }
        using var ca=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var ea=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        int commandPort=((IPEndPoint)ca.Client.LocalEndPoint!).Port,eventPort=((IPEndPoint)ea.Client.LocalEndPoint!).Port;ca.Dispose();ea.Dispose();
        var controllerId=Guid.NewGuid().ToString("N");var perspective=new PerspectiveDefinition{Name="bottom-third",Rectangle=new(){Y=66.67,Height=33.33}};
        var large=Enumerable.Range(0,499).Select(i=>new PerspectiveDefinition{Name="perspective"+i.ToString("D3")}).Prepend(perspective).ToArray();
        var cRoot=Path.Combine(fakeDrive,"Home Technologies","Bin","Controller");var xRoot=Path.Combine(fakeDrive,"Home Technologies","Bin","ExperienceX");
        CopyBuild(Path.Combine(workspace,"Controller","bin","Release","net9.0-windows"),cRoot);
        CopyBuild(Path.Combine(workspace,"experiencex","bin","Release","net9.0-windows"),xRoot);
        var cPath=Path.Combine(cRoot,$"appsettings.{Environment.MachineName}.json");var xPath=Path.Combine(xRoot,$"appsettings.{Environment.MachineName}.json");
        var logRoot=Path.Combine(root,"logs");
        var config=JsonNode.Parse(File.ReadAllText(Path.Combine(cRoot,"appsettings.defaults.json")))!.AsObject();
        config["ControllerId"]=controllerId;config["DeviceName"]="local-controller-test";config["ConfigurationMode"]=false;config["DefinitionsPort"]=definitionsPort;
        config["UdpCommandsPort"]=commandPort;config["UdpEventsPort"]=eventPort;config["Perspectives"]=JsonSerializer.SerializeToNode(large);
        config["Arduinos"]=new JsonArray();config["Triggers"]=new JsonArray();
        config["Scenes"]=JsonSerializer.SerializeToNode(new[]{new{Name="local-test",AmbientPlaylist="local-test-ambient"}});
        config["Playlists"]=JsonSerializer.SerializeToNode(new[]{new{Name="local-test-ambient",Type="ambient",Items=Array.Empty<object>()}});
        config["Telemetry"]=JsonSerializer.SerializeToNode(new{Directory=logRoot,HealthIntervalSeconds=2});Write(cPath,config);
        var displays=Forms.Screen.AllScreens.Select(d=>d.DeviceName).ToArray();var primary=Forms.Screen.PrimaryScreen!.DeviceName;
        var options=new ExperienceOptions{DeviceName="local-experience-test",ConfigurationMode=false,DeveloperMode=false,CalibrationGridEnabled=false,
            UdpCommandsPort=commandPort,UdpEventsPort=eventPort,UdpSendAttempts=1,MediaDiscovery=new(){Enabled=false},
            VideoDirectory=media,AudioDirectory=media,DisplayBackgrounds=displays.ToDictionary(d=>d,_=>new DisplayBackgroundOptions{IsTransparent=true}),
            DisplayScreens=displays.ToDictionary(d=>d,_=>new List<ScreenOptions>{new(){Id="probe",Name="probe",Points=new(){Enabled=true,TopLeft=new(.25,.25),TopRight=new(.75,.25),BottomLeft=new(.25,.75),BottomRight=new(.75,.75)}}})};
        var xConfig=JsonSerializer.SerializeToNode(options)!.AsObject();xConfig["Telemetry"]=JsonSerializer.SerializeToNode(new{Directory=logRoot,HealthIntervalSeconds=2});Write(xPath,xConfig);
        Process? controller=null,experience=null;
        try
        {
            controller=Launch(Path.Combine(cRoot,"Controller.exe"),fakeDrive,cRoot);experience=Launch(Path.Combine(xRoot,"ExperienceX.exe"),fakeDrive,xRoot);
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="ControllerReady")&&Events(logRoot,experience.Id).Count(e=>Name(e)=="DirectXRendererReady")==displays.Length,"Both real applications started",20000);
            if (liveOnly)
            {
                var liveMain = Windows(controller.Id).First(h => Visible(h) && Caption(h).Contains("Home Technologies"));
                nint Button(string caption) => Children(liveMain).First(h => Caption(h) == caption);
                var live = Button("Live"); await Until(() => IsWindowEnabled(live), "Live is available outside configuration mode"); SendMessage(live,0xf5,0,0);
                await Until(() => Events(logRoot,controller.Id).Any(e => Name(e)=="ExperienceInventoryUpdated"), "Controller discovers real Experience inventory over UDP and TCP");
                await Task.Delay(2000); File.WriteAllLines(Path.Combine(root,"live-captions.txt"),Children(liveMain).Select(Caption)); Snapshot(liveMain,Path.Combine(root,"live-before.png"));
                await Until(() => Children(liveMain).Count(h => Caption(h).Contains(" / probe Video screen"))==displays.Length, "Live displays every real screen target");
                SendMessage(Button("Select all"),0xf5,0,0);
                await Until(() => Children(liveMain).Any(h => Caption(h).EndsWith("Eye")), "Live carousel includes local video media");
                SendMessage(Children(liveMain).First(h=>Caption(h).EndsWith("Eye")),0xf5,0,0);
                await Until(() => Events(logRoot,experience.Id).Count(e=>Name(e)=="VideoPlaybackConfirmed")==displays.Length, "One Live click starts muted video on every selected display",20000);
                Check(Events(logRoot,experience.Id).Where(e=>Name(e)=="UdpRequestReceived").All(e=>!e.GetProperty("Data").GetProperty("Loop").GetBoolean()), "Live sends one-shot playback without looping");
                await Until(() => Children(liveMain).Any(h=>Caption(h).EndsWith("Bell")), "Live carousel includes audio media");
                SendMessage(Children(liveMain).First(h=>Caption(h).EndsWith("Bell")),0xf5,0,0);
                await Until(() => Events(logRoot,experience.Id).Any(e=>Name(e)=="AudioPlaybackStarted" || Name(e)=="AudioGroupPlaybackStarted" || Name(e)=="CoordinatedAudioPlaybackStarted"), "Live starts silent fixture audio on selected outputs",10000);
                Snapshot(liveMain,Path.Combine(root,"live.png"));
                Inject(0x11,0x10,0x12,0x43);
                await Until(()=>ReadNode(cPath)["ConfigurationMode"]!.GetValue<bool>()&&ReadExperience(xPath).ConfigurationMode,"Shared chord enables both processes");
                if (IsSecurityPromptForeground())
                { Results.Add("BLOCKED C/E foreground switching: Windows Security holds focus. Shortcut dispatch covered by unit checks."); Console.WriteLine(Results.Last()); }
                else
                {
                await Until(()=>ForegroundPid()==controller.Id,"Controller receives configuration focus");
                Inject(0x45); await Until(()=>ForegroundPid()==experience.Id,"E switches from Controller to Experience");
                await Task.Delay(300); Inject(0x43); await Until(()=>ForegroundPid()==controller.Id,"C switches from Experience to Controller");
                Check(ReadExperience(xPath).ConfigurationMode&&ReadNode(cPath)["ConfigurationMode"]!.GetValue<bool>(),"Application switching preserves configuration mode");
                }
                await Until(()=>IsWindowEnabled(Button("Perspectives")),"Perspectives enabled after configuration reload");
                SendMessage(Button("Perspectives"),0xf5,0,0);
                await Until(()=>Children(liveMain).Any(h=>Caption(h)=="Add perspective"),"Perspectives is embedded in the existing window");
                Check(Windows(controller.Id).Count(h=>Visible(h))==1,"Navigation creates no extra top-level Controller window");
                var led=Button("LED settings"); SendMessage(led,0xf5,0,0);
                await Until(()=>Children(liveMain).Any(h=>Caption(h)=="Save perspective"),"LED settings use embedded navigation");
                Check(Windows(controller.Id).Count(h=>Visible(h))==1,"LED settings creates no popup dialog");
                SendMessage(Button("Cancel"),0xf5,0,0);
                SendMessage(Button("← Dashboard"),0xf5,0,0);
                Inject(0x11,0x10,0x12,0x43);
                await Until(()=>!ReadNode(cPath)["ConfigurationMode"]!.GetValue<bool>()&&!ReadExperience(xPath).ConfigurationMode,"The same shared chord exits configuration mode in both processes");
                Check(!Events(logRoot,experience.Id).Any(e=>Name(e)=="ScreenRenderingFailed"),"Live playback has no screen rendering failures");
                Console.WriteLine("Local Live evidence: "+root); return;
            }
            if(focusOnly)
            {
                Inject(0x11,0x10,0x12,0x43);
                await Until(()=>ReadNode(cPath)["ConfigurationMode"]!.GetValue<bool>()&&ReadExperience(xPath).ConfigurationMode,"Actual shortcut enables both processes");
                await Until(()=>ForegroundPid()==controller.Id,"Controller receives focus",4000);
                Check(true,"Focus-only real-process check passed");return;
            }
            await Until(()=>ReadExperience(xPath).ControllerDefinitions.TryGetValue(controllerId,out var d)&&d.Perspectives.Length==500,"500 perspectives reached Experience over real UDP and HTTP",15000);
            using var http=new HttpClient(new HttpClientHandler{UseProxy=false});
            using var response=await http.GetAsync($"http://localhost:{definitionsPort}/definitions");var bytes=await response.Content.ReadAsByteArrayAsync();
            Check(response.IsSuccessStatusCode&&JsonSerializer.Deserialize<ControllerSharedDefinitions>(bytes,ControllerSharedDefinitions.Json)!.Perspectives.Length==500,"Real HTTP response contains all 500 independent perspectives");
            using var conditional=new HttpRequestMessage(HttpMethod.Get,$"http://localhost:{definitionsPort}/definitions");conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var notModified=await http.SendAsync(conditional);Check(notModified.StatusCode==HttpStatusCode.NotModified,"Unchanged HTTP snapshot returns 304");
            using var udp=new UdpClient(AddressFamily.InterNetwork);
            var network = new BroadcastOptions { DeviceName="local-test-parent",UdpCommandsPort=commandPort,UdpEventsPort=eventPort,UdpSendAttempts=1 };
            await UdpBroadcastSender.SendAsync(ControllerDiscovery.Request(network,"local-discovery"),network,CancellationToken.None);
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="DiscoveryResponseSent"),"Explicit discovery request produces the general announcement");
            var announcementCount=Events(logRoot,controller.Id).Count(e=>Name(e)=="ControllerAnnounced");
            await Until(()=>Events(logRoot,controller.Id).Count(e=>Name(e)=="ControllerAnnounced")>announcementCount,"Unsolicited ten-second announcement repeats",13000);
            var selection=ReadExperience(xPath).ControllerDefinitions[controllerId].Select(perspective);
            xConfig=ReadNode(xPath);
            foreach(var d in displays)xConfig["DisplayScreens"]![d]![0]!["Perspective"]=JsonSerializer.SerializeToNode(selection);
            Write(xPath,xConfig);
            await Send(new{experience="video",value="Eye.mp4",screen="*",mute=true,loop=true},"local-play");
            await Until(()=>Events(logRoot,experience.Id).Count(e=>Name(e)=="VideoPlaybackConfirmed"&&e.GetProperty("RequestId").GetString()=="local-play")==displays.Length,"Cropped video starts on every physical display",18000);
            await Until(()=>Events(logRoot,experience.Id).Count(e=>Name(e)=="DirectXPlaybackPerformance"&&e.GetProperty("RequestId").GetString()=="local-play"&&e.GetProperty("Data").GetProperty("TransferredVideoFrames").GetInt64()>10)>=displays.Length,"Every display transfers actual video frames",10000);
            config=ReadNode(cPath);var updated=perspective with{Name="right-half",Rectangle=new(){X=50,Width=50}};
            config["Perspectives"]=JsonSerializer.SerializeToNode(new[]{updated,new PerspectiveDefinition{Name="second"}});Write(cPath,config);
            await Until(()=>ReadExperience(xPath).DisplayScreens.Values.All(s=>s[0].Perspective!.Name=="right-half"&&s[0].Perspective!.Rectangle.X==50),"Live controller edit updates every bound screen without playback restart");
            Check(Events(logRoot,experience.Id).Count(e=>Name(e)=="VideoPlaybackConfirmed"&&e.GetProperty("RequestId").GetString()=="local-play")==displays.Length,"Crop updates retain the existing decoder sessions");
            var old=ReadExperience(xPath).ScreensFor(primary)[0].Perspective!.Rectangle;
            config["Perspectives"]=new JsonArray();Write(cPath,config);
            await Until(()=>ReadExperience(xPath).DisplayScreens.Values.All(s=>s[0].Perspective!.Available==false),"Deleting a perspective marks each bound selection unavailable");
            Check(ReadExperience(xPath).ScreensFor(primary)[0].Perspective!.Rectangle==old,"Deleted perspective retains its last crop");
            config["Perspectives"]=JsonSerializer.SerializeToNode(new[]{updated});Write(cPath,config);
            await Until(()=>ReadExperience(xPath).ScreensFor(primary)[0].Perspective!.Available,"Reintroducing the same perspective ID restores the binding");
            Inject(0x11,0x10,0x12,0x43);
            await Until(()=>ReadNode(cPath)["ConfigurationMode"]!.GetValue<bool>()&&ReadExperience(xPath).ConfigurationMode,"Actual global shortcut enables both separate processes");
            if(IsSecurityPromptForeground())
            {
                Results.Add("BLOCKED Controller focus: Windows Security retains the foreground");
                Console.WriteLine("BLOCKED interactive focus check: Windows Security retains the foreground; continuing other real-process checks.");
            }
            else
            {
                await Until(()=>ForegroundPid()==controller.Id,"Controller receives focus after the shared shortcut",5000);
                await Task.Delay(1200);Check(ForegroundPid()==controller.Id,"Experience watchdog does not steal Controller focus");
            }
            var main=Windows(controller.Id).First(h=>Visible(h)&&Caption(h).Contains("Controller"));
            var editorButton=Children(main).First(h=>Caption(h)=="Perspectives");
            await Until(()=>IsWindowEnabled(editorButton),"Controller enables the perspective editor after configuration reload");
            SendMessage(editorButton,0xf5,0,0);
            await Until(()=>Windows(controller.Id).Any(h=>Visible(h)&&Caption(h)=="Controller • Perspectives"),"Controller opens the actual perspective editor");
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="ControllerPreviewOpened"),"Controller preview decodes local video",15000);
            var editor=Windows(controller.Id).First(h=>Caption(h)=="Controller • Perspectives");
            var pause=Children(editor).First(h=>Caption(h)=="Pause");SendMessage(pause,0xf5,0,0);
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="ControllerPreviewPlaybackChanged"&&!e.GetProperty("Data").GetProperty("Playing").GetBoolean()),"Preview Pause works");
            var play=Children(editor).First(h=>Caption(h)=="Play");SendMessage(play,0xf5,0,0);
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="ControllerPreviewPlaybackChanged"&&e.GetProperty("Data").GetProperty("Playing").GetBoolean()),"Preview Play works");
            await Until(()=>Events(logRoot,controller.Id).Any(e=>Name(e)=="ControllerPreviewLooped"),"Preview loops naturally",20000);
            SendMessage(editor,0x10,0,0);
            await Until(()=>!Windows(controller.Id).Any(h=>Visible(h)&&Caption(h)=="Controller • Perspectives"),"Closing the editor releases its preview");
            await Stop(controller);controller=null;
            var cached=ReadExperience(xPath).ControllerDefinitions[controllerId];
            await Task.Delay(1500);Check(ReadExperience(xPath).ControllerDefinitions[controllerId].Perspectives.SequenceEqual(cached.Perspectives),"Experience retains definitions when Controller stops");
            await Stop(experience);experience=null;
            experience=Launch(Path.Combine(xRoot,"ExperienceX.exe"),fakeDrive,xRoot);
            await Until(()=>Events(logRoot,experience.Id).Count(e=>Name(e)=="DirectXRendererReady")==displays.Length,"Experience restarts independently");
            Check(ReadExperience(xPath).ControllerDefinitions[controllerId].Perspectives.Length==1,"The perspective cache survives restart without Controller");
            await Stop(experience);experience=null;
            var errors=Events(logRoot,null).Where(e=>e.TryGetProperty("Level",out var l)&&l.GetString()=="Error").ToArray();
            Check(errors.Length==0,"Neither application logged an error during the complete workflow");
            var performance=Events(logRoot,null).Where(e=>Name(e)=="DirectXPlaybackPerformance").ToArray();
            Check(performance.Length>=displays.Length && performance.All(e=>e.GetProperty("Data").GetProperty("MaxVideoTimestampGapMilliseconds").GetDouble()<10000),"All-display frame telemetry excludes no-frame sentinel timestamps");
            Console.WriteLine("Local evidence: "+root);
            async Task Send(object payload,string id)
            {
                var sender=new BroadcastOptions{DeviceName="local-test-parent",UdpCommandsPort=commandPort,UdpEventsPort=eventPort};
                await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender,"command","local-experience-test",payload,id),sender,CancellationToken.None);
            }
        }
        catch(Exception ex)
        {
            Results.Add("FAIL "+ex.Message);
            throw;
        }
        finally
        {
            Exception? cleanupError=null;
            foreach(var process in new[]{controller,experience}.Where(p=>p is not null))
                try { await Stop(process!); } catch(Exception ex) { cleanupError??=ex;Results.Add("FAIL cleanup: "+ex.Message); }
            File.WriteAllLines(Path.Combine(root,"checks.txt"),Results);
            if(cleanupError is not null)throw cleanupError;
        }
    }
    private static void Snapshot(nint window, string path)
    {
        GetWindowRect(window,out var rectangle);
        using var bitmap=new System.Drawing.Bitmap(rectangle.Right-rectangle.Left,rectangle.Bottom-rectangle.Top);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rectangle.Left,rectangle.Top,0,0,bitmap.Size);
        bitmap.Save(path);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out WindowRect rectangle);
    private static void CopyBuild(string source,string target)
    {
        Directory.CreateDirectory(target);
        foreach(var record in File.ReadAllLines(Path.Combine(source,"build-manifest.sha256")))
        {if(string.IsNullOrWhiteSpace(record))continue;var relative=record.Split('|',2)[1];var path=Path.Combine(target,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(Path.Combine(source,relative),path,true);}
        File.Copy(Path.Combine(source,"build-manifest.sha256"),Path.Combine(target,"build-manifest.sha256"),true);
    }
    private static Process Launch(string executable,string drive,string working)
    {var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=working};start.Environment["OneDrive"]=drive;return Process.Start(start)!;}
    private static async Task Stop(Process p)
    {
        if(p.HasExited){p.Dispose();return;}
        foreach(var h in Windows(p.Id).Where(Visible))PostMessage(h,0x10,0,0);
        try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}
        catch(TimeoutException){p.Kill(true);await p.WaitForExitAsync();throw new Exception("Application did not shut down gracefully: "+p.ProcessName);}
        finally{p.Dispose();}
    }
    private static JsonObject ReadNode(string path)
    {using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return JsonNode.Parse(stream,new(){PropertyNameCaseInsensitive=true})!.AsObject();}
    private static ExperienceOptions ReadExperience(string path)=>ExperienceOptions.Parse(ReadNode(path).ToJsonString());
    private static void Write(string path,JsonObject config)
    {var temp=path+".test-tmp";File.WriteAllText(temp,config.ToJsonString(new(){WriteIndented=true}));if(File.Exists(path))File.Move(temp,path,true);else File.Move(temp,path);}
    private static List<JsonElement> Events(string root,int? pid)
    {
        var result=new List<JsonElement>();if(!Directory.Exists(root))return result;
        foreach(var path in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories))
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);
            string? line;while((line=reader.ReadLine()) is not null){try{var e=JsonSerializer.Deserialize<JsonElement>(line);if(pid is null||e.GetProperty("ProcessId").GetInt32()==pid)result.Add(e);}catch(JsonException){}}
        }return result;
    }
    private static string? Name(JsonElement e)=>e.GetProperty("Event").GetString();
    private static void Check(bool yes,string text){if(!yes)throw new Exception(text);Results.Add("PASS "+text);Console.WriteLine("PASS "+text);}
    private static async Task Until(Func<bool> ready,string text,int milliseconds=8000)
    {var stop=Stopwatch.StartNew();while(stop.ElapsedMilliseconds<milliseconds){try{if(ready()){Check(true,text);return;}}catch(IOException){}await Task.Delay(30);}throw new TimeoutException(text+"; current foreground PID="+ForegroundPid());}
    private static List<nint> Windows(int pid){var list=new List<nint>();EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out var p);if(p==pid)list.Add(h);return true;},0);return list;}
    private static List<nint> Children(nint owner){var list=new List<nint>();EnumChildWindows(owner,(h,_)=>{list.Add(h);return true;},0);return list;}
    private static string Caption(nint h){var text=new StringBuilder(256);GetWindowText(h,text,text.Capacity);return text.ToString();}
    private static bool IsSecurityPromptForeground()
    {
        var foreground=GetForegroundWindow();
        GetWindowThreadProcessId(foreground,out var pid);
        try { using var process=Process.GetProcessById((int)pid);return process.ProcessName is "PickerHost" or "consent"; }
        catch(ArgumentException){return false;}
    }
    private static int ForegroundPid(){GetWindowThreadProcessId(GetForegroundWindow(),out var p);return (int)p;}
    private delegate bool Enumerate(nint h,nint l);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Enumerate callback,nint l);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(nint h,Enumerate callback,nint l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint h,StringBuilder text,int count);
    [DllImport("user32.dll",EntryPoint="IsWindowVisible")]private static extern bool Visible(nint h);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(nint h);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint h,out uint pid);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern nint SendMessage(nint h,uint msg,nint w,nint l);
    [DllImport("user32.dll")]private static extern bool PostMessage(nint h,uint msg,nint w,nint l);
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)]private struct InputUnion{[FieldOffset(0)]public Keyboard Key;[FieldOffset(0)]public Mouse Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public ushort Vk,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Mouse{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")]private static extern uint SendInput(uint count,Input[] input,int size);
    private static void Inject(params ushort[] keys)
    {var input=keys.Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k}}}).Concat(keys.Reverse().Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k,Flags=2}}})).ToArray();if(SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=input.Length)throw new Exception("Keyboard injection failed.");}
}
