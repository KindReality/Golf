using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms=System.Windows.Forms;

internal static class ScreenCopyHardwareChecks
{
    internal static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                Exception? failure=null;ExperienceHost? host=null;Forms.IDataObject? previousClipboard=null;
                var folder=Path.Combine(Path.GetTempPath(),"ExperienceX-copy-ui-"+Guid.NewGuid().ToString("N"));
                try
                {
                    previousClipboard=Forms.Clipboard.GetDataObject();Directory.CreateDirectory(folder);
                    var media=Path.Combine(folder,"Media");Directory.CreateDirectory(media);
                    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Home Technologies","Controller","Media","Eye.mp4"),Path.Combine(media,"Eye.mp4"));
                    int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
                    var display=Forms.Screen.PrimaryScreen!.DeviceName;
                    var original=new ScreenOptions{Id="video",Name="Eye",DefaultName="screen1",Roles="wall",Brightness=75,Framing=new(){Zoom=2},BlackKey=new(){Enabled=false}};
                    var mat=new ScreenOptions{Id="mat",Name="mask",Type="mat",IsNegative=true,Points=new(){Enabled=true,Vertices=[new(.65,.2),new(.9,.2),new(.9,.7),new(.75,.6),new(.65,.7)]}};
                    var options=new ExperienceOptions{ConfigurationMode=true,CalibrationGridEnabled=false,TransitionInSeconds=.05,MediaDiscovery=new(){Enabled=false},
                        VideoDirectory=media,DefaultDisplayName=display,UdpCommandsPort=Port(),UdpEventsPort=Port(),
                        DisplayScreens=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,d=>d.Primary?new List<ScreenOptions>{original,mat}:[])};
                    var path=Path.Combine(folder,"appsettings.json");File.WriteAllText(path,JsonSerializer.Serialize(options));var store=new PointConfigurationStore(path);
                    ExperienceOptions Read(){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);return ExperienceOptions.Parse(reader.ReadToEnd());}
                    host=new(dispatcher,path);host.Start();var window=host.DisplayWindows.Single(w=>w.DisplayName==display);
                    await GForceFullscreenChecks.FocusForTest(window.Handle);window.Activate();window.Focus();await Task.Delay(150);
                    Forms.Cursor.Position=window.PointToScreen(new(window.ClientSize.Width/3,window.ClientSize.Height/3));
                    nint Position(double x,double y)=>(nint)(((int)Math.Round(y*(window.ClientSize.Height-1))<<16)|(int)Math.Round(x*(window.ClientSize.Width-1)));
                    void Click(double x,double y){SendMessage(window.Handle,0x201,1,Position(x,y));SendMessage(window.Handle,0x202,0,Position(x,y));}
                    void ClipboardKey(bool paste)=>SendMessage(host.HotkeyWindowHandle,0x312,paste?ApplicationHotkeys.PasteScreenId:ApplicationHotkeys.CopyScreenId,(nint)(((paste?0x56:0x43)<<16)|2));
                    Click(.3,.3);Check(host.SelectedScreen?.ScreenId==original.Id,"Video selected for copying");
                    await UntilAsync(async()=>(await window.Renderer!.SnapshotAsync()).Single(s=>s.Id==original.Id).Frames>5,"Source video playing");
                    var sourceBefore=(await window.Renderer!.SnapshotAsync()).Single(s=>s.Id==original.Id);
                    ClipboardKey(false);ClipboardKey(true);ClipboardKey(true);
                    await Until(()=>Read().ScreensFor(display).Count==4&&host.SelectedScreen?.ScreenName=="Eye-3","Rapid Ctrl+C,V,V creates and selects two independent copies");
                    var clones=Read().ScreensFor(display).Where(s=>s.Id!=original.Id&&s.IsVideo).ToArray();
                    Check(clones.All(s=>s.MediaFile=="Eye.mp4"&&s.Points==original.Points&&s.Roles=="wall"&&s.Brightness==75&&s.Framing==original.Framing&&s.BlackKey==original.BlackKey),"Pasted video retains binding and presentation settings");
                    await UntilAsync(async()=>(await window.Renderer.SnapshotAsync()).Where(s=>clones.Any(c=>c.Id==s.Id)).All(s=>s.Frames>5),"Both copied screens independently play the same video");
                    var sourceAfter=(await window.Renderer.SnapshotAsync()).Single(s=>s.Id==original.Id);
                    Check(sourceAfter.RequestId==sourceBefore.RequestId&&sourceAfter.Frames>=sourceBefore.Frames,"Copy/paste does not restart the source video");
                    SendMessage(window.Handle,0x201,1,Position(0,0));var moving=host.SelectedScreen!.ScreenId;
                    SendMessage(window.Handle,0x200,1,Position(.01,.01));SendMessage(window.Handle,0x202,0,Position(.01,.01));
                    await Until(()=>Read().PointsFor(display,moving).TopLeft.X>0,"Initial corner can move independently after duplication");
                    Check(Read().ScreensFor(display).Where(s=>s.IsVideo&&s.Id!=moving).All(s=>s.Points==new PointOptions()),"Moving one initial corner leaves coincident original/copy corners unchanged");
                    Click(.65,.2);Check(host.SelectedScreen?.ScreenId==mat.Id,"Mat selected for copying");ClipboardKey(false);ClipboardKey(true);
                    await Until(()=>Read().ScreensFor(display).Count==5&&host.SelectedScreen?.ScreenName=="mask-2","Mat copy selected automatically");
                    var pastedMat=Read().ScreensFor(display).Single(s=>s.Name=="mask-2");Check(pastedMat.IsNegative&&pastedMat.Points==mat.Points,"Mat copy preserves all five points and negative mode");
                    SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.NameId,(nint)(0x4e<<16));
                    var editor=window.Controls.OfType<ScreenNameEditor>().Single();editor.Text="Clipboard text";editor.SelectAll();
                    ClipboardKey(false);ClipboardKey(true);SendMessage(editor.Handle,0x301,0,0);
                    Check(Forms.Clipboard.GetText()=="Clipboard text","Name editing retains normal text copy instead of screen copy");
                    Check(Read().ScreensFor(display).Count==5,"Name editing ignores queued screen paste");SendMessage(editor.Handle,0x100,27,0);await Task.Delay(150);
                    ClipboardKey(true);await Task.Delay(150);Check(Read().ScreensFor(display).Count==5,"Ordinary text clipboard cannot create a screen");
                    await store.SetConfigurationModeAsync(false);await Task.Delay(250);ClipboardKey(false);ClipboardKey(true);await Task.Delay(150);
                    Check(Read().ScreensFor(display).Count==5&&Forms.Clipboard.GetText()=="Clipboard text","Production mode releases clipboard shortcuts");
                    Console.WriteLine("PASS actual clipboard, rapid hotkeys, independent video playback, mat duplication, initial-corner mouse drag, normal text editing and production guards.");
                }
                catch(Exception ex){failure=ex;}
                finally
                {
                    try{if(host is not null)await host.DisposeAsync();if(previousClipboard is not null)Forms.Clipboard.SetDataObject(previousClipboard,true);else Forms.Clipboard.Clear();}
                    catch(Exception ex){failure??=ex;}
                    Console.WriteLine("Copy evidence: "+folder);dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    if(failure is null)done.TrySetResult();else done.TrySetException(failure);
                }
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(45));}finally{if(!thread.Join(TimeSpan.FromSeconds(5)))throw new Exception("Copy/paste test dispatcher did not stop.");}
    }
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private static Task Until(Func<bool> ready,string text)=>UntilAsync(()=>Task.FromResult(ready()),text);
    private static async Task UntilAsync(Func<Task<bool>> ready,string text){var end=DateTime.UtcNow.AddSeconds(8);while(DateTime.UtcNow<end){try{if(await ready()){Console.WriteLine("PASS "+text);return;}}catch(IOException){}await Task.Delay(25);}throw new TimeoutException(text);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nint w,nint l);
}
