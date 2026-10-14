using System.Windows.Threading;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class LedEditorChecks
{
    internal static async Task Run()
    {
        var devices=await WledDiscovery.FindAsync();
        if(devices.Length==0)throw new InvalidOperationException("No local WLED device is available for the read-only editor check.");
        var device=devices.First(d=>d.Segments.Length>0);
        using var client=new WledClient();await client.ValidateStreamingAsync(device.Address);
        var definition=new PerspectiveDefinition{Name="LED editor test",Type="pixelArray",Led=device.Perspective(device.Segments[0],false)};
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>
            {
                using var editor=new LedPerspectiveEditor(definition);
                try
                {
                    editor.Show();
                    IEnumerable<Control> Tree(Control parent)=>parent.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Tree(c)));
                    var save=Tree(editor).OfType<Button>().Single(b=>b.Text=="Save perspective");
                    var clock=System.Diagnostics.Stopwatch.StartNew();while(!save.Enabled){if(clock.ElapsedMilliseconds>10000)throw new TimeoutException("WLED editor did not load segments.");await Task.Delay(20);}
                    var fps=Tree(editor).OfType<NumericUpDown>().Single(n=>n.Maximum==120);
                    var thickness=Tree(editor).OfType<NumericUpDown>().Single(n=>n.Maximum==100);
                    fps.Value=40;thickness.Value=12.5m;
                    var screenshot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.tools/led-controller-editor.png"));
                    using(var bitmap=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));bitmap.Save(screenshot);}
                    save.PerformClick();clock.Restart();while(editor.Result is null){if(clock.ElapsedMilliseconds>10000)throw new TimeoutException("WLED editor did not save its result.");await Task.Delay(20);}
                    if(editor.Result.Id!=definition.Id || editor.Result.Led!.DeviceMac!=device.Mac || editor.Result.Led.FramesPerSecond!=40 || editor.Result.Led.SamplingThicknessPercent!=12.5 || editor.Result.Led.PixelCount!=device.Segments[0].PixelCount)
                        throw new Exception("WLED editor lost identity, dimensions or edited settings.");
                    Console.WriteLine("PASS Controller discovers WLED, loads segments, validates realtime settings and edits per-perspective FPS/thickness without changing the WLED device.");
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{editor.Close();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
