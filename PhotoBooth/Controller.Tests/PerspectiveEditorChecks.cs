using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Networking;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class PerspectiveEditorChecks
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
                var root=Path.Combine(Path.GetTempPath(),"ControllerPerspectiveChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
                PerspectivePreview? preview=null;
                try
                {
                    var build=new BuildEnvironment("Development",1000);
                    var initial=ConfigurationDefaults.Populate("{\"ConfigurationMode\":true}",build);
                    var path=Path.Combine(root,"appsettings.json");File.WriteAllText(path,initial);
                    var current=ControllerOptions.Parse(initial,build);
                    await using var configuration=new ConfigurationService(path,build,new ActivityLog(),next=>{current=next;return Task.CompletedTask;});configuration.Start();
                    var perspective=new PerspectiveDefinition{Name="bottom-third",Rectangle=new(){Y=66.67,Height=33.33}};
                    await configuration.SavePerspectivesAsync([],[perspective]);await Until(()=>current.Perspectives.Length==1);
                    var renamed=perspective with{Name="ticker"};await configuration.SavePerspectivesAsync([perspective],[renamed]);await Until(()=>current.Perspectives[0].Name=="ticker");
                    Check(current.Perspectives[0].Id==perspective.Id,"Controller rename preserves the perspective ID");
                    try{await configuration.SavePerspectivesAsync([perspective],[]);throw new Exception("Stale edit overwrote newer definitions");}catch(InvalidOperationException){}
                    Check(current.Perspectives.Length==1,"Stale editor saves cannot overwrite a remote change");
                    preview=new();var rectangle=new PerspectiveDefinition{Name="Sample",Rectangle=new(){X=25,Y=25,Width=50,Height=50}};
                    preview.SetPerspectives([rectangle],rectangle.Id);
                    preview.Measure(new(960,540));preview.Arrange(new(0,0,960,540));preview.UpdateLayout();
                    PerspectiveDefinition? completed=null;preview.Completed+=(_,next)=>completed=next;
                    for(int i=0;i<2;i++)
                    {
                        var file=Path.Combine(root,"preview"+i+".png");using(var image=new System.Drawing.Bitmap(320*(i+1),180*(i+1))){using var graphics=System.Drawing.Graphics.FromImage(image);graphics.Clear(System.Drawing.Color.FromArgb(12,24,38));image.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
                        preview.Open(file);File.Delete(file);
                        Check(completed is null && preview.SelectedId==rectangle.Id,"Changing preview media retains the selected independent perspective");
                    }
                    preview.Nudge(1,0,1);preview.CancelEdit();Check(completed is null,"Escape cancels an unfinished keyboard rectangle edit");
                    preview.Nudge(1,0,1);preview.FinishKeyboardEdit();
                    Check(completed is not null && completed.Rectangle.X>25 && completed.Rectangle.Width==50,"Keyboard dragging moves the rectangle without changing its size");
                    preview.SetPerspectives([rectangle],rectangle.Id);completed=null;preview.NextPoint(false);preview.Nudge(1,1,10);preview.FinishKeyboardEdit();
                    Check(completed is not null && completed.Rectangle.X>25 && completed.Rectangle.Y>25 && completed.Rectangle.X+completed.Rectangle.Width==75,"Corner movement keeps the opposite corner fixed and the shape rectangular");
                    preview.SetPerspectives([rectangle],rectangle.Id);
                    await Task.Delay(100);preview.UpdateLayout();
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(960,540,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(preview);
                    var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.tools/perspective-editor-preview.png"));Directory.CreateDirectory(Path.GetDirectoryName(output)!);using(var file=File.Create(output))png.Save(file);
                    Check(!JsonSerializer.Serialize(rectangle).Contains("preview"),"Saved perspective contains no media reference");
                    var many=Enumerable.Range(0,500).Select(i=>new PerspectiveDefinition{Name="perspective"+i}).ToArray();current=current with{Perspectives=many};
                    var messages=new ConcurrentQueue<(DateTime At,BroadcastEnvelope Envelope,int Size)>();
                    var networkLog=new ActivityLog();
                    await using var announcements=new ControllerAnnouncements(()=>current,networkLog,(bytes,_,_)=>{messages.Enqueue((DateTime.UtcNow,BroadcastEnvelope.Parse(bytes),bytes.Length));return Task.CompletedTask;});
                    announcements.Start();await Until(()=>messages.Count>0);
                    if(networkLog.Snapshot().Any(entry=>entry.Message.Contains("TCP")))
                    {
                        using var http=new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler{UseProxy=false});
                        using var response=await http.GetAsync("http://localhost:"+current.DefinitionsPort+"/definitions");
                        var bytes=await response.Content.ReadAsByteArrayAsync();
                        var downloaded=JsonSerializer.Deserialize<ControllerSharedDefinitions>(bytes,ControllerSharedDefinitions.Json)!;
                        Check(response.IsSuccessStatusCode && downloaded.Perspectives.Length==500 && ControllerSharedDefinitions.Revision(bytes)==messages.First().Envelope.Body.GetProperty("revision").GetString(),"Real HTTP endpoint returns the complete matching large snapshot");
                        using var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,"http://localhost:"+current.DefinitionsPort+"/definitions");request.Headers.IfNoneMatch.Add(response.Headers.ETag!);
                        using var unchanged=await http.SendAsync(request);Check(unchanged.StatusCode==System.Net.HttpStatusCode.NotModified,"HTTP ETag avoids retransferring unchanged definitions");
                    }
                    else Console.WriteLine("HTTP binding requires Install-Controller.cmd URL reservation on this account; transport client covered separately.");
                    Check(messages.First().Size<1200 && !messages.First().Envelope.Body.TryGetProperty("perspectives",out _),"500 perspectives still produce a small general UDP announcement");
                    var revision=messages.First().Envelope.Body.GetProperty("revision").GetString();current=current with{Perspectives=[perspective]};announcements.Publish();await Until(()=>messages.Any(m=>m.Envelope.Body.GetProperty("revision").GetString()!=revision));
                    Check(true,"A completed definition change immediately announces a new revision");
                    var count=messages.Count;await Until(()=>messages.Count>count,12000);
                    Check(true,"Controller periodically announces itself every ten seconds");
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{preview?.Stop();Directory.Delete(root,true);dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static async Task Until(Func<bool> ready,int milliseconds=5000){var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);while(!ready()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Controller perspective state did not arrive");await Task.Delay(10);}}
}
