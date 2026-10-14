using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms=System.Windows.Forms;

internal static class LedHardwareChecks
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
                try { await TestAsync(); done.SetResult(); }
                catch(Exception ex) {done.SetException(ex);}
                finally {dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start(); return done.Task;
    }
    private static async Task TestAsync()
    {
        var primary=Forms.Screen.PrimaryScreen!;
        using var source=new Forms.Form {Text="LED synthetic capture test",FormBorderStyle=Forms.FormBorderStyle.None,StartPosition=Forms.FormStartPosition.Manual,Bounds=new(primary.WorkingArea.X+50,primary.WorkingArea.Y+80,400,240),BackColor=Color.Red,TopMost=true,ShowInTaskbar=false};
        source.Show(); await Task.Delay(200); DwmFlush();
        using var capture=new LedCapture();
        var bounds=new Rectangle(source.PointToScreen(Point.Empty),source.ClientSize);
        capture.Capture(bounds);
        var rgb=capture.Sample(new PointOptions(),4,2);
        Check(rgb.Where((_,i)=>i%3==0).All(c=>c>245) && rgb.Where((_,i)=>i%3!=0).All(c=>c<10),"Jinx-compatible capture produces RGB24 from the desktop");
        using var overlay=new Forms.Form {Text="Excluded LED guides test",FormBorderStyle=Forms.FormBorderStyle.None,StartPosition=Forms.FormStartPosition.Manual,Bounds=source.Bounds,BackColor=Color.Blue,TopMost=true,ShowInTaskbar=false};
        Check(LedCalibrationWindow.SetWindowDisplayAffinity(overlay.Handle,0x11),"Windows accepts capture exclusion");
        overlay.Show(source);await Task.Delay(200);DwmFlush();capture.Capture(bounds);
        var excluded=capture.Sample(new PointOptions(),4,2);
        Check(excluded.SequenceEqual(rgb),"Excluded editing window is omitted rather than sampled or blacked out");
        overlay.Hide();
        // A source stripe affects several output channels in smooth mode, unlike point sampling.
        using(var graphics=source.CreateGraphics())
        { graphics.Clear(Color.Black); graphics.FillRectangle(Brushes.White,198,0,4,240); }
        DwmFlush(); capture.Capture(bounds); var smooth=capture.Sample(new PointOptions(),4,2);
        Check(smooth.Any(c=>c is >0 and <255),"HALFTONE combines source areas");
        using(var graphics=source.CreateGraphics()) graphics.Clear(Color.Red);
        DwmFlush();capture.Capture(bounds);
        var region=LedGeometry.Rotate(new PointOptions{Enabled=true,TopLeft=new(.25,.25),TopRight=new(.75,.25),BottomLeft=new(.25,.75),BottomRight=new(.75,.75)},.5,.1,.9,.5,400,240);
        var rotated=capture.Sample(region,4,2);
        Check(rotated.Where((_,i)=>i%3==0).All(c=>c>240),"Rotated source rectification retains the sampled colors");
        using(var graphics=source.CreateGraphics()) {graphics.Clear(Color.Red);graphics.FillRectangle(Brushes.Lime,200,0,200,240);}
        DwmFlush();capture.Capture(bounds);rotated=capture.Sample(region,4,2);
        Check(rotated.Take(12).Where((_,i)=>i%3==1).All(c=>c>225) && rotated.Skip(12).Where((_,i)=>i%3==0).All(c=>c>225),"Rotated sampling preserves source orientation rather than using its axis-aligned bounding box");
        using(var graphics=source.CreateGraphics())graphics.Clear(Color.Red);DwmFlush();

        await using var fake=new FakeWled();
        using var ddp=new UdpClient(new IPEndPoint(IPAddress.Loopback,DdpFrames.Port));
        using var wledClient=new WledClient();var device=await wledClient.ReadAsync(fake.Address);
        var controller=new ControllerSharedDefinitions{ControllerId=Guid.NewGuid().ToString("N"),ControllerName="LED test",Hostname=Environment.MachineName};
        var array=new PerspectiveDefinition{Name="Array",Type="pixelArray",Led=device.Perspective(device.Segments[0],false,30)};
        var matrix=new PerspectiveDefinition{Name="Matrix",Type="pixelMatrix",Led=device.Perspective(device.Segments[1],true,10)};
        var a=new ScreenOptions{Id="led-array",Type="fastled",Perspective=controller.Select(array),Points=LedGeometry.Initial(true)};
        var m=new ScreenOptions{Id="led-matrix",Type="fastled",Perspective=controller.Select(matrix),Points=LedGeometry.Initial(false)};
        await using(var output=new LedOutputManager())
        {
            output.Update([new("synthetic",bounds,a),new("synthetic",bounds,m)]);
            byte[] frame=await FrameAsync(ddp);
            foreach(int pixel in new[]{0,1,2,3,6,7}) Check(frame[10+pixel*3]>240 && frame[11+pixel*3]<10 && frame[12+pixel*3]<10,"Segment RGB lands at its global device coordinates");
            Check(frame.Skip(10+4*3).Take(6).All(c=>c==0),"Unused pixels remain black while streaming");
            await Task.Delay(350);
            Check(output.FramesSent>5 && output.Captures<=output.FramesSent+1,"Multiple screens share display captures and retain independent output rates");
            output.Update([new("synthetic",bounds,a)]);
            await Task.Delay(80);
            while(ddp.Available>0) await ddp.ReceiveAsync();
            frame=await FrameAsync(ddp);
            Check(frame.Skip(16).All(c=>c==0),"Removing a matrix clears its pixels while another segment continues");
            output.Update([]);await Task.Delay(80);while(ddp.Available>0) await ddp.ReceiveAsync();
            await Task.Delay(120);Check(ddp.Available==0,"Last LED screen removal ends streaming and permits WLED's timeout fallback");
        }
        source.Hide();
        var options=new ExperienceOptions{ConfigurationMode=true,DeveloperMode=true,MediaDiscovery=new(){Enabled=false},DisplayScreens=new(){[primary.DeviceName]=[a,m]},DisplayBackgrounds=new(){[primary.DeviceName]=new(){BackgroundColor="#FF0000",IsTransparent=false}}};
        var window=new MainWindow(primary.DeviceName,options);
        try
        {
            window.Start(); await UntilAsync(()=>window.State==RendererState.Idle && window.ToolsRenderer?.State==RendererState.Idle);
            window.Surface(m.Id).SelectScreen(true);await Task.Delay(150);
            capture.Capture(new(window.PointToScreen(Point.Empty),window.ClientSize));
            var content=capture.Sample(new PointOptions(),4,2);
            Check(content.Where((_,i)=>i%3==0).All(c=>c>240) && content.Where((_,i)=>i%3!=0).All(c=>c<10),"DirectX guides, array band and matrix handles do not contaminate capture");
            window.UpdateOptions(options with{ConfigurationMode=false});await Task.Delay(120);
            Check(window.ToolsRenderer is not null && !Forms.Application.OpenForms.OfType<LedCalibrationWindow>().Any(f=>f.Visible),"LED sampling screens are invisible outside configuration mode");
            window.UpdateOptions(options); await Task.Delay(150);
            capture.Capture(new(window.PointToScreen(Point.Empty),window.ClientSize));
            Check(capture.Sample(new PointOptions(),4,2).Where((_,i)=>i%3!=0).All(c=>c<10),"Re-entering configuration mode keeps editing guides excluded");
        }
        finally {await window.ShutdownWindowAsync();window.Dispose();}
        Console.WriteLine("Passed native smooth/rotated capture, guide exclusion, composed DDP output, per-screen rates, deletion and fallback lifecycle.");
    }
    private static async Task<byte[]> FrameAsync(UdpClient socket)
    {
        using var deadline=new CancellationTokenSource(10000);
        var packet=(await socket.ReceiveAsync(deadline.Token)).Buffer;
        Check(packet.Length==34 && packet[0]==0x41,"One RGB DDP frame contains the complete eight-pixel device"); return packet;
    }
    private static async Task UntilAsync(Func<bool> predicate)
    { var clock=Stopwatch.StartNew();while(!predicate()){if(clock.ElapsedMilliseconds>10000)throw new TimeoutException("Native LED renderer not ready.");await Task.Delay(20);} }
    private static void Check(bool condition,string text){if(!condition)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private sealed class FakeWled : IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _closed=new();
        private readonly Task _serve;
        public string Address {get;}
        public FakeWled(){_listener.Start();Address="127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port;_serve=ServeAsync();}
        private async Task ServeAsync()
        {
            try
            {
                while(!_closed.IsCancellationRequested)
                {
                    using var socket=await _listener.AcceptTcpClientAsync(_closed.Token);await using var stream=socket.GetStream();
                    using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    int total=0;string? line;string? request=await reader.ReadLineAsync(_closed.Token);
                    while((line=await reader.ReadLineAsync(_closed.Token)) is {Length:>0}){total+=line.Length;if(total>8192)throw new Exception("HTTP request too large.");}
                    var body=Encoding.UTF8.GetBytes(request?.Contains("/json/cfg")==true ? "{\"if\":{\"live\":{\"en\":true,\"mso\":false,\"rlm\":true,\"timeout\":25,\"offset\":0,\"dmx\":{\"addr\":1}}}}" : """
                        {"info":{"brand":"WLED","name":"Synthetic","mac":"aabbccddeeff","leds":{"count":8,"matrix":{"w":4,"h":2}}},"state":{"seg":[{"id":0,"start":0,"stop":2,"startY":0,"stopY":1},{"id":1,"start":2,"stop":4,"startY":0,"stopY":2}]}}
                        """);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),_closed.Token);
                    await stream.WriteAsync(body,_closed.Token);
                }
            }
            catch(OperationCanceledException) when(_closed.IsCancellationRequested){}
        }
        public async ValueTask DisposeAsync(){_closed.Cancel();_listener.Stop();await _serve;_closed.Dispose();}
    }
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();
}
