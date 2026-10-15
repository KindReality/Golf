using System.Text.Json;
using ExperienceX;

internal static class VideoOutlineChecks
{
    internal static async Task Run()
    {
        void Check(bool value,string message) { if(!value)throw new Exception(message); }
        var quad = new PointOptions { Enabled=true, TopLeft=new(.2,.2), TopRight=new(.8,.2), BottomRight=new(.8,.8), BottomLeft=new(.2,.8) };
        var outline = PointEditing.Insert(quad,0,new(.5,.2),video:true);
        outline.Validate();
        Check(outline.Vertices.Count==5 && outline.MappingCornerIndices!.SequenceEqual([0,2,3,4]),"Inserting a video point lost its four original mapping anchors");
        Check(outline.TopLeft==quad.TopLeft && outline.TopRight==quad.TopRight && outline.BottomRight==quad.BottomRight && outline.BottomLeft==quad.BottomLeft,"A new outline point changed keystone corners");
        var outward=PointEditing.Move(outline,1,.5,.05);
        outward.Validate();
        Check(outward.Vertices[1]==new ScreenPoint(.5,.05),"An extra video point cannot expand beyond the old keystone rectangle");
        Check(VideoFramingOptions.TryPointer(outward,.5,.05,out var tip) && Math.Abs(tip.Y)<1e-6,"Expanded video mapping did not reach an outward point");
        var concave=PointEditing.Move(outline,1,.5,.5);
        concave.Validate();
        Check(!ScreenSelection.Contains(concave,.5,.3)&&ScreenSelection.Contains(concave,.3,.6),"Concave video selection filled its notch");
        var twice=PointEditing.Insert(outward,3,new(.5,.8),video:true);
        twice.Validate();
        Check(twice.MappingCornerIndices!.SequenceEqual([0,2,3,5]),"Repeated insertion did not retain the mapping anchor indices");
        Check(twice==JsonSerializer.Deserialize<PointOptions>(JsonSerializer.Serialize(twice)),"Video outline round trip lost anchor identity or equality");
        var many = new PointOptions { Enabled=true, MappingCornerIndices=[0,10,20,30],
            Vertices=Enumerable.Range(0,40).Select(i=>new ScreenPoint(.5+.4*Math.Cos(-3*Math.PI/4+i*Math.PI/20),.5+.4*Math.Sin(-3*Math.PI/4+i*Math.PI/20))).ToArray() };
        many.Validate();
        Check(PolygonGeometry.Triangles(many).Length==38*3&&PointEditing.Points(many).Length==40,"Video outlines retained a four/32-point limit");
        var cache=new VideoOutlineCache();
        var covered=cache.Get(outward,1920,1080,new(){RotationDegrees=45});
        Check(covered.Framing.Zoom>=1&&cache.Get(outward,1920,1080,new(){RotationDegrees=45}).Framing==covered.Framing,"Outline coverage is unstable");
        foreach(var bad in new[] { outline with{MappingCornerIndices=[0,0,3,4]}, outline with{MappingCornerIndices=[0,2,3,99]}, outline with{MappingCornerIndices=null}, outline with{Vertices=[new(.2,.2),new(.5,.9),new(.8,.2),new(.8,.8),new(.2,.8)]} })
        {
            try { bad.Validate(); throw new Exception("Invalid video outline accepted"); } catch(ArgumentException) { }
        }
        var folder=Path.Combine(Path.GetTempPath(),"ExperienceX-outline-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path=Path.Combine(folder,"appsettings.json");
            var screen=new ScreenOptions{Id="video",Name="Eye",Roles="wall",Points=quad,Framing=new(){Zoom=2,RotationDegrees=30}};
            File.WriteAllText(path,JsonSerializer.Serialize(new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["TEST"]=[screen]}}));
            var store=new PointConfigurationStore(path);
            await store.SaveAsync("TEST",quad,twice,screen.Id);
            var loaded=ExperienceOptions.Parse(new PointConfigurationStore(path).EnsureDisplays(["TEST"])).ScreensFor("TEST").Single();
            Check(loaded.Points==twice&&loaded.Framing==screen.Framing&&loaded.Roles=="wall","Saving/restarting polygon video lost geometry or other screen settings");
            var copy=ScreenCopy.Decode(new ScreenCopy{SourceDisplay="TEST",Screen=loaded}.Encode());
            Check(copy.Screen.Points==twice&&copy.Screen.Framing==screen.Framing,"Copy/paste lost outline points or mapping anchors");
            await store.SaveAsync("TEST",twice,quad,screen.Id);
            Check(ExperienceOptions.Parse(File.ReadAllText(path)).PointsFor("TEST")==quad,"Undo/reset retained stale mapping indices");
            await store.SaveAsync("TEST",quad,twice,screen.Id);
            try { await store.SaveAsync("TEST",quad,concave,screen.Id); throw new Exception("A stale outline edit overwrote a newer edit"); } catch(PointEditConflictException) { }
        }
        finally { Directory.Delete(folder,true); }
        Console.WriteLine("PASS video outline insertion, outward expansion, concave selection, anchor identity, invalid geometry, persistence, copy/paste, reset and stale-edit guards.");
    }
}
