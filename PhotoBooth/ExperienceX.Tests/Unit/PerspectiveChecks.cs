using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;

internal static class PerspectiveChecks
{
    private sealed class Transport : HttpMessageHandler
    {
        public byte[] Bytes = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Bytes) }); }
    }
    public static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>
            {
                var path=Path.Combine(Path.GetTempPath(),"PerspectiveChecks-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                try
                {
                    var id=Guid.NewGuid().ToString("N");var secondId=Guid.NewGuid().ToString("N");
                    var perspective=new PerspectiveDefinition{Name="bottom-third",Rectangle=new(){Y=66.67,Height=33.33}};
                    var definitions=new ControllerSharedDefinitions{ControllerId=id,ControllerName="controller-one",Hostname="controller-one",Perspectives=[perspective]};
                    var configuration=Path.Combine(path,"appsettings.json");
                    File.WriteAllText(configuration,JsonSerializer.Serialize(new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["display1"]=[new ScreenOptions{Perspective=definitions.Select(perspective)}]}},ConfigurationSchema.Json));
                    var store=new PointConfigurationStore(configuration);
                    var transport=new Transport{Bytes=definitions.Serialize()};
                    await using var client=new ControllerDefinitionsClient(dispatcher,d=>store.ApplyPerspectivesAsync(d),transport);
                    BroadcastEnvelope Announcement(ControllerSharedDefinitions d,string? revision=null)=>BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new(){DeviceName=d.ControllerName},"event","*",new{@event=ControllerDiscovery.ResponseEvent,input="controller",hostname=d.Hostname,controllerId=d.ControllerId,revision=revision??ControllerSharedDefinitions.Revision(d.Serialize()),definitionsPort=54244}));
                    client.Receive(Announcement(definitions));await Until(()=>client.Perspectives.Count==1);
                    var loaded=ExperienceOptions.Parse(File.ReadAllText(configuration));
                    Check(loaded.ControllerDefinitions.Count==1 && loaded.ScreensFor("display1")[0].Perspective!.Rectangle.Y==66.67,"Unsolicited announcement downloads and caches complete definitions");
                    client.Receive(Announcement(definitions));await Task.Delay(100);
                    Check(transport.Calls==1,"Repeated revision does not download unchanged definitions");
                    definitions=definitions with{Perspectives=[perspective with{Name="renamed",Rectangle=new(){X=25,Width=50}}]};transport.Bytes=definitions.Serialize();client.Receive(Announcement(definitions));
                    await Until(()=>ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!.Name=="renamed");
                    Check(client.Perspectives[0].PerspectiveId==perspective.Id,"Renaming retains stable perspective identity and updates existing screens");
                    var second=definitions with{ControllerId=secondId,ControllerName="controller-two",Hostname="controller-two"};transport.Bytes=second.Serialize();client.Receive(Announcement(second));await Until(()=>client.Perspectives.Count==2);
                    Check(client.Perspectives[0].ControllerId!=client.Perspectives[1].ControllerId,"Identical names remain separate across controllers");
                    var before=ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!;
                    definitions=definitions with{Perspectives=[]};transport.Bytes=definitions.Serialize();client.Receive(Announcement(definitions));
                    await Until(()=>!ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!.Available);
                    var retained=ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!;
                    Check(retained.Rectangle==before.Rectangle,"Deleted perspectives retain the last rectangle and become unavailable");
                    definitions=definitions with{Perspectives=[perspective]};transport.Status=HttpStatusCode.ServiceUnavailable;client.Receive(Announcement(definitions));await Task.Delay(150);
                    Check(!ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!.Available,"Failed HTTP transfer preserves the last complete configuration");
                    transport.Status=HttpStatusCode.OK;transport.Bytes=definitions.Serialize();client.Receive(Announcement(definitions));
                    await Until(()=>ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!.Available);
                    Check(true,"The next periodic announcement recovers a failed download");
                    transport.Bytes=second.Serialize();definitions=definitions with{ControllerName="changed"};client.Receive(Announcement(definitions));await Task.Delay(150);
                    Check(ExperienceOptions.Parse(File.ReadAllText(configuration)).ScreensFor("display1")[0].Perspective!.ControllerName=="controller-one","Revision mismatch rejects incomplete or stale definitions");
                    await using var cached=new ControllerDefinitionsClient(dispatcher,_=>Task.CompletedTask,new Transport());cached.Seed(ExperienceOptions.Parse(File.ReadAllText(configuration)).ControllerDefinitions.Values);
                    Check(cached.Perspectives.Count==2,"Cached perspectives survive restart without a controller");
                    foreach(var rectangle in new[]{new PerspectiveRectangle{X=-1},new PerspectiveRectangle{X=90,Width=11},new PerspectiveRectangle{Height=0},new PerspectiveRectangle{X=double.NaN}})
                    {try{rectangle.Validate();throw new Exception("Invalid crop accepted");}catch(ArgumentException){}}
                    Check(true,"Out-of-bounds and non-finite percentage rectangles are rejected");
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{Directory.Delete(path,true);dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static async Task Until(Func<bool> predicate){var deadline=DateTime.UtcNow.AddSeconds(4);while(true){try{if(predicate())return;}catch(IOException){}if(DateTime.UtcNow>deadline)throw new TimeoutException("Perspective state did not arrive");await Task.Delay(10);}}
}
