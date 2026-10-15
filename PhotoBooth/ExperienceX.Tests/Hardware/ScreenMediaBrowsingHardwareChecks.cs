using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms=System.Windows.Forms;

internal static class ScreenMediaBrowsingHardwareChecks
{
    internal static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                var folder=Path.Combine(Path.GetTempPath(),"ExperienceX-folder-ui-"+Guid.NewGuid().ToString("N"));
                ExperienceHost? host=null;Exception? failure=null;
                try
                {
                    var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../.."));
                    var fixture=Environment.GetEnvironmentVariable("EXPERIENCEX_BOUND_TEST_VIDEO")??Path.Combine(root,".tools","screen-media-fixture.mp4");
                    var media=Path.Combine(folder,"Media");
                    Directory.CreateDirectory(Path.Combine(media,"Ghosts","Deep"));Directory.CreateDirectory(Path.Combine(media,"Witches"));
                    File.Copy(fixture,Path.Combine(media,"Ghosts","A.mp4"));File.Copy(fixture,Path.Combine(media,"Witches","A.mp4"));
                    using(var image=new Bitmap(16,16)){using(var g=Graphics.FromImage(image))g.Clear(Color.Red);image.Save(Path.Combine(media,"Ghosts","Deep","B.png"),ImageFormat.Png);}
                    int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
                    var display=Forms.Screen.PrimaryScreen!.DeviceName;
                    var screen=new ScreenOptions{Id="stage",Name="Stage"};
                    var options=new ExperienceOptions{DeveloperMode=true,ConfigurationMode=true,MediaDiscovery=new(){Enabled=false},VideoDirectory=media,
                        UdpCommandsPort=Port(),UdpEventsPort=Port(),TransitionInSeconds=.01,CalibrationGridEnabled=false,
                        DisplayScreens=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,d=>d.Primary?new List<ScreenOptions>{screen}:[])};
                    var config=Path.Combine(folder,"appsettings.json");File.WriteAllText(config,JsonSerializer.Serialize(options));
                    ScreenOptions Read(){using var file=new FileStream(config,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return ExperienceOptions.Parse(reader.ReadToEnd()).ScreensFor(display).Single(s=>s.Id==screen.Id);}
                    host=new(dispatcher,config,()=>true);host.Start();
                    var window=host.DisplayWindows.Single(w=>w.DisplayName==display);
                    async Task Until(Func<bool> ready,string message)
                    {
                        var deadline=DateTime.UtcNow.AddSeconds(8);
                        while(DateTime.UtcNow<deadline){try{if(ready()){Console.WriteLine("PASS "+message);return;}}catch(IOException){}await Task.Delay(25);}
                        throw new TimeoutException(message);
                    }
                    async Task<ScreenPlaybackSnapshot> Playing(string relative)
                    {
                        var deadline=DateTime.UtcNow.AddSeconds(12);
                        while(DateTime.UtcNow<deadline)
                        {
                            var state=(await window.Renderer!.SnapshotAsync()).Single(t=>t.Id==screen.Id);
                            if(state.NameLocked&&state.Frames>0&&state.Opacity==1&&state.BoundPath?.EndsWith(relative.Replace('/',Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)==true)return state;
                            await Task.Delay(25);
                        }
                        throw new TimeoutException("Media did not play: "+relative);
                    }
                    void Key(Forms.Keys key)=>SendMessage(window.Handle,0x100,(nint)key,0);
                    await Until(()=>window.State==RendererState.Idle,"Browser renderer ready");
                    int x=(int)(window.ClientSize.Width*.6),y=(int)(window.ClientSize.Height*.6);
                    nint Position(int px,int py)=>(nint)((ushort)px|((long)(ushort)py<<16));
                    SendMessage(window.Handle,0x201,1,Position(x,y));SendMessage(window.Handle,0x202,0,Position(x,y));
                    await Until(()=>host.SelectedScreen?.ScreenId==screen.Id,"Screen selected for folder browsing");
                    Key(Forms.Keys.Down);await Until(()=>window.BrowserLabel(screen.Id)=="Folder: Ghosts/","Down highlights a category folder");
                    Key(Forms.Keys.Right);Key(Forms.Keys.Down);Key(Forms.Keys.Right);Key(Forms.Keys.Down);
                    await Until(()=>Read().MediaFile=="Ghosts/Deep/B.png","Four-arrow navigation selects a nested image");
                    await Playing("Ghosts/Deep/B.png");
                    if(Read().Name!="Stage"||Read().Points!=screen.Points)throw new Exception("Browsing changed screen identity or geometry");
                    Key(Forms.Keys.Left);await Until(()=>window.BrowserLabel(screen.Id)=="Folder: Ghosts/Deep/","Left selects the parent folder entry");
                    if(Read().MediaFile!="Ghosts/Deep/B.png")throw new Exception("Folder selection changed current playback");
                    Key(Forms.Keys.Down);await Until(()=>Read().MediaFile=="Ghosts/A.mp4","Down selects video within a category");
                    var playing=await Playing("Ghosts/A.mp4");
                    Key(Forms.Keys.Left);Key(Forms.Keys.Down);await Until(()=>window.BrowserLabel(screen.Id)=="Folder: Witches/","Another category is selectable while video plays");
                    var unchanged=await Playing("Ghosts/A.mp4");if(unchanged.RequestId!=playing.RequestId)throw new Exception("Highlighting a folder restarted playback");
                    Key(Forms.Keys.Right);Key(Forms.Keys.Down);await Until(()=>Read().MediaFile=="Witches/A.mp4","Same filenames in different folders remain distinct");await Playing("Witches/A.mp4");
                    Key(Forms.Keys.Tab);var before=Read();Key(Forms.Keys.Down);Key(Forms.Keys.Enter);
                    await Until(()=>Read().Points!=before.Points&&!host.CalibrationBusy,"Arrows still move a selected point");
                    if(Read().MediaFile!="Witches/A.mp4")throw new Exception("Point movement browsed media");
                    SendMessage(window.Handle,0x100,(nint)Forms.Keys.N,0);
                    window.EditScreenName(window.Surface(screen.Id));var editor=window.Controls.OfType<ScreenNameEditor>().Single();
                    editor.Text="StageRenamed";SendMessage(editor.Handle,0x100,(nint)Forms.Keys.Left,0);SendMessage(editor.Handle,0x100,(nint)Forms.Keys.Escape,0);
                    if(Read().Name!="Stage"||Read().MediaFile!="Witches/A.mp4")throw new Exception("Name-editor arrows browsed media or canceled editing changed configuration");
                    var beforeRename=await Playing("Witches/A.mp4");
                    window.EditScreenName(window.Surface(screen.Id));editor=window.Controls.OfType<ScreenNameEditor>().Single();editor.Text="StageRenamed";
                    SendMessage(editor.Handle,0x100,(nint)Forms.Keys.Enter,0);
                    await Until(()=>Read().Name=="StageRenamed"&&Read().MediaFile=="Witches/A.mp4","Renaming the screen preserves independently selected media");
                    if((await Playing("Witches/A.mp4")).RequestId!=beforeRename.RequestId)throw new Exception("Renaming a screen restarted its selected media");
                    await host.DisposeAsync();host=null;
                    var restored=ExperienceOptions.Parse(File.ReadAllText(config)).ScreensFor(display).Single();
                    if(restored.MediaFile!="Witches/A.mp4"||restored.Name!="StageRenamed")throw new Exception("Shutdown lost nested media selection");
                    Console.WriteLine("PASS native category navigation, local image/video playback, stable screen names, retained playback during folder browsing, point/name editing guards and persisted selection.");
                }
                catch(Exception ex){failure=ex;}
                finally
                {
                    try{if(host is not null)await host.DisposeAsync();Directory.Delete(folder,true);}catch(Exception ex){failure??=ex;}
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    if(failure is null)done.TrySetResult();else done.TrySetException(failure);
                }
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
}
