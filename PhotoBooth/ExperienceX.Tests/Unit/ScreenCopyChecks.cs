using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;

internal static class ScreenCopyChecks
{
    internal static async Task Run()
    {
        var folder=Path.Combine(Path.GetTempPath(),"ExperienceX-copy-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var screen=new ScreenOptions { Id="video",Name="Eye",DefaultName="screen1",MediaFile="Eye.mp4",Roles="wall,video",Brightness=65,
                BlackKey=new(){Enabled=false},Framing=new(){Zoom=2,CenterX=.7},
                Points=new(){Enabled=true,TopLeft=new(.1,.15),TopRight=new(.9,.1),BottomRight=new(.85,.9),BottomLeft=new(.05,.85)},
                Perspective=new(){ControllerId=Guid.NewGuid().ToString("N"),PerspectiveId=Guid.NewGuid().ToString("N"),Name="crop",Rectangle=new(){X=10,Y=20,Width=70,Height=60}} };
            var mat=new ScreenOptions{Id="mat",Name="mask",Type="mat",IsNegative=true,Points=new(){Enabled=true,
                Vertices=[new(.1,.1),new(.9,.1),new(.9,.9),new(.5,.5),new(.1,.9)]}};
            var options=new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["A"]=[screen,mat],["B"]=[]}};
            var path=Path.Combine(folder,"appsettings.json");var root=JsonSerializer.SerializeToNode(options)!.AsObject();root["Custom"]=42;File.WriteAllText(path,root.ToJsonString());
            var copy=new ScreenCopy{SourceDisplay="A",Screen=screen};Check(ScreenCopy.Decode(copy.Encode())==copy,"Screen clipboard lost configuration during serialization.");
            var store=new PointConfigurationStore(path);var duplicate=await store.PasteScreenAsync("A",copy);
            Check(duplicate.Id!=screen.Id && duplicate.Name=="Eye-2" && duplicate.DefaultName=="screen2","Paste reused the source identity or failed unique names.");
            Check(duplicate with {Id=screen.Id,Name=screen.Name,DefaultName=screen.DefaultName}==screen,"Copy lost media, roles, framing, brightness, chroma key, perspective or geometry.");
            var again=await store.PasteScreenAsync("A",copy);Check(again.Name=="Eye-3"&&again.Id!=duplicate.Id,"Repeated paste did not produce an independent unique screen.");
            var cross=await store.PasteScreenAsync("B",copy);Check(cross.Points==screen.Points&&cross.MediaFile=="Eye.mp4"&&cross.Name=="Eye","Cross-display copy changed normalized geometry or file binding.");
            var matCopy=new ScreenCopy{SourceDisplay="A",Screen=mat};var pastedMat=await store.PasteScreenAsync("A",matCopy);
            Check(pastedMat.IsNegative&&pastedMat.Points==mat.Points&&pastedMat.Name=="mask-2","Mat copy lost added points or negative mode.");
            var current=ExperienceOptions.Parse(File.ReadAllText(path));Check(current.ScreensFor("A").Select(s=>s.Id).SequenceEqual([screen.Id,duplicate.Id,again.Id,mat.Id,pastedMat.Id]),"Paste did not follow video/mat insertion order.");
            Check(current.ScreensFor("A")[0]==screen&&current.ScreensFor("A")[3]==mat&&JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>()==42,"Paste altered existing screens or unrelated configuration.");
            await store.SetConfigurationModeAsync(false);var before=File.ReadAllBytes(path);
            try{await store.PasteScreenAsync("B",copy);throw new Exception("Paste worked outside configuration mode.");}catch(PointEditConflictException){}
            Check(File.ReadAllBytes(path).SequenceEqual(before),"Rejected paste changed the configuration.");
            foreach(var bad in new[]{copy with{Version=2},copy with{Screen=screen with{Type="visualization",Perspective=null}},copy with{Screen=screen with{Framing=new(){Zoom=0}}}})
                try{bad.Encode();throw new Exception("Invalid clipboard screen accepted.");}catch(ArgumentException){}
            Console.WriteLine("PASS screen copy serialization, unique identities/names, complete video/mat settings, cross-display geometry, layer insertion and configuration guards.");
        }
        finally{Directory.Delete(folder,true);}
    }
    private static void Check(bool valid,string message){if(!valid)throw new Exception(message);}
}
