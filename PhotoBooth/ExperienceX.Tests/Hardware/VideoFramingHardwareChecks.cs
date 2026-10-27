using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class VideoFramingHardwareChecks
{
    internal static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                ExperienceHost? host = null;
                Exception? failure = null;
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-framing-ui-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    int Port() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var points = new PointOptions { Enabled = true, TopLeft = new(.1,.1), TopRight = new(.9,.1), BottomLeft = new(.1,.9), BottomRight = new(.9,.9) };
                    var screen = new ScreenOptions { Id = "video", Points = points };
                    var options = new ExperienceOptions { ConfigurationMode = true, CalibrationGridEnabled = false, LocalController = new() { Enabled = false },
                        UdpCommandsPort = Port(), UdpEventsPort = Port(), DefaultDisplayName = display,
                        DisplayScreens = Forms.Screen.AllScreens.ToDictionary(d => d.DeviceName, d => d.Primary ? new List<ScreenOptions> { screen } : []) };
                    var path = Path.Combine(folder,"appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options));
                    var store = new PointConfigurationStore(path);
                    ScreenOptions Read()
                    {
                        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        return ExperienceOptions.Parse(reader.ReadToEnd()).ScreensFor(display).Single();
                    }
                    host = new(dispatcher,path,()=>true); host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == display);
                    // Native messages and controlled modifier snapshots exercise our window without touching OS prompts.
                    window.Activate(); window.Focus();
                    await Until(() => window.State == RendererState.Idle, "Renderer ready");
                    int x = (int)(window.ClientSize.Width * .35), y = (int)(window.ClientSize.Height * .4);
                    nint Position(int px,int py) => (nint)((ushort)px | ((long)(ushort)py << 16));
                    void Wheel(int delta)
                    {
                        var p = window.PointToScreen(new(x,y));
                        SendMessage(window.Handle,0x20A,(nint)(delta << 16),Position(p.X,p.Y));
                    }
                    SendMessage(window.Handle,0x201,1,Position(x,y)); SendMessage(window.Handle,0x202,0,Position(x,y));
                    await Until(() => host.SelectedScreen?.ScreenId == screen.Id && !host.CalibrationBusy, "Video selected");
                    var video = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Home Technologies","Controller","Media","Eye.mp4");
                    window.PlayVideo(video,new() { Experience="video",Value="Eye.mp4",ScreenId=screen.Id,RequestId="framing-test",Loop=true,Mute=true });
                    await UntilAsync(async () => (await window.Renderer!.SnapshotAsync()).Single().Frames > 20,"Video playing");
                    var center = ScreenNameLabel.Center(points,window.ClientSize.Width,window.ClientSize.Height);
                    SendMessage(window.Handle,0x201,1,Position((int)center.X,(int)center.Y)); SendMessage(window.Handle,0x202,0,Position((int)center.X,(int)center.Y));
                    await Until(() => window.Controls.OfType<ScreenNameEditor>().Any(), "Single-click name editing waits for a possible double-click");
                    var nameEditor = window.Controls.OfType<ScreenNameEditor>().Single();
                    var nativeCenter = window.PointToScreen(new((int)center.X,(int)center.Y));
                    SendMessage(nameEditor.Handle,0x20A,(nint)(1200 << 16),Position(nativeCenter.X,nativeCenter.Y));
                    Check(!window.Controls.OfType<ScreenNameEditor>().Any(),"Scrolling after a center click stayed trapped in the unchanged name editor.");
                    Check(window.FramingEditor.Draft!.Zoom > 2 && Read().Framing.Zoom == 1,"Wheel did not preview before saving.");
                    await Until(() => Read().Framing.Zoom > 2 && !window.FramingEditor.Busy && window.ScreenDefinition(screen.Id)!.Framing == Read().Framing,"Wheel pause saves and reloads zoom");
                    var before = Read(); var frames = (await window.Renderer!.SnapshotAsync()).Single().Frames;
                    Forms.Cursor.Position=window.PointToScreen(new(x,y));
                    window.ZoomSelected(1);
                    Check(window.FramingEditor.Draft!.Zoom>before.Framing.Zoom,"Keyboard zoom-in did not preview.");
                    await Until(()=>Read().Framing.Zoom>before.Framing.Zoom&&!window.FramingEditor.Busy,"Keyboard zoom saves");
                    var enlarged=Read();window.ZoomSelected(-1);
                    await Until(()=>Read().Framing.Zoom<enlarged.Framing.Zoom&&!window.FramingEditor.Busy,"Keyboard zoom-out saves");
                    before=Read();
                    SendMessage(window.Handle,0x201,9,Position((int)center.X,(int)center.Y));
                    SendMessage(window.Handle,0x200,9,Position((int)center.X+25,(int)center.Y-80));
                    Check(window.FramingEditor.Zooming && window.FramingEditor.Draft!.Zoom>before.Framing.Zoom && Read().Framing==before.Framing,
                        "Ctrl+drag did not zoom upward or saved before release.");
                    SendMessage(window.Handle,0x202,8,Position((int)center.X+25,(int)center.Y-80));
                    await Until(()=>Read().Framing.Zoom>before.Framing.Zoom&&!window.FramingEditor.Busy,"Ctrl+drag saves zoom on release");
                    Check(Read().Points==points,"Ctrl+drag moved the screen's points.");
                    before=Read();
                    SendMessage(window.Handle,0x201,9,Position(x,y));SendMessage(window.Handle,0x200,9,Position(x+20,y+80));
                    Check(window.FramingEditor.Draft!.Zoom<before.Framing.Zoom,"Ctrl+drag downward did not zoom out.");
                    SendMessage(window.Handle,0x202,8,Position(x+20,y+80));
                    await Until(()=>Read().Framing.Zoom<before.Framing.Zoom&&!window.FramingEditor.Busy,"Ctrl+drag downward saves");
                    before=Read();
                    SendMessage(window.Handle,0x201,9,Position(x,y));SendMessage(window.Handle,0x200,9,Position(x+80,y));
                    Check(window.FramingEditor.Draft!.Zoom>before.Framing.Zoom,"Rightward Ctrl+drag did not zoom in.");
                    SendMessage(window.Handle,0x202,8,Position(x+80,y));
                    await Until(()=>Read().Framing.Zoom>before.Framing.Zoom&&!window.FramingEditor.Busy,"Rightward Ctrl+drag saves zoom");
                    before=Read();
                    SendMessage(window.Handle,0x201,9,Position(x,y));SendMessage(window.Handle,0x200,9,Position(x-80,y));
                    Check(window.FramingEditor.Draft!.Zoom<before.Framing.Zoom,"Leftward Ctrl+drag did not zoom out.");
                    SendMessage(window.Handle,0x202,8,Position(x-80,y));
                    await Until(()=>Read().Framing.Zoom<before.Framing.Zoom&&!window.FramingEditor.Busy,"Leftward Ctrl+drag saves zoom");
                    before=Read();
                    SendMessage(window.Handle,0x201,5,Position(x,y)); SendMessage(window.Handle,0x200,5,Position(x+35,y+20));
                    Check(Read().Framing == before.Framing && window.FramingEditor.Panning,"Shift+drag saved before release or failed to capture.");
                    SendMessage(window.Handle,0x202,4,Position(x+35,y+20));
                    await Until(() => Read().Framing != before.Framing && !window.FramingEditor.Busy,"Shift+drag saves panning on release");
                    Check(Read().Points == points,"Shift+drag moved the screen instead of its media.");
                    var modifierInput=new ModifierFramingInput(Forms.Keys.None,new(.35,.4),true);
                    window.ModifierInputForTest=()=>modifierInput;
                    await Task.Delay(80);
                    before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Control};
                    await Until(()=>window.ModifierFramingActive,"Ctrl alone starts framing without mouse capture");
                    Check(!window.Capture,"Button-free framing captured the mouse");
                    modifierInput=modifierInput with{Pointer=new(.45,.4)};
                    await Until(()=>window.FramingEditor.Draft!.Zoom>before.Framing.Zoom,"Ctrl plus rightward movement previews zoom through the native window timer");
                    Check(Read().Framing==before.Framing,"Button-free zoom saved before Ctrl release");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};
                    await Until(()=>Read().Framing.Zoom>before.Framing.Zoom&&!window.FramingEditor.Busy,"Ctrl release persists button-free zoom");
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Shift};
                    await Until(()=>window.ModifierFramingActive,"Shift alone starts media panning");
                    modifierInput=modifierInput with{Pointer=new(.4,.33)};
                    await Until(()=>window.FramingEditor.Draft!=before.Framing,"Shift plus movement pans without a mouse button");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};
                    await Until(()=>Read().Framing!=before.Framing&&!window.FramingEditor.Busy,"Shift release persists button-free pan");
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Alt};
                    await Until(()=>window.ModifierFramingActive&&window.FramingEditor.Rotating,"Alt starts media rotation");
                    modifierInput=modifierInput with{Pointer=new(.45,.33)};
                    await Until(()=>window.FramingEditor.Draft!.RotationDegrees>before.Framing.RotationDegrees,"Alt plus rightward movement previews clockwise rotation");
                    Check(Read().Framing==before.Framing&&window.FramingEditor.Draft!.Zoom==before.Framing.Zoom,"Rotation saved early or changed zoom");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};
                    await Until(()=>Read().Framing.RotationDegrees>before.Framing.RotationDegrees&&!window.FramingEditor.Busy,"Alt release persists rotation");
                    var savedRotation=Read().Framing.RotationDegrees;
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Control};
                    await Until(()=>window.ModifierFramingActive,"Zoom starts on an already rotated video");
                    modifierInput=modifierInput with{Pointer=new(.5,.33)};
                    await Until(()=>window.FramingEditor.Draft!.Zoom>before.Framing.Zoom,"Zooming rotated media previews increased zoom");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};
                    await Until(()=>Read().Framing.Zoom>before.Framing.Zoom&&!window.FramingEditor.Busy,"Zooming rotated media saves");
                    Check(Read().Framing.RotationDegrees==savedRotation,"Zooming reset the rotation angle");
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Shift};
                    await Until(()=>window.ModifierFramingActive,"Movement starts on zoomed and rotated media");
                    modifierInput=modifierInput with{Pointer=new(.48,.35)};
                    await Until(()=>window.FramingEditor.Draft!=before.Framing,"Moving zoomed and rotated media previews a changed source position");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};
                    await Until(()=>Read().Framing!=before.Framing&&!window.FramingEditor.Busy,"Moving zoomed and rotated media saves");
                    Check(Read().Framing.RotationDegrees==savedRotation&&Read().Framing.Zoom==before.Framing.Zoom,"Moving media changed its rotation or zoom");
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Alt};
                    await Until(()=>window.ModifierFramingActive,"Alt rotation can begin again");
                    modifierInput=modifierInput with{Pointer=new(.45,.43)};
                    await Until(()=>window.FramingEditor.Draft!.RotationDegrees<before.Framing.RotationDegrees,"Alt plus downward movement rotates counterclockwise");
                    modifierInput=modifierInput with{Focused=false};
                    await Until(()=>!window.ModifierFramingActive&&window.FramingEditor.Draft is null,"Losing focus cancels unfinished rotation");
                    Check(Read().Framing.RotationDegrees==savedRotation,"Focus loss saved unfinished rotation");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None,Focused=true};await Task.Delay(80);
                    await Task.Delay(80);before=Read();modifierInput=modifierInput with{Modifiers=Forms.Keys.Control};
                    await Until(()=>window.ModifierFramingActive,"New modifier gesture starts after release");
                    modifierInput=modifierInput with{Pointer=new(.4,.23)};
                    await Until(()=>window.FramingEditor.Draft!=before.Framing,"Cancel fixture has a changed preview");
                    SendMessage(window.Handle,0x100,27,0);
                    await Task.Delay(80);
                    Check(!window.ModifierFramingActive&&window.FramingEditor.Draft is null&&Read().Framing==before.Framing,"Escape did not cancel button-free framing");
                    modifierInput=modifierInput with{Modifiers=Forms.Keys.None};await Task.Delay(80);
                    window.ModifierInputForTest=null;
                    Check(Read().Points==points,"Button-free framing changed the screen geometry");
                    var after = (await window.Renderer.SnapshotAsync()).Single();
                    Check(after.RequestId=="framing-test" && after.Frames>frames,"Zoom/move/rotation restarted playback.");
                    SendMessage(window.Handle,0x201,1,Position((int)center.X,(int)center.Y)); SendMessage(window.Handle,0x202,0,Position((int)center.X,(int)center.Y));
                    var typing = window.Controls.OfType<ScreenNameEditor>().Single(); typing.Text="Typing";
                    var beforeTyping = Read().Framing;
                    SendMessage(typing.Handle,0x20A,(nint)(1200 << 16),Position(nativeCenter.X,nativeCenter.Y));
                    Check(window.Controls.Contains(typing) && window.FramingEditor.Draft is null && Read().Framing==beforeTyping,
                        "Scrolling discarded a typed name or zoomed while editing it.");
                    SendMessage(typing.Handle,0x100,27,0);
                    SendMessage(window.Handle,0x201,1,Position(x,y)); SendMessage(window.Handle,0x200,1,Position(x+20,y)); SendMessage(window.Handle,0x202,0,Position(x+20,y));
                    await Until(() => Read().Points.TopLeft.X > .1 && !host.CalibrationBusy,"Ordinary drag still moves screen geometry");
                    var framing = Read().Framing;
                    await Until(() => window.ScreenDefinition(screen.Id)!.Points == Read().Points,"Geometry reloads");
                    Wheel(120);
                    var remote = new VideoFramingOptions { Zoom=3, RotationDegrees=30 };
                    await store.SaveVideoFramingAsync(display,Read(),remote);
                    await Until(() => window.ScreenDefinition(screen.Id)!.Framing==remote,"Remote framing applies live");
                    await Task.Delay(500);
                    Check(Read().Framing==remote && window.FramingEditor.Draft is null,"Local wheel draft overwrote a remote framing edit.");
                    await store.AdjustScreenBrightnessAsync(display,screen.Id,-45);
                    await store.ToggleScreenTransparencyAsync(display,screen.Id);
                    await store.SetPerspectiveAsync(display,screen.Id,new(){ControllerId=Guid.NewGuid().ToString("N"),PerspectiveId=Guid.NewGuid().ToString("N"),Name="reset-crop",Rectangle=new(){X=10,Y=10,Width=80,Height=80}});
                    await Until(()=>window.ScreenDefinition(screen.Id)!.Perspective is not null && window.ScreenDefinition(screen.Id)!.Brightness==55 && window.ScreenDefinition(screen.Id)!.BlackKey is not null,"Video reset fixture applies brightness, chroma key and perspective");
                    var pointsBeforeReset=Read().Points;
                    var playbackBeforeReset=(await window.Renderer.SnapshotAsync()).Single();
                    SendMessage(host.HotkeyWindowHandle,0x312,ApplicationHotkeys.ResetVideoId,(nint)((0x52<<16)|2));
                    await Until(()=>Read().Framing==new VideoFramingOptions() && Read().Brightness==100 && Read().BlackKey is null && Read().Perspective is null && host.HudMessage.Contains("video reset"),"Ctrl+R resets framing, brightness, chroma key and perspective together");
                    Check(Read().Points==pointsBeforeReset,"Ctrl+R changed screen points");
                    var playbackAfterReset=(await window.Renderer.SnapshotAsync()).Single();
                    Check(playbackAfterReset.RequestId==playbackBeforeReset.RequestId && playbackAfterReset.Frames>=playbackBeforeReset.Frames,"Video reset restarted playback");
                    var originalOutline=Read().Points;
                    int edgeX=(int)((originalOutline.TopLeft.X+originalOutline.TopRight.X)*.5*(window.ClientSize.Width-1));
                    int edgeY=(int)((originalOutline.TopLeft.Y+originalOutline.TopRight.Y)*.5*(window.ClientSize.Height-1));
                    SendMessage(window.Handle,0x203,1,Position(edgeX,edgeY));SendMessage(window.Handle,0x202,0,Position(edgeX,edgeY));
                    await Until(()=>Read().Points.Vertices.Count==5&&!host.CalibrationBusy&&window.ScreenDefinition(screen.Id)!.Points==Read().Points,"Double-click video edge inserts and saves a point");
                    var addedOutline=Read().Points;
                    Check(addedOutline.TopLeft==originalOutline.TopLeft&&addedOutline.TopRight==originalOutline.TopRight,"Video point insertion changed the keystone anchors");
                    SendMessage(window.Handle,0x201,1,Position(edgeX,edgeY));SendMessage(window.Handle,0x200,1,Position(edgeX,edgeY+60));SendMessage(window.Handle,0x202,0,Position(edgeX,edgeY+60));
                    await Until(()=>Read().Points!=addedOutline&&!host.CalibrationBusy,"Dragging the inserted video point saves an irregular outline");
                    SendMessage(window.Handle,0x100,(nint)Forms.Keys.Z,0);
                    await Until(()=>Read().Points==addedOutline&&!host.CalibrationBusy,"Undo restores the last video outline movement");
                    var playbackAfterOutline=(await window.Renderer.SnapshotAsync()).Single();
                    Check(playbackAfterOutline.RequestId==playbackAfterReset.RequestId&&playbackAfterOutline.Frames>playbackAfterReset.Frames,"Adding, moving or undoing video outline points interrupted playback");
                    SendMessage(window.Handle,0x100,(nint)Forms.Keys.R,0);
                    await Until(()=>Read().Points==new PointOptions()&&!host.CalibrationBusy,"Reset clears the extra outline points and mapping indices");
                    await store.SetConfigurationModeAsync(false);
                    await Until(() => !window.Capture && window.FramingEditor.Draft is null,"Leaving configuration releases gestures");
                    await Task.Delay(150);
                    window.ZoomSelected(1); Wheel(1200); SendMessage(window.Handle,0x201,9,Position(x,y)); SendMessage(window.Handle,0x200,9,Position(x,y-80)); SendMessage(window.Handle,0x202,8,Position(x,y-80));
                    SendMessage(window.Handle,0x201,5,Position(x,y)); SendMessage(window.Handle,0x200,5,Position(x+40,y)); SendMessage(window.Handle,0x202,4,Position(x+40,y));
                    await Task.Delay(500);
                    Check(Read().Framing==new VideoFramingOptions() && Read().Points==new PointOptions(),"Production mode accepted zoom, pan or geometry changes.");
                    await store.SetConfigurationModeAsync(true);
                    // Close while the wheel-save debounce is still pending: the latest framing must survive.
                    window.FramingEditor.Wheel(Read(), (double)x/(window.ClientSize.Width-1), (double)y/(window.ClientSize.Height-1), 120);
                    var pending = window.FramingEditor.Draft;
                    Check(pending is { Zoom: > 1 },"Pending shutdown framing was not created.");
                    await host.DisposeAsync(); host=null;
                    Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Single().Framing==pending,"Shutdown lost the last wheel gesture.");
                    Console.WriteLine("PASS wheel/keyboard zoom, Ctrl+drag zoom, Shift+drag pan, ordinary screen drag, saved framing, uninterrupted video and production-mode guards.");
                }
                catch(Exception ex) { failure = ex; }
                finally
                {
                    try { if(host is not null) await host.DisposeAsync(); }
                    catch(Exception ex) { failure ??= ex; }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    Console.WriteLine("Framing evidence: "+folder);
                    if(failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
                }
            });
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        finally { if(!thread.Join(TimeSpan.FromSeconds(5))) throw new Exception("Framing test dispatcher did not stop."); }
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static Task Until(Func<bool> ready,string message) => UntilAsync(()=>Task.FromResult(ready()),message);
    private static async Task UntilAsync(Func<Task<bool>> ready,string message)
    {
        var clock=Stopwatch.StartNew(); while(clock.ElapsedMilliseconds<8000)
        {
            try { if(await ready()) { Console.WriteLine("PASS "+message); return; } } catch(IOException) { }
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
}
