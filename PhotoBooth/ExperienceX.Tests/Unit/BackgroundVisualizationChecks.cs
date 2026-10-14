using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;
internal static class BackgroundVisualizationChecks
{
    public static async Task Run()
    {
        var folder=Path.Combine(Path.GetTempPath(),"Experience-background-visual-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var path=Path.Combine(folder,"appsettings.json");
            var video=new ScreenOptions{Id="video",Name="Eye",Brightness=55,Points=new(){Enabled=true,TopLeft=new(.1,.1),TopRight=new(.9,.1),BottomLeft=new(.1,.9),BottomRight=new(.9,.9)}};
            var mat=new ScreenOptions{Id="mat",Name="Mask",Type="mat"};
            var visual=new ScreenOptions{Id="visual",Name="gforce",Type="visualization"};
            async Task Reset(string target)
            {
                var settings=new ExperienceOptions{ConfigurationMode=true,DisplayAliases=new(){["Projector"]="DISPLAY1"},DisplayBackgrounds=new(){["Projector"]=new(){IsTransparent=false,BackgroundColor="#102030"},["DISPLAY2"]=new(){IsTransparent=true}},DisplayScreens=new(){["DISPLAY1"]=[video,mat], ["DISPLAY2"]=[]}};
                settings.DisplayScreens[target].Add(visual);
                var root=JsonSerializer.SerializeToNode(settings)!.AsObject();root["Custom"]=17;
                await File.WriteAllTextAsync(path,root.ToJsonString());
            }
            var store=new PointConfigurationStore(path);
            foreach(var color in new[]{"#000000","#FFFFFF"})
            {
                await Reset("DISPLAY1");var before=ExperienceOptions.Parse(File.ReadAllText(path));
                var choice=new BackgroundSelection();
                if(choice.PressB("DISPLAY1",DisplayBackground.For("DISPLAY1",before).A==0) is not null)throw new Exception("G-Force background did not open the color prompt.");
                var change=color=="#000000" ? choice.PressB("DISPLAY2",true)! : choice.PressW()!;
                if(change.Display!="DISPLAY1")throw new Exception("The color choice followed the cursor instead of its original display.");
                var saved=ExperienceOptions.Parse(await store.SetBackgroundAsync(change.Display,change.IsTransparent,change.Color));
                if(saved.ScreensFor("DISPLAY1").Count!=2 || saved.ScreensFor("DISPLAY1").Any(s=>s.IsVisualization) || saved.ScreensFor("DISPLAY1")[0]!=video || saved.ScreensFor("DISPLAY1")[1]!=mat)
                    throw new Exception("A solid background did not remove only G-Force, preserving other screens/geometry.");
                if(DisplayBackground.For("DISPLAY1",saved).A!=1 || saved.DisplayBackgrounds["Projector"].IsTransparent || saved.DisplayBackgrounds["Projector"].BackgroundColor!=color || JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>()!=17)
                    throw new Exception("Background color/transparency/alias or unrelated configuration was lost.");
                Console.WriteLine("PASS "+(color=="#000000"?"B,B black":"B,W white")+" atomically removes G-Force and preserves media/mat screens and geometry");
            }
            await Reset("DISPLAY1");var transparent=ExperienceOptions.Parse(await store.SetBackgroundAsync("DISPLAY1",true));
            if(!transparent.ScreensFor("DISPLAY1").Any(s=>s.IsVisualization))throw new Exception("Transparency removed G-Force unnecessarily.");
            await Reset("DISPLAY2");var other=ExperienceOptions.Parse(await store.SetBackgroundAsync("DISPLAY1",false,"#FFFFFF"));
            if(!other.ScreensFor("DISPLAY2").Any(s=>s.IsVisualization))throw new Exception("A background change removed G-Force from another display.");
            Console.WriteLine("PASS transparency and another display's visualization are preserved");
        }
        finally{Directory.Delete(folder,true);}
    }
}
