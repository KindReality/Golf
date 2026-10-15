using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;
using Forms=System.Windows.Forms;

internal static class ExperienceRestartChecks
{
    internal static async Task Run()
    {
        if(Process.GetProcessesByName("ExperienceX").Any() || Process.GetProcessesByName("Controller").Any())
            throw new Exception("Close local Controller/Experience before the isolated restart test.");
        var root=Path.Combine(Path.GetTempPath(),"ExperienceX-restart-test-"+Guid.NewGuid().ToString("N"));
        var drive=Path.Combine(root,"OneDrive");var source=Path.Combine(drive,"Home Technologies","Bin","ExperienceX");
        var temp=Path.Combine(root,"Temp");var runtime=Path.Combine(temp,"Experience");var media=Path.Combine(root,"Media");
        Directory.CreateDirectory(source);Directory.CreateDirectory(runtime);Directory.CreateDirectory(media);
        var package=Path.GetFullPath("experiencex/bin/Release/net9.0-windows");
        foreach(var line in File.ReadAllLines(Path.Combine(package,"build-manifest.sha256")))
        {
            if(!line.Trim().Any())continue;var name=line.Split('|',2)[1];var target=Path.Combine(source,name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(package,name),target);
        }
        File.Copy(Path.Combine(package,"build-manifest.sha256"),Path.Combine(source,"build-manifest.sha256"));
        var keptMedia=Path.Combine(runtime,"Media","keep.bin");Directory.CreateDirectory(Path.GetDirectoryName(keptMedia)!);File.WriteAllText(keptMedia,"retained-media");
        int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
        var display=Forms.Screen.PrimaryScreen!.DeviceName;var logs=Path.Combine(root,"logs");
        var options=new ExperienceOptions{ConfigurationMode=false,CalibrationGridEnabled=false,MediaDiscovery=new(){Enabled=false},
            VideoDirectory=media,AudioDirectory=media,UdpCommandsPort=Port(),UdpEventsPort=Port(),DefaultDisplayName=display,
            DisplayBackgrounds=Forms.Screen.AllScreens.ToDictionary(s=>s.DeviceName,_=>new DisplayBackgroundOptions{IsTransparent=true}),
            DisplayScreens=Forms.Screen.AllScreens.ToDictionary(s=>s.DeviceName,_=>new List<ScreenOptions>{new(){Id="preserved",Name="preserved",Brightness=55,Roles="wall"}})};
        var node=JsonSerializer.SerializeToNode(options)!.AsObject();node["CustomRestartMarker"]="preserved";node["Telemetry"]=JsonSerializer.SerializeToNode(new{Directory=logs,HealthIntervalSeconds=2});
        var config=Path.Combine(source,$"appsettings.{Environment.MachineName}.json");File.WriteAllText(config,node.ToJsonString());
        Process? original=null;
        try
        {
            var start=new ProcessStartInfo(Path.Combine(source,"ExperienceX.exe")){UseShellExecute=false,WorkingDirectory=source,CreateNoWindow=true};
            start.Environment["OneDrive"]=drive;start.Environment["TEMP"]=temp;start.Environment["TMP"]=temp;
            original=Process.Start(start)!;
            await Until(()=>Events().Any(e=>Event(e)=="DirectXRendererReady"),"Isolated Experience started");
            Inject(0x11,0x10,0x43);
            await Until(()=>ExperienceOptions.Parse(File.ReadAllText(config)).ConfigurationMode&&ForegroundPid()==original.Id,"Configuration shortcut focuses Experience");
            await Task.Delay(500);Inject(0x58);
            await Until(()=>original.HasExited,"X shuts down the original Experience process",12000);
            await Until(()=>Process.GetProcessesByName("ExperienceX").Any(p=>PathFor(p).Equals(Path.Combine(runtime,"ExperienceX.exe"),StringComparison.OrdinalIgnoreCase)),"Restart script launches a new local runtime",15000);
            await Until(()=>Events().Count(e=>Event(e)=="ApplicationStarted")>=2,"Restarted Experience logs a fresh session");
            var restored=ExperienceOptions.Parse(File.ReadAllText(config));
            Check(restored.ConfigurationMode&&restored.ScreensFor(display).Single().Brightness==55&&restored.ScreensFor(display).Single().Roles=="wall"&&
                JsonNode.Parse(File.ReadAllText(config))!["CustomRestartMarker"]!.GetValue<string>()=="preserved","Restart preserves configuration and configuration mode");
            Check(File.ReadAllText(keptMedia)=="retained-media","Restart retains local media");
            await Until(()=>Events().Any(e=>Event(e)=="ApplicationStopped"&&e.GetProperty("ProcessId").GetInt32()==original.Id),"Original process completed graceful shutdown");
            Check(Events().Any(e=>Event(e)=="RestartHotkeyPressed")&&Events().Any(e=>Event(e)=="ApplicationRestartRequested"),"Restart logs identify the keyboard request");
        }
        finally
        {
            foreach(var process in Process.GetProcessesByName("ExperienceX").Where(p=>PathFor(p).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
            {
                try{process.CloseMainWindow();if(!process.WaitForExit(6000)){process.Kill();process.WaitForExit(5000);}}
                finally{process.Dispose();}
            }
            original?.Dispose();Console.WriteLine("Restart evidence: "+root);
        }
        List<JsonElement> Events()
        {
            var result=new List<JsonElement>();if(!Directory.Exists(logs))return result;
            foreach(var file in Directory.EnumerateFiles(logs,"*.jsonl",SearchOption.AllDirectories))
            {
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);
                while(reader.ReadLine() is {} line){try{using var document=JsonDocument.Parse(line);result.Add(document.RootElement.Clone());}catch(JsonException){}}
            }
            return result;
        }
    }
    private static string Event(JsonElement e)=>e.GetProperty("Event").GetString()!;
    private static string PathFor(Process p){try{return p.MainModule?.FileName??"";}catch{return "";}}
    private static void Check(bool good,string text){if(!good)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private static async Task Until(Func<bool> ready,string text,int timeout=12000){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<timeout){try{if(ready()){Check(true,text);return;}}catch(IOException){}await Task.Delay(40);}throw new TimeoutException(text);}
    private static int ForegroundPid(){GetWindowThreadProcessId(GetForegroundWindow(),out var pid);return (int)pid;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public Union Data;}
    [StructLayout(LayoutKind.Explicit)]private struct Union{[FieldOffset(0)]public Keyboard Key;[FieldOffset(0)]public Mouse Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public ushort Vk,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Mouse{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")]private static extern uint SendInput(uint count,Input[] input,int size);
    private static void Inject(params ushort[] keys){var input=keys.Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k}}}).Concat(keys.Reverse().Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k,Flags=2}}})).ToArray();if(SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=input.Length)throw new Exception("Shortcut input failed.");}
}
