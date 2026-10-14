using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;
using System.Text.Json;
internal static class PerspectiveCreationChecks
{
    public static async Task Run()
    {
        var folder=Path.Combine(Path.GetTempPath(),"Controller-perspective-create-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var build=new BuildEnvironment("Development",1000);var path=Path.Combine(folder,"appsettings.json");var json=ConfigurationDefaults.Populate("{}",build);File.WriteAllText(path,json);
            var current=ControllerOptions.Parse(json,build);
            await using var configuration=new ConfigurationService(path,build,new ActivityLog(),next=>{current=next;return Task.CompletedTask;});configuration.Start();
            var request=new PerspectiveCreateRequest{ControllerId=current.ControllerId,Name="Eye",WidthPercent=56.25,HeightPercent=100};
            var first=await configuration.CreatePerspectiveAsync(request);
            var second=await configuration.CreatePerspectiveAsync(request with{PerspectiveId=Guid.NewGuid().ToString("N")});
            var repeat=await configuration.CreatePerspectiveAsync(request);
            var saved=ControllerOptions.Parse(File.ReadAllText(path),build);
            if(saved.Perspectives.Length!=2 || first.Name!="Eye" || second.Name!="Eye-2" || repeat.Id!=first.Id || first.Rectangle!=new PerspectiveRectangle{X=21.875,Width=56.25,Height=100})throw new Exception("Controller did not atomically center/name/idempotently persist perspectives.");
            if(saved.ConfigurationMode)throw new Exception("Remote creation incorrectly required Controller configuration mode.");
            try{await configuration.CreatePerspectiveAsync(request with{ControllerId=Guid.NewGuid().ToString("N")});throw new Exception("Wrong Controller accepted");}catch(ArgumentException){}
            Console.WriteLine("PASS dimension-only creation, Controller-owned position, unique naming, persistent duplicate suppression and correct Controller targeting");
        }
        finally{Directory.Delete(folder,true);}
    }
}
