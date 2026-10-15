using ExperienceX;
using PhotoBooth.Media;
using Forms = System.Windows.Forms;
using System.Text.Json;

internal static class ScreenMediaBrowserChecks
{
    internal static async Task Run()
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        var folder=Path.Combine(Path.GetTempPath(),"ExperienceX-media-browser-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach(var name in new[]{"Ghosts/A.mp4","Ghosts/Nested/B.png","Witches/A.mp4","Z.mp4","Ghosts/Sound.wav"})
            {
                var file=MediaLibrary.Resolve(folder,name);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,"fixture");
            }
            Directory.CreateDirectory(Path.Combine(folder,"Empty"));
            var catalog=ScreenMediaCatalog.Read(folder);
            Check(catalog.Entries("").Select(e=>e.Name).SequenceEqual(["Empty","Ghosts","Witches","Z.mp4"]),"Folders/files are not ordered alphabetically with folders first");
            Check(catalog.Entries("Ghosts").Select(e=>e.Name).SequenceEqual(["Nested","A.mp4"]),"A video browser included audio or lost nested categories");
            Check(catalog.Match(new(){Name="A"})?.Error is not null,"Duplicate media names in different folders were guessed instead of disambiguated");
            Check(catalog.Match(new(){MediaFile="Ghosts/A.mp4"})?.RelativePath=="Ghosts/A.mp4","An explicit nested binding failed");
            var browser=new ScreenMediaBrowser();var screen=new ScreenOptions{Name="Stage",DefaultName="screen1"};
            string? Key(Forms.Keys key)
            {
                var selected=browser.Navigate(catalog,screen,key);
                if(selected is not null) screen=screen with{MediaFile=selected};
                return selected;
            }
            Check(Key(Forms.Keys.Down) is null&&browser.Label=="Folder: Empty/","Down did not select the first folder from the default state");
            Key(Forms.Keys.Right);Check(Key(Forms.Keys.Down) is null,"An empty folder tried to play media");
            Key(Forms.Keys.Left);Key(Forms.Keys.Down);Key(Forms.Keys.Right);
            Check(browser.Label=="Folder: Ghosts/","Right did not enter the selected folder");
            Key(Forms.Keys.Down);Key(Forms.Keys.Right);
            Check(Key(Forms.Keys.Down)=="Ghosts/Nested/B.png"&&screen.Name=="Stage","Browsing failed to select nested media independently of screen identity");
            Key(Forms.Keys.Left);
            Check(browser.Label=="Folder: Ghosts/Nested/"&&screen.MediaFile=="Ghosts/Nested/B.png","Going to a parent stopped or changed existing playback");
            Check(Key(Forms.Keys.Down)=="Ghosts/A.mp4","Down did not move from the selected folder to its adjacent media");
            Check(Key(Forms.Keys.Right) is null&&screen.MediaFile=="Ghosts/A.mp4","Right on a media file restarted playback");
            Key(Forms.Keys.Left);Key(Forms.Keys.Down);Key(Forms.Keys.Right);
            Check(Key(Forms.Keys.Down)=="Witches/A.mp4","The same basename in another category was not independently selectable");
            var restarted=new ScreenMediaBrowser();restarted.Reconcile(catalog,screen);
            Check(restarted.Label=="Witches/A.mp4","Restart lost the selected category and media");
            Key(Forms.Keys.Left);Key(Forms.Keys.Down);Check(screen.MediaFile=="Z.mp4","Root media was unavailable");
            Check(Key(Forms.Keys.Down)==""&&catalog.Match(screen) is null,"The no-media entry did not release the playback lock");
            Check(Key(Forms.Keys.Up)=="Z.mp4","Up did not wrap through the current folder");
            foreach(var invalid in new[]{"../Escape.mp4",@"C:\Escape.mp4","/Escape.mp4","Ghosts//A.mp4","Ghosts/../A.mp4",@"\\server\media\A.mp4"})
                try{MediaLibrary.Normalize(invalid);throw new Exception("Unsafe media path accepted");}catch(ArgumentException){}
            var config=Path.Combine(folder,"config.json");
            var expected=new ScreenOptions{Id="stage",Name="Stage"};
            File.WriteAllText(config,JsonSerializer.Serialize(new ExperienceOptions{ConfigurationMode=true,DisplayScreens=new(){["TEST"]=[expected]}}));
            var store=new PointConfigurationStore(config);
            await store.SelectMediaAsync("TEST",expected,"Ghosts/Nested/B.png");
            var saved=ExperienceOptions.Parse(File.ReadAllText(config)).ScreensFor("TEST").Single();
            Check(saved.Name=="Stage"&&saved.MediaFile=="Ghosts/Nested/B.png"&&saved.Points==expected.Points,"Selecting media renamed/moved the screen or lost the relative path");
            try{await store.SelectMediaAsync("TEST",expected,"Z.mp4");throw new Exception("Stale media selection overwrote a newer selection");}catch(PointEditConflictException){}
            await store.SelectMediaAsync("TEST",saved,"");
            Check(ScreenMediaCatalog.Read(folder).Match(ExperienceOptions.Parse(File.ReadAllText(config)).ScreensFor("TEST").Single()) is null,"Unbinding did not persist");
            Console.WriteLine("PASS category folders, four-arrow navigation, empty folders, wrapping, retained playback while browsing folders, stable screen names, duplicate basenames, nested persistence and path validation.");
        }
        finally{Directory.Delete(folder,true);}
    }
}
