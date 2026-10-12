using Controller.Windows;
using System.Windows.Threading;

internal static class PerspectivePreviewPlaybackChecks
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
                var preview=new PerspectivePreview();var window=new System.Windows.Window{Content=preview,Title="Local perspective preview test",Width=960,Height=620};
                try
                {
                    var media=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Home Technologies","Controller","Media");
                    string? failed=null;preview.Failed+=message=>failed=message;
                    window.Show();preview.Open(Path.Combine(media,"Eye.mp4"));
                    await Until(()=>preview.Duration>0&&preview.Position>.3,()=>failed,"Controller preview starts playing real local video",15000);
                    preview.TogglePlay();var paused=preview.Position;await Task.Delay(250);
                    Check(!preview.Playing&&Math.Abs(preview.Position-paused)<.08,"Pause holds the preview position");
                    preview.Seek(Math.Max(0,preview.Duration-.3));preview.TogglePlay();
                    await Until(()=>preview.Position<1&&preview.Playing,()=>failed,"Seek near the end loops back to the start",8000);
                    var perspective=new PhotoBooth.Networking.PerspectiveDefinition{Name="tiny",Rectangle=new(){Width=.0001,Height=.0001}};
                    preview.SetPerspectives([perspective],perspective.Id);preview.NextPoint(false);preview.Nudge(1,1,1);preview.CancelEdit();
                    Check(true,"Tiny valid rectangles can be edited without an invalid clamp range");
                    preview.Open(Path.Combine(media,"Skull.png"));preview.TogglePlay();await Task.Delay(150);
                    Check(!preview.Playing&&preview.Duration==0,"Switching to an image detaches the old video and disables video playback");
                    preview.Open(Path.Combine(media,"Crash.mp4"));await Until(()=>preview.Duration>0&&preview.Position>.2,()=>failed,"Switching back to another video starts its independent loop",15000);
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{preview.Stop();window.Close();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private static void Check(bool success,string message){if(!success)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static async Task Until(Func<bool> ready,Func<string?> failed,string message,int timeout)
    {var until=DateTime.UtcNow.AddMilliseconds(timeout);while(!ready()){if(failed() is {} failure)throw new Exception(failure);if(DateTime.UtcNow>until)throw new TimeoutException(message);await Task.Delay(30);}Check(true,message);}
}
