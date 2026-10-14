using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ExperienceX;
using Forms=System.Windows.Forms;
internal static class PerspectiveCreationHardwareChecks
{
    public static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>
            {
                var workspace=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../.."));
                var root=Path.Combine(workspace,".tools","perspective-create-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
                Process? controller=null;
                try
                {
                    int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
                    int commands=Port(),events=Port();var controllerId=Guid.NewGuid().ToString("N");
                    var cBuild=Path.Combine(workspace,"Controller","bin","Release","net9.0-windows");
                    var c=JsonNode.Parse(File.ReadAllText(Path.Combine(cBuild,"appsettings.defaults.json")))!.AsObject();
                    c["DeviceName"]="crop-controller-test";c["ControllerId"]=controllerId;c["UdpCommandsPort"]=commands;c["UdpEventsPort"]=events;c["ConfigurationMode"]=false;c["DefinitionsPort"]=54244;
                    c["Perspectives"]=new JsonArray();c["Arduinos"]=new JsonArray();c["Triggers"]=new JsonArray();
                    c["Scenes"]=JsonSerializer.SerializeToNode(new[]{new{Name="test",AmbientPlaylist="empty"}});
                    c["Playlists"]=JsonSerializer.SerializeToNode(new[]{new{Name="empty",Type="ambient",Items=Array.Empty<object>()}});
                    var cPath=Path.Combine(root,"controller.json");File.WriteAllText(cPath,c.ToJsonString());
                    var launch=new ProcessStartInfo(Path.Combine(cBuild,"Controller.exe")){UseShellExecute=false,WorkingDirectory=cBuild};launch.ArgumentList.Add("--configuration");launch.ArgumentList.Add(cPath);controller=Process.Start(launch)!;
                    var display=Forms.Screen.PrimaryScreen!;var width=.5*display.Bounds.Height/display.Bounds.Width;
                    var points=new PointOptions{Enabled=true,TopLeft=new(.5-width/2,.25),TopRight=new(.5+width/2,.25),BottomLeft=new(.5-width/2,.75),BottomRight=new(.5+width/2,.75)};
                    var media=Path.Combine(root,"Media");Directory.CreateDirectory(media);
                    var options=new ExperienceOptions{DeviceName="crop-experience-test",DeveloperMode=true,ConfigurationMode=true,MediaDiscovery=new(){Enabled=false},VideoDirectory=media,AudioDirectory=media,UdpCommandsPort=commands,UdpEventsPort=events,UdpSendAttempts=3,
                        DisplayBackgrounds=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,_=>new DisplayBackgroundOptions{IsTransparent=true}),
                        DisplayScreens=Forms.Screen.AllScreens.ToDictionary(d=>d.DeviceName,d=>d.Primary ? new List<ScreenOptions>{new(){Id="shape",Name="Shape",Points=points}} : [])};
                    var xPath=Path.Combine(root,"experience.json");File.WriteAllText(xPath,JsonSerializer.Serialize(options));
                    await using(var host=new ExperienceHost(dispatcher,xPath,()=>true))
                    {
                        host.Start();var window=host.DisplayWindows.Single(w=>w.DisplayName==display.DeviceName);
                        await Until(()=>Read().ControllerDefinitions.ContainsKey(controllerId),"Experience receives real Controller definitions");
                        var x=(int)(window.ClientSize.Width*points.TopLeft.X);var y=(int)(window.ClientSize.Height*points.TopLeft.Y);var position=(nint)((y<<16)|x);
                        SendMessage(window.Handle,0x201,1,position);SendMessage(window.Handle,0x202,0,position);
                        await Until(()=>host.SelectedScreen?.ScreenId=="shape","Native selection chooses the editable screen");await Task.Delay(250);
                        void F()=>SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.CreatePerspectiveId,(nint)(0x46<<16));
                        F();await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective is not null,"F saves through UDP/Controller configuration/HTTP definitions and selects the perspective");
                        var first=Read().ScreensFor(display.DeviceName).Single();var expectedWidth=100.0*display.Bounds.Height/display.Bounds.Width;
                        Check(first.Points==points && first.Perspective!.Name=="Shape" && Math.Abs(first.Perspective.Rectangle.Width-expectedWidth)<1e-6 && first.Perspective.Rectangle.Height==100,"No media loaded: F creates the expected dimensions without moving points");
                        await Task.Delay(500); // First save/select operation must finish before a subsequent press.
                        F();await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective?.PerspectiveId!=first.Perspective!.PerspectiveId,"Second F creates a new perspective on the owning Controller");
                        var second=Read().ScreensFor(display.DeviceName).Single();Check(second.Perspective!.Name=="Shape-2","Controller gives repeated names a unique suffix");
                        var defs=Read().ControllerDefinitions[controllerId];Check(defs.Perspectives.Length==2,"Controller persisted exactly two creations despite UDP retries");
                        using(var picker=new PerspectivePicker(defs.Perspectives.Select(defs.Select).ToArray()))
                        using(var accept=new Forms.Timer{Interval=1500})
                        {
                            accept.Tick+=(_,_)=>{try{Check(picker.TopMost && ConfigurationDialog.Active && (GetWindowLongPtr(picker.Handle,-20).ToInt64()&8)!=0,"Perspective dialog stays topmost while display guards yield");picker.DialogResult=Forms.DialogResult.OK;}catch(Exception ex){done.TrySetException(ex);picker.Close();}};
                            accept.Start();picker.ShowDialog(window);
                        }
                        Check(!ConfigurationDialog.Active,"Closing the picker resumes normal display topmost enforcement");
                        // Controller position edits propagate without changing destination points.
                        c=JsonNode.Parse(File.ReadAllText(cPath))!.AsObject();var entry=c["Perspectives"]!.AsArray().Select(n=>n!.AsObject()).Single(n=>n["Id"]!.GetValue<string>()==second.Perspective.PerspectiveId);entry["Rectangle"]!["X"]=0;
                        var temporary=cPath+".test";File.WriteAllText(temporary,c.ToJsonString());File.Move(temporary,cPath,true);
                        await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective?.Rectangle.X==0,"Controller-only position edits update the selected Experience perspective");
                        Check(Read().ScreensFor(display.DeviceName).Single().Points==points,"Controller crop positioning preserves destination geometry");
                        ExperienceOptions Read(){using var file=new FileStream(xPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return ExperienceOptions.Parse(reader.ReadToEnd());}
                    }
                    Console.WriteLine("Perspective creation evidence: "+root);done.TrySetResult();
                }
                catch(Exception ex){done.TrySetException(ex);}
                finally
                {
                    if(controller is not null){if(!controller.HasExited){controller.CloseMainWindow();try{await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}catch(TimeoutException){controller.Kill();await controller.WaitForExitAsync();}}controller.Dispose();}
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(70));}finally{await Task.Run(()=>thread.Join(10000));}
    }
    private static void Check(bool valid,string text){if(!valid)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private static async Task Until(Func<bool> ready,string text){var deadline=Environment.TickCount64+25000;while(true){try{if(ready())break;}catch(IOException){}if(Environment.TickCount64>deadline)throw new TimeoutException(text);await Task.Delay(40);}Check(true,text);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint h,uint m,nint w,nint l);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint h,int index);
}
