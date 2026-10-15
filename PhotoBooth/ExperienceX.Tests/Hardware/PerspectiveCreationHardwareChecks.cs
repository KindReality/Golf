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
                var previousNetwork=Environment.GetEnvironmentVariable(PhotoBooth.Networking.LocalTestNetwork.Variable);
                Environment.SetEnvironmentVariable(PhotoBooth.Networking.LocalTestNetwork.Variable,"127.0.0.3");
                try
                {
                    if(Process.GetProcessesByName("Controller").Any() || Process.GetProcessesByName("ExperienceX").Any()) throw new InvalidOperationException("Stop the local applications before this test.");
                    int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
                    int commands=Port(),events=Port();var controllerId=Guid.NewGuid().ToString("N");
                    var cBuild=Path.Combine(workspace,"Controller","bin","Release","net9.0-windows");
                    var c=JsonNode.Parse(File.ReadAllText(Path.Combine(cBuild,"appsettings.defaults.json")))!.AsObject();
                    c["DeviceName"]="crop-controller-test";c["ControllerId"]=controllerId;c["UdpCommandsPort"]=commands;c["UdpEventsPort"]=events;c["ConfigurationMode"]=false;c["DefinitionsPort"]=54244;
                    c["ExperienceSupervisionEnabled"]=false;c["Telemetry"]=JsonSerializer.SerializeToNode(new{Directory=Path.Combine(root,"logs")});
                    c["Perspectives"]=new JsonArray();c["Arduinos"]=new JsonArray();c["Triggers"]=new JsonArray();
                    c["Scenes"]=JsonSerializer.SerializeToNode(new[]{new{Name="test",AmbientPlaylist="empty"}});
                    c["Playlists"]=JsonSerializer.SerializeToNode(new[]{new{Name="empty",Type="ambient",Items=Array.Empty<object>()}});
                    var cPath=Path.Combine(root,"controller.json");File.WriteAllText(cPath,c.ToJsonString());
                    var launch=new ProcessStartInfo(Path.Combine(cBuild,"Controller.exe")){UseShellExecute=false,WorkingDirectory=cBuild};launch.Environment[PhotoBooth.Networking.LocalTestNetwork.Variable]="127.0.0.2";launch.ArgumentList.Add("--configuration");launch.ArgumentList.Add(cPath);controller=Process.Start(launch)!;
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
                        async Task PressP(Action<PerspectivePicker>? choose=null, bool cancel=false)
                        {
                            var seen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var answer=new Forms.Timer{Interval=80};
                            answer.Tick+=(_,_)=>
                            {
                                var picker=Forms.Application.OpenForms.OfType<PerspectivePicker>().FirstOrDefault();
                                if(picker is null || !picker.Visible)return;
                                answer.Stop();
                                try
                                {
                                    Check(picker.CreateNew && picker.Selection is null && picker.AcceptButton is Forms.Button {Text:"Create perspective"},"P defaults to creating from the current screen, even with an existing perspective");
                                    Check(picker.Controls.OfType<Forms.ListBox>().Single().Items[0]!.ToString()!.Contains("100%"),"Default creation choice shows crop dimensions");
                                    choose?.Invoke(picker);
                                    (cancel?picker.CancelButton:picker.AcceptButton)!.PerformClick();
                                    seen.TrySetResult();
                                }
                                catch(Exception ex){picker.DialogResult=Forms.DialogResult.Cancel;seen.TrySetException(ex);}
                            };
                            answer.Start();SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.PerspectiveId,(nint)(0x50<<16));
                            await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        await PressP();await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective is not null,"P creates through UDP/Controller configuration/HTTP definitions and selects the perspective");
                        var first=Read().ScreensFor(display.DeviceName).Single();var expectedWidth=100.0*display.Bounds.Height/display.Bounds.Width;
                        Check(first.Points==points && first.Perspective!.Name=="Shape" && Math.Abs(first.Perspective.Rectangle.Width-expectedWidth)<1e-6 && first.Perspective.Rectangle.Height==100,"Without media loaded: creation uses screen geometry without moving points");
                        await Task.Delay(500);
                        await PressP();await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective?.PerspectiveId!=first.Perspective!.PerspectiveId,"Confirming the default again creates a new perspective on the owning Controller");
                        var second=Read().ScreensFor(display.DeviceName).Single();Check(second.Perspective!.Name=="Shape-2","Controller gives repeated names a unique suffix");
                        await Task.Delay(500);
                        await PressP(cancel:true);await Task.Delay(300);
                        Check(Read().ScreensFor(display.DeviceName).Single().Perspective==second.Perspective,"Cancel preserves the current perspective");
                        void ChooseExisting(PerspectivePicker picker)
                        {
                            var list=picker.Controls.OfType<Forms.ListBox>().Single();
                            list.SelectedIndex=Enumerable.Range(0,list.Items.Count).Single(i=>list.Items[i] is PerspectiveChoices.Choice c && c.Value?.PerspectiveId==first.Perspective!.PerspectiveId);
                            Check(!picker.CreateNew && picker.AcceptButton is Forms.Button {Text:"Use perspective"},"Choosing an existing perspective changes the action to Use");
                        }
                        await PressP(ChooseExisting);await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective?.PerspectiveId==first.Perspective!.PerspectiveId,"P applies an existing perspective without creating another");
                        await PressP(picker=>picker.Controls.OfType<Forms.ListBox>().Single().SelectedIndex=1);
                        await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective is null,"Full perspective remains available and clears the crop");
                        await PressP(picker=>
                        {
                            var list=picker.Controls.OfType<Forms.ListBox>().Single();
                            list.SelectedIndex=Enumerable.Range(0,list.Items.Count).Single(i=>list.Items[i] is PerspectiveChoices.Choice c && c.Value?.PerspectiveId==second.Perspective!.PerspectiveId);
                        });
                        await Until(()=>Read().ScreensFor(display.DeviceName).Single().Perspective?.PerspectiveId==second.Perspective!.PerspectiveId,"Existing perspective can be restored after choosing Full");
                        var defs=Read().ControllerDefinitions[controllerId];Check(defs.Perspectives.Length==2,"Cancel and selecting existing/Full do not create extra perspectives");
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
                    if(controller is not null){if(!controller.HasExited){PhotoBooth.Windows.ConfigurationShortcut.RequestClose("Controller");try{await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}catch(TimeoutException){controller.Kill();await controller.WaitForExitAsync();}}controller.Dispose();}
                    Environment.SetEnvironmentVariable(PhotoBooth.Networking.LocalTestNetwork.Variable,previousNetwork);
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
