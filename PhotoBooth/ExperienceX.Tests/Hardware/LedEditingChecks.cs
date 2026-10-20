using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms=System.Windows.Forms;

internal static class LedEditingChecks
{
    internal static Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>
            {
                string root=Path.Combine(Path.GetTempPath(),"LedEditing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
                try
                {
                    var display=Forms.Screen.PrimaryScreen!.DeviceName;
                    var device=WledClient.Parse("127.0.0.1:1",JsonDocument.Parse(LedChecks.DeviceJson).RootElement);
                    var array=new PerspectiveDefinition{Name="Stairs",Type="pixelArray",Led=device.Perspective(device.Segments[0],false)};
                    var matrix=new PerspectiveDefinition{Name="Ceiling",Type="pixelMatrix",Led=device.Perspective(device.Segments[1],true)};
                    var definitions=new ControllerSharedDefinitions{ControllerId=Guid.NewGuid().ToString("N"),ControllerName="Test controller",Hostname=Environment.MachineName,Perspectives=[array,matrix]};
                    var options=new ExperienceOptions{DeveloperMode=true,ConfigurationMode=true,LocalController=new(){Enabled=false},UdpCommandsPort=FreePort(),UdpEventsPort=FreePort(),ControllerDefinitions=new(){[definitions.ControllerId]=definitions},DisplayScreens=Forms.Screen.AllScreens.ToDictionary(s=>s.DeviceName,_=>new List<ScreenOptions>()),DisplayBackgrounds=Forms.Screen.AllScreens.ToDictionary(s=>s.DeviceName,_=>new DisplayBackgroundOptions{IsTransparent=true}),DefaultDisplayName=display};
                    string path=Path.Combine(root,"appsettings.json");File.WriteAllText(path,JsonSerializer.Serialize(options));
                    await using var host=new ExperienceHost(dispatcher,path,()=>true);host.Start();
                    ExperienceOptions Read() { using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); using var reader=new StreamReader(stream);return ExperienceOptions.Parse(reader.ReadToEnd()); }
                    var window=host.DisplayWindows.Single(w=>w.DisplayName==display);
                    window.Activate();window.Focus();await Task.Delay(150);
                    void Add(int index)
                    {
                        using var accept=new Forms.Timer{Interval=20};
                        accept.Tick+=(_,_)=>
                        {
                            var picker=Forms.Application.OpenForms.OfType<PerspectivePicker>().FirstOrDefault();if(picker is null)return;
                            var list=picker.Controls.OfType<Forms.ListBox>().Single();
                            if(list.Items.Count!=2)throw new Exception("LED picker included full or media perspectives.");
                            list.SelectedIndex=index;picker.DialogResult=Forms.DialogResult.OK;picker.Close();
                        };
                        accept.Start();
                        SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.AddId,(nint)(0x41<<16));
                        SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.LedId,(nint)(0x4C<<16));
                    }
                    Add(1);await Until(()=>Read().ScreensFor(display).Count==1 && host.SelectedScreen is not null);
                    var first=Read().ScreensFor(display)[0];Check(first.IsLedArray && first.Points.Vertices.Count==2,"A,L chooses a controller array and creates only two endpoints");
                    void Drag(System.Drawing.Point start,System.Drawing.Point end)
                    {SendMessage(window.Handle,0x201,1,Pack(start));SendMessage(window.Handle,0x200,1,Pack(end));SendMessage(window.Handle,0x202,0,Pack(end));}
                    Drag(new(0,300),new(80,240));await Until(()=>Read().PointsFor(display,first.Id).Vertices[0].X>.05);
                    Check(Read().PointsFor(display,first.Id).Vertices[1]==first.Points.Vertices[1],"Dragging an array endpoint retains its opposite endpoint");
                    await Task.Delay(200); // Wait for renderer confirmation, beyond the atomic file replacement.
                    Drag(new(470,270),new(500,295));await Until(()=>Read().PointsFor(display,first.Id).Vertices[1].Y>.52);
                    var moved=Read().PointsFor(display,first.Id);Check(moved.Vertices.Count==2,"Array body dragging moves and saves the line");
                    await Task.Delay(200);
                    Add(0);await Until(()=>Read().ScreensFor(display).Count==2 && host.SelectedScreen?.ScreenId!=first.Id);
                    var second=Read().ScreensFor(display)[1];Check(!second.IsLedArray && second.IsLed && second.Points.TopLeft==new ScreenPoint(.25,.25),"New matrix occupies centered fifty-percent area");
                    System.Drawing.Point Pixel(ScreenPoint p)=>new((int)Math.Round(p.X*(window.ClientSize.Width-1)),(int)Math.Round(p.Y*(window.ClientSize.Height-1)));
                    var br=Pixel(second.Points.BottomRight);Drag(br,new(br.X+20,br.Y+10));await Until(()=>Read().PointsFor(display,second.Id).BottomRight.X>.76);
                    var sized=Read().PointsFor(display,second.Id);LedGeometry.Validate(sized,false);Check(sized.TopLeft==second.Points.TopLeft,"Matrix resizing retains the opposite corner");
                    await Task.Delay(200);
                    var handle=LedGeometry.RotationHandle(sized,window.ClientSize.Width,window.ClientSize.Height);
                    int hx=(int)Math.Round(handle.X*(window.ClientSize.Width-1)),hy=(int)Math.Round(handle.Y*(window.ClientSize.Height-1));
                    Drag(new(hx,hy),new(hx+75,hy+15));await Until(()=>Read().PointsFor(display,second.Id)!=sized);
                    var rotated=Read().PointsFor(display,second.Id);LedGeometry.Validate(rotated,false);Check(rotated.TopLeft.Y!=rotated.TopRight.Y,"Separate rotation handle rotates the rectangle and saves immediately");
                    await Task.Delay(200);
                    SendMessage(window.Handle,0x100,0x5A,0);await Until(()=>Read().PointsFor(display,second.Id)==sized);
                    Check(true,"Z restores the previous matrix geometry");
                    await Task.Delay(200);
                    // Body selection removes point focus so Delete removes the screen.
                    var body=Pixel(new(.35,.65));Drag(body,new(body.X+10,body.Y+5));await Until(()=>Read().PointsFor(display,second.Id)!=sized);
                    await Task.Delay(200);
                    SendMessage(window.Handle,0x100,0x2E,0);await Until(()=>Read().ScreensFor(display).Count==1);
                    Check(Read().ScreensFor(display)[0].Id==first.Id,"Delete removes selected LED screen without deleting another screen");
                    await Task.Delay(80);
                }
                catch(Exception ex){done.TrySetException(ex);}
                finally {if(!done.Task.IsCompleted)done.SetResult();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private static nint Pack(System.Drawing.Point point)=>(nint)((point.Y<<16)|(point.X&0xFFFF));
    private static int FreePort(){using var udp=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0));return ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;}
    private static async Task Until(Func<bool> ready){var clock=Stopwatch.StartNew();while(true){try{if(ready())return;}catch(IOException){}if(clock.ElapsedMilliseconds>8000)throw new TimeoutException("LED screen editing state did not arrive.");await Task.Delay(20);}}
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint hwnd,uint message,nint wParam,nint lParam);
}
