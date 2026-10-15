using ExperienceX;
using Forms=System.Windows.Forms;
using System.Windows.Threading;

internal static class ModifierFramingChecks
{
    internal static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                try
                {
                    foreach (var direction in new[]
                    {
                        (X:.5, Y:.3, Zoom:4.0, Name:"up"),
                        (X:.625, Y:.5, Zoom:4.0, Name:"right"),
                        (X:.5, Y:.7, Zoom:1.0, Name:"down"),
                        (X:.375, Y:.5, Zoom:1.0, Name:"left"),
                        (X:.5625, Y:.4, Zoom:4.0, Name:"up-right"),
                        (X:.4375, Y:.6, Zoom:1.0, Name:"down-left"),
                        (X:.625, Y:.7, Zoom:2.0, Name:"opposing axes")
                    })
                    {
                        var initial=new ScreenOptions{Id="direction",Framing=new(){Zoom=2}};
                        VideoFramingOptions? draft=null;VideoFramingOptions? saved=null;
                        await using var directionEditor=new VideoFramingEditor((_,v)=>draft=v,(_,v,_)=>{saved=v;return Task.CompletedTask;},(_,_)=>{});
                        var movement=new ModifierFramingGesture(directionEditor,()=>{});
                        movement.Update(new(Forms.Keys.Control,new(.5,.5),true),initial,true,1600,1000);
                        movement.Update(new(Forms.Keys.Control,new(direction.X,direction.Y),true),initial,true,1600,1000);
                        Check(Math.Abs(draft!.Zoom-direction.Zoom)<1e-8,"Ctrl movement "+direction.Name+" uses pixel-correct zoom on a rectangular display");
                        movement.Update(new(Forms.Keys.None,new(direction.X,direction.Y),true),initial,true,1600,1000);
                        Check(direction.Zoom==2 ? saved is null : saved is not null&&Math.Abs(saved.Zoom-direction.Zoom)<1e-8,"Ctrl release commits only changed zoom for "+direction.Name);
                        Check(initial.Points==new PointOptions(),"Zoom directions preserve screen geometry");
                    }
                    foreach (var direction in new[] { (X:.625,Y:.5,Angle:90.0), (X:.5,Y:.3,Angle:90.0), (X:.375,Y:.5,Angle:-90.0), (X:.5,Y:.7,Angle:-90.0), (X:.625,Y:.7,Angle:0.0) })
                    {
                        var initial=new ScreenOptions{Id="rotation",Framing=new(){Zoom=2,CenterX=.6}};
                        VideoFramingOptions? draft=null;VideoFramingOptions? saved=null;
                        await using var rotationEditor=new VideoFramingEditor((_,v)=>draft=v,(_,v,_)=>{saved=v;return Task.CompletedTask;},(_,_)=>{});
                        var movement=new ModifierFramingGesture(rotationEditor,()=>{});
                        movement.Update(new(Forms.Keys.Alt,new(.5,.5),true),initial,true,1600,1000);
                        movement.Update(new(Forms.Keys.Alt,new(direction.X,direction.Y),true),initial,true,1600,1000);
                        Check(rotationEditor.Rotating&&Math.Abs(draft!.RotationDegrees-direction.Angle)<1e-8&&saved is null,"Alt movement previews clockwise/counterclockwise rotation without early saving");
                        Check(draft!.Zoom==2&&draft.CenterX==.6&&initial.Points==new PointOptions(),"Rotation preserved zoom, center and screen geometry");
                        movement.Update(new(Forms.Keys.None,new(direction.X,direction.Y),true),initial,true,1600,1000);
                        Check(direction.Angle==0?saved is null:saved is not null&&Math.Abs(saved.RotationDegrees-direction.Angle)<1e-8,"Alt release saves only changed rotation");
                        movement.Update(new(Forms.Keys.Alt,new(.5,.5),true),initial,true,1600,1000);
                        movement.Update(new(Forms.Keys.Alt,new(.6,.5),true),initial,true,1600,1000);
                        var previous=saved;
                        movement.Update(new(Forms.Keys.Alt,new(.6,.5),true,Escape:true),initial,true,1600,1000);
                        Check(!movement.Active&&draft is null&&saved==previous,"Escape cancels Alt rotation without saving");
                        movement.Update(new(Forms.Keys.None,new(.5,.5),true),initial,true,1600,1000);
                        movement.Update(new(Forms.Keys.Alt|Forms.Keys.Control,new(.5,.5),true),initial,true,1600,1000);
                        movement.Update(new(Forms.Keys.Alt,new(.6,.5),true),initial,true,1600,1000);
                        Check(!movement.Active,"Alt combined with another modifier blocks rotation until release");
                    }
                    var screen=new ScreenOptions{Id="video",Framing=new(){Zoom=2}};
                    int saves=0;VideoFramingOptions? preview=null;
                    await using var editor=new VideoFramingEditor((_,v)=>preview=v,(original,value,_)=>{screen=screen with{Framing=value};saves++;return Task.CompletedTask;},(_,_)=>{});
                    var gesture=new ModifierFramingGesture(editor,()=>{});
                    void Update(Forms.Keys keys,double x=.5,double y=.5,bool focused=true,bool mouse=false,bool other=false,bool escape=false,bool enabled=true,ScreenOptions? selected=null)
                        =>gesture.Update(new(keys,new(x,y),focused,mouse,other,escape),selected??screen,enabled,1000,1000);
                    void Release()=>Update(Forms.Keys.None);
                    Update(Forms.Keys.Control);Update(Forms.Keys.Control,y:.3);
                    Check(gesture.Active&&preview!.Zoom==4&&saves==0,"Ctrl plus movement previews zoom without a mouse button or early save");
                    Release();Check(saves==1&&screen.Framing.Zoom==4&&!gesture.Active,"Ctrl release saves the zoom");
                    Update(Forms.Keys.Shift);Update(Forms.Keys.Shift,x:.6,y:.55);
                    Check(preview!=screen.Framing&&saves==1,"Shift plus movement previews bounded media pan");
                    Release();Check(saves==2&&screen.Points==new PointOptions(),"Shift release saves pan without moving screen points");
                    var before=screen.Framing;
                    Update(Forms.Keys.Control);Update(Forms.Keys.Control,y:.4);Update(Forms.Keys.Control,y:.4,escape:true);
                    Check(!gesture.Active&&preview is null&&screen.Framing==before&&saves==2,"Escape cancels the modifier gesture");Release();
                    Update(Forms.Keys.Control);Update(Forms.Keys.Control,y:.4);Update(Forms.Keys.Control,focused:false);
                    Check(!gesture.Active&&preview is null&&saves==2,"Losing focus cancels instead of saving accidental movement");Release();
                    Update(Forms.Keys.Control|Forms.Keys.Shift);Update(Forms.Keys.Control,y:.3);
                    Check(!gesture.Active&&saves==2,"Mixed modifiers block gestures until the modifiers are released");Release();
                    Update(Forms.Keys.Control,other:true);Update(Forms.Keys.Control,y:.3);
                    Check(!gesture.Active,"Another shortcut prevents framing until modifier release");Release();
                    Update(Forms.Keys.Control,mouse:true);Check(!gesture.Active,"Mouse-button gestures retain their existing path");Release();
                    Update(Forms.Keys.Control,x:1.2);Update(Forms.Keys.Control,x:.5,y:.3);
                    Check(!gesture.Active,"A gesture cannot start outside the selected screen and enter while already held");Release();
                    Update(Forms.Keys.Control,enabled:false);Check(!gesture.Active,"Disabled configuration/text-edit context ignores modifier movement");Release();
                    Update(Forms.Keys.Control,selected:screen with{Type="mat"});Check(!gesture.Active,"Mats do not accept media framing gestures");Release();
                    editor.BeginZoom(screen,.5,.5,1000,1000);
                    Update(Forms.Keys.Control,y:.3);
                    Check(!gesture.Active&&editor.Dragging,"Button-free mode does not take over an existing click-and-drag gesture");
                    editor.Cancel("FixtureFinished");
                    Console.WriteLine("PASS button-free framing, release saves, Escape/focus cancellation, modifiers, shortcuts, selection and mouse gesture isolation.");
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
}
