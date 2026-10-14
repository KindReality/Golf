using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms=System.Windows.Forms;

internal static class GForceFullscreenChecks
{
    public static async Task Run(bool chromaKey=false,bool nativeControl=false)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../.."));
                var run=Path.Combine(root,".tools","gforce-fullscreen-"+(chromaKey?"key-":"")+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(run);
                Process? probe=null;
                Exception? failure=null;
                Task<string>? output=null,errors=null;
                try
                {
                    var start=new ProcessStartInfo("python"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true};
                    start.ArgumentList.Add(Path.Combine(root,"ExperienceX.Tests","Scripts","Trace-GForceGraphics.py"));start.ArgumentList.Add("--run");start.ArgumentList.Add(run);
                    probe=Process.Start(start)!;output=probe.StandardOutput.ReadToEndAsync();errors=probe.StandardError.ReadToEndAsync();
                    await Until(()=>File.Exists(Path.Combine(run,"process.json")),"Graphics trace attaches after normal G-Force startup",15000);
                    var pid=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(run,"process.json"))).GetProperty("pid").GetInt32();
                    nint normal=0;
                    await Until(()=>{normal=Windows(pid).FirstOrDefault(h=>Class(h)=="SoundSpectrum G-Force Standalone"&&IsWindowVisible(h));return normal!=0;},"Standalone window appears",15000);
                    var display=Forms.Screen.PrimaryScreen!;var bounds=display.Bounds;
                    SetWindowPos(normal,0,bounds.Left+80,bounds.Top+80,800,600,0x14);
                    await Until(()=>{try{return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(run,"graphics-summary.json"))).GetProperty("d3d9Presents").GetInt32()>5;}catch(IOException){return false;}catch(JsonException){return false;}},"G-Force begins rendering before the fullscreen request",20000);
                    if(nativeControl)Check(GForceWindow.EnterFullscreen(normal),"Native fullscreen request is delivered without global input");
                    else
                    {
                        using var focus=new FocusHotkey(()=>{AllowSetForegroundWindow((uint)pid);SetForegroundWindow(normal);});
                        Inject(0x11,0x10,0x12,0x78);
                        await Until(()=>ForegroundPid()==pid,"The test hotkey grants foreground focus to G-Force",5000);
                        Inject(0x10,0x0d);
                    }
                    nint fullscreen=0;
                    await Until(()=>{fullscreen=Windows(pid).FirstOrDefault(h=>Class(h)=="SSFullscreenWindowClass"&&IsWindowVisible(h));return fullscreen!=0;},"G-Force enters its real fullscreen mode",8000);
                    await Task.Delay(2000);
                    Capture("gforce-fullscreen.png").Dispose();
                    using var commandSocket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var eventSocket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
                    int commands=((IPEndPoint)commandSocket.Client.LocalEndPoint!).Port,events=((IPEndPoint)eventSocket.Client.LocalEndPoint!).Port;commandSocket.Dispose();eventSocket.Dispose();
                    var options=new ExperienceOptions{DeveloperMode=false,ConfigurationMode=false,MediaDiscovery=new(){Enabled=false},UdpCommandsPort=commands,UdpEventsPort=events,TransitionInSeconds=.1,
                        DisplayBackgrounds=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,_=>new DisplayBackgroundOptions{IsTransparent=true}),
                        DisplayScreens=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,d=>d.Primary?new List<ScreenOptions>{
                            new(){Id="media",Name="overlay-probe",BlackKey=new(){Enabled=chromaKey},Points=new(){Enabled=true,TopLeft=new(.08,.16),TopRight=new(.55,.16),BottomLeft=new(.08,.75),BottomRight=new(.55,.75)}},
                            new(){Id="mat",Name="blackout-probe",Type="mat",Points=new(){Enabled=true,TopLeft=new(.68,.15),TopRight=new(.9,.15),BottomLeft=new(.68,.7),BottomRight=new(.9,.7)}}}:new List<ScreenOptions>())};
                    var path=Path.Combine(run,"appsettings.json");File.WriteAllText(path,JsonSerializer.Serialize(options));
                    await using(var host=new ExperienceHost(dispatcher,path))
                    {
                        host.Start();var window=host.DisplayWindows.Single(w=>w.DisplayName==display.DeviceName);
                        var video=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Home Technologies","Controller","Media","Eye.mp4");
                        window.Renderer!.Play(video,new(){Experience="video",Value="Eye.mp4",Loop=true,Mute=true,ScreenId="media",RequestId="fullscreen-overlay-test"});
                        await UntilAsync(async()=>{var s=(await window.Renderer.SnapshotAsync()).Single(s=>s.Id=="media");return s.Frames>30&&s.Opacity==1;},"Experience video decodes and becomes opaque above G-Force",12000);
                        Check(IsWindowVisible(fullscreen),"G-Force stays in fullscreen while Experience is shown");
                        Check(ForegroundPid()!=Environment.ProcessId,"Experience overlays do not take foreground focus");
                        Check(Above(window.Handle,fullscreen),"Experience surface is above the G-Force fullscreen window");
                        var bitmap=Capture("gforce-fullscreen-overlay.png");
                        if(chromaKey)
                        {
                            // Eye.mp4 has black source pixels along its top edge, away from the eye itself.
                            int visible=0;
                            for(int y=(int)(bounds.Height*.17);y<(int)(bounds.Height*.18);y++)
                            for(int x=(int)(bounds.Width*.1);x<(int)(bounds.Width*.53);x+=4)
                            {var pixel=bitmap.GetPixel(x,y);if(pixel.B>20||pixel.G>20)visible++;}
                            Check(visible>20,"G-Force colors are visible through Eye.mp4's keyed black source pixels");
                        }
                        int black=0,total=0;for(int y=(int)(bounds.Height*.25);y<(int)(bounds.Height*.6);y+=8)for(int x=(int)(bounds.Width*.73);x<(int)(bounds.Width*.85);x+=8){var p=bitmap.GetPixel(x,y);if(p.R<3&&p.G<3&&p.B<3)black++;total++;}
                        bitmap.Dispose();Check(black==total,"Mat is black on the actual desktop capture above G-Force");
                        SetWindowPos(fullscreen,(nint)(-1),0,0,0,0,0x13);
                        await Until(()=>Above(window.Handle,fullscreen),"Experience reasserts its position after G-Force is raised",4000);
                        await Task.Delay(1000);Capture("gforce-fullscreen-restacked.png").Dispose();
                        Check(IsWindowVisible(fullscreen),"G-Force remains fullscreen after topmost recovery");
                        var beforeFocus=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(run,"graphics-summary.json"))).GetProperty("fullscreenPresented").GetInt32();
                        var editing=options with{ConfigurationMode=true};
                        var replacement=path+".test-tmp";File.WriteAllText(replacement,JsonSerializer.Serialize(editing));File.Move(replacement,path,true);
                        await Task.Delay(500);
                        using(var focus=new FocusHotkey(()=>{SetForegroundWindow(window.Handle);window.Activate();window.Focus();}))
                        {
                            Inject(0x11,0x10,0x12,0x78);
                            await Until(()=>ForegroundPid()==Environment.ProcessId,"Experience receives focus for configuration editing",5000);
                        }
                        await Task.Delay(1500);
                        var fullscreenSurvives=IsWindowVisible(fullscreen);
                        var afterFocus=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(run,"graphics-summary.json"))).GetProperty("fullscreenPresented").GetInt32();
                        Console.WriteLine("OBSERVATION G-Force fullscreen remains visible after Experience receives editing focus: "+fullscreenSurvives);
                        Console.WriteLine("OBSERVATION Successful G-Force fullscreen presents advance while Experience has focus: "+(afterFocus>beforeFocus));
                        File.WriteAllText(Path.Combine(run,"focus-observation.json"),JsonSerializer.Serialize(new{FullscreenSurvivesExperienceFocus=fullscreenSurvives,FullscreenFramesAdvanceAfterExperienceFocus=afterFocus>beforeFocus,SuccessfulFullscreenPresentsBeforeFocus=beforeFocus,SuccessfulFullscreenPresentsAfterFocus=afterFocus,ForegroundProcessId=ForegroundPid(),GForceProcessId=pid}));
                        var summary=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(run,"graphics-summary.json")));
                        Console.WriteLine("Graphics: "+summary.GetRawText());
                        Check(summary.GetProperty("d3d9Presents").GetInt32()>50,"G-Force continues submitting Direct3D 9 frames behind Experience");
                    }
                    File.WriteAllText(Path.Combine(run,"stop"),"");await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    File.WriteAllText(Path.Combine(run,"probe-console.txt"),await output);File.WriteAllText(Path.Combine(run,"probe-errors.txt"),await errors);
                    Check(probe.ExitCode==0,"Graphics probe closes G-Force and restores preferences");Console.WriteLine("Fullscreen evidence: "+run);
                    Bitmap Capture(string name){var image=new Bitmap(bounds.Width,bounds.Height);using var graphics=Graphics.FromImage(image);graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);image.Save(Path.Combine(run,name),ImageFormat.Png);return image;}
                }
                catch(Exception ex){failure=ex;}
                finally
                {
                    File.WriteAllText(Path.Combine(run,"stop"),"");
                    try
                    {
                        if(probe is not null&&!probe.HasExited)try{await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}catch(TimeoutException)
                        {
                            probe.Kill(true);await probe.WaitForExitAsync();
                            var backup=Path.Combine(run,"standalone-preferences.backup");
                            if(File.Exists(backup))File.Copy(backup,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"SoundSpectrum","G-Force","Preferences (Standalone).txt"),true);
                            failure??=new Exception("Graphics probe required forced cleanup.");
                        }
                        if(output is not null)File.WriteAllText(Path.Combine(run,"probe-console.txt"),await output);
                        if(errors is not null)File.WriteAllText(Path.Combine(run,"probe-errors.txt"),await errors);
                    }
                    catch(Exception ex){failure??=ex;}
                    finally{probe?.Dispose();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
                    if(failure is not null)done.SetException(failure);else done.SetResult();
                }
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(75));}
        finally{if(!thread.Join(TimeSpan.FromSeconds(10)))throw new Exception("The fullscreen test dispatcher did not finish shutting down.");}
    }
    internal static async Task FocusForTest(nint window)
    {
        using var focus = new FocusHotkey(() => SetForegroundWindow(window));
        Inject(0x11, 0x10, 0x12, 0x78);
        await Until(() => ForegroundPid() == Environment.ProcessId, "Test hotkey grants Experience foreground focus", 5000);
    }
    private static void Check(bool yes,string text){if(!yes)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private static async Task Until(Func<bool> ready,string text,int milliseconds){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){if(ready()){Check(true,text);return;}await Task.Delay(30);}throw new TimeoutException(text);}
    private static async Task UntilAsync(Func<Task<bool>> ready,string text,int milliseconds){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){if(await ready()){Check(true,text);return;}await Task.Delay(30);}throw new TimeoutException(text);}
    private static List<nint> Windows(int pid){var result=new List<nint>();EnumWindows((h,l)=>{GetWindowThreadProcessId(h,out var p);if(p==pid)result.Add(h);return true;},0);return result;}
    private static string Class(nint h){var name=new StringBuilder(128);GetClassName(h,name,128);return name.ToString();}
    private static int ForegroundPid(){GetWindowThreadProcessId(GetForegroundWindow(),out var p);return (int)p;}
    private static bool Above(nint upper,nint lower){for(var h=GetWindow(upper,2);h!=0;h=GetWindow(h,2))if(h==lower)return true;return false;}
    private delegate bool Callback(nint h,nint l);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback cb,nint l);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint h,out uint p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint h,StringBuilder name,int length);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint h,uint relation);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public Union Data;}
    [StructLayout(LayoutKind.Explicit)]private struct Union{[FieldOffset(0)]public Keyboard Key;[FieldOffset(0)]public Mouse Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public ushort Vk,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Mouse{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")]private static extern uint SendInput(uint count,Input[] inputs,int size);
    private static void Inject(params ushort[] keys){var input=keys.Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k}}}).Concat(keys.Reverse().Select(k=>new Input{Type=1,Data=new(){Key=new(){Vk=k,Flags=2}}})).ToArray();if(SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=input.Length)throw new Exception("Fullscreen shortcut injection failed.");}
    private sealed class FocusHotkey : Forms.NativeWindow,IDisposable
    {
        private readonly Action _focus;
        public FocusHotkey(Action focus){_focus=focus;CreateHandle(new Forms.CreateParams{Parent=(nint)(-3)});if(!RegisterHotKey(Handle,0x6f09,0x4007,0x78)){DestroyHandle();throw new Exception("Temporary foreground test hotkey is unavailable.");}}
        protected override void WndProc(ref Forms.Message message){if(message.Msg==0x312&&message.WParam==0x6f09){_focus();return;}base.WndProc(ref message);}
        public void Dispose(){UnregisterHotKey(Handle,0x6f09);DestroyHandle();}
        [DllImport("user32.dll")]private static extern bool RegisterHotKey(nint h,int id,uint modifiers,uint key);
        [DllImport("user32.dll")]private static extern bool UnregisterHotKey(nint h,int id);
    }
}
