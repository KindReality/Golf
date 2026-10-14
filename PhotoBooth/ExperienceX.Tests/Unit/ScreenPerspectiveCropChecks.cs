using ExperienceX;
using PhotoBooth.Networking;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class ScreenPerspectiveCropChecks
{
    public static async Task Run()
    {
        void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS "+text);}
        PointOptions Quad(double left,double top,double right,double bottom)=>new(){Enabled=true,TopLeft=new(left,top),TopRight=new(right,top),BottomLeft=new(left,bottom),BottomRight=new(right,bottom)};
        var square=ScreenPerspectiveCrop.Calculate(Quad(0,0,.5625,1),1920,1080);
        Check(Math.Abs(square.WidthPercent-56.25)<1e-8 && square.HeightPercent==100,"Square pixel geometry retains full height and minimally reduces width");
        var wide=ScreenPerspectiveCrop.Calculate(Quad(0,0,1,.5),1920,1080);
        Check(wide.WidthPercent==100 && wide.HeightPercent==50,"Wide geometry retains full width and minimally reduces height");
        Check(ScreenPerspectiveCrop.Calculate(new(),1920,1080)==(100,100),"Full screen retains the full perspective without media loaded");
        var rotated=new PointOptions{Enabled=true,TopLeft=new(.5,.2),TopRight=new(.65,.5),BottomRight=new(.5,.8),BottomLeft=new(.35,.5)};
        var a=ScreenPerspectiveCrop.Calculate(rotated,1920,1080);var b=ScreenPerspectiveCrop.Calculate(rotated,3840,2160);
        Check(Math.Abs(a.WidthPercent-b.WidthPercent)<1e-8 && Math.Abs(a.HeightPercent-b.HeightPercent)<1e-8 && (a.WidthPercent==100 || a.HeightPercent==100),"Opposite-edge estimation handles rotation and resolution scaling");
        var request=new PerspectiveCreateRequest{ControllerId=Guid.NewGuid().ToString("N"),Name="Eye",WidthPercent=square.WidthPercent,HeightPercent=square.HeightPercent};
        var command=BroadcastEnvelope.Parse(request.Command(new(){DeviceName="experience-test"},"controller-test"));
        Check(command.Body.GetProperty("widthPercent").GetDouble()==56.25 && !command.Body.TryGetProperty("x",out _) && !command.Body.TryGetProperty("y",out _) && !command.Body.TryGetProperty("rectangle",out _) && !command.Body.TryGetProperty("value",out _),"Experience sends dimensions without position or media");
        Check(request.Rectangle()==new PerspectiveRectangle{X=21.875,Y=0,Width=56.25,Height=100},"Controller supplies the initial centered position");
        foreach(var invalid in new[]{request with{WidthPercent=0},request with{WidthPercent=101},request with{HeightPercent=double.NaN}})
            try{invalid.Validate();throw new Exception("Invalid dimensions accepted");}catch(ArgumentException){}
        var path=Path.Combine(Path.GetTempPath(),"Experience-perspective-crop-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(path,"{\"configurationMode\":true,\"Custom\":17,\"DisplayScreens\":{\"TEST\":[{\"Name\":\"screen1\",\"SizingMode\":\"fit\"}]}}");
            var store=new PointConfigurationStore(path);var cleaned=store.EnsureDisplays(["TEST"]);
            Check(!JsonNode.Parse(cleaned)!["DisplayScreens"]!["TEST"]![0]!.AsObject().ContainsKey("SizingMode") && JsonNode.Parse(cleaned)!["Custom"]!.GetValue<int>()==17,"Startup removes obsolete sizing settings while preserving configuration");
            var before=ExperienceOptions.Parse(cleaned).ScreensFor("TEST")[0].Points;
            await store.SetPerspectiveAsync("TEST","screen1",new(){ControllerId=request.ControllerId,PerspectiveId=request.PerspectiveId,Name=request.Name,Rectangle=request.Rectangle()});
            var after=ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("TEST")[0];
            Check(after.Points==before && after.Perspective!.Rectangle.Width==56.25,"Selecting the created perspective preserves editable destination points");
        }
        finally{foreach(var file in new[]{path,path+".bak",path+".lock"})if(File.Exists(file))File.Delete(file);}
    }
}
