using System.Diagnostics;
using System.Globalization;
using System.Text;
using ExperienceX;

internal static class GForceControlHardwareChecks
{
    internal static async Task Run()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SoundSpectrum", "G-Force", "G-Force Standalone.x64.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Install G-Force 5.8.3 to run the native control check.", executable);
        if (Process.GetProcesses().Any(p => { using (p) return p.ProcessName.StartsWith("G-Force Standalone", StringComparison.OrdinalIgnoreCase); }))
            throw new InvalidOperationException("Preserve independent G-Force instances: this check requires no Standalone running.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSpectrum", "G-Force");
        var backups = Directory.GetFiles(directory, "*.txt").ToDictionary(p => p, File.ReadAllBytes);
        var preferences = Path.Combine(directory, "Preferences (Standalone).txt");
        VisualizationProcess? visual = null;
        try
        {
            // Prevent starting a Toolbar; the test controls only its owned Standalone via TCP.
            var original = Encoding.Latin1.GetString(backups[preferences]);
            File.WriteAllBytes(preferences, Encoding.Latin1.GetBytes(original.Replace("G-Force.AutoLaunchToolbar = 1", "G-Force.AutoLaunchToolbar = 0")));
            visual = VisualizationProcess.Start(executable);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { _ = GForceControlEndpoint.FindPort(visual.Process.Id); break; }
                catch (InvalidOperationException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
            }
            var window = GForceWindow.Find(visual.Process.Id, GForceWindow.MainClass, true);
            if (window != 0) GForceWindow.Place(window, 100, 100, 480, 270);
            using var client = await GForceControlClient.ConnectAsync(visual.Process.Id);
            client.ConsoleMessage += message => Console.WriteLine("G-Force: " + message);
            var themes = await client.GetThemesAsync();
            Check(themes.Contains("Infernal"), "Direct TCP lists installed themes");
            var initialTheme = await client.GetActiveThemeAsync();
            Console.WriteLine("Initial theme: " + initialTheme);
            await client.SetColorMapSlideshowAsync(false);
            await client.SetColorMapAsync("Clear Blue Water", .25);
            await Until(async () => (await client.GetColorMapAsync()) == "Clear Blue Water");
            Console.WriteLine("PASS Direct TCP selects Clear Blue Water and reads back the active ColorMap");
            await client.ActivateThemeAsync("Infernal");
            await Until(async () => (await client.GetActiveThemeAsync()) == "Infernal");
            Console.WriteLine("PASS Direct TCP activates Infernal and reads back the active theme");
            await client.SetColorMapSlideshowAsync(false);
            await client.SetColorMapAsync("Clear Blue Water", .25);
            await Until(async () => (await client.GetColorMapAsync()) == "Clear Blue Water");
            await Task.Delay(1000);
            Check(await client.GetColorMapAsync() == "Clear Blue Water", "Selected palette remains active with the ColorMap slideshow disabled");
            await client.SetAudioSensitivityAsync(.75);
            var response = double.Parse(await client.GetAudioSensitivityAsync(), CultureInfo.InvariantCulture);
            Check(Math.Abs(response - .75) < .00001, "Direct TCP changes and reads back audio sensitivity");
            var readbacks = await Task.WhenAll(client.GetActiveThemeAsync(), client.GetColorMapAsync());
            Check(readbacks[0] == "Infernal" && readbacks[1] == "Clear Blue Water", "Concurrent calls retain the correct reply association");
            await client.StartParticleAsync("Explosion");
            var running = await client.ExecuteAsync("print( GetRunningList( \"Particle\" ) );");
            Check(running.Contains("Explosion"), "Direct TCP starts an Explosion particle and reads back running particles");
            await client.ExecuteAsync("print( NoSuchSaftFunction() );");
            Check(await client.GetColorMapAsync() == "Clear Blue Water", "Interpreter error notifications do not desynchronize later replies");
            var controls = new VisualizationControls(client);
            var defaults = new VisualizationOptions { WaveShape = "Simple Vertical", FlowField = "Linear Spread", TransitionSeconds = 0 };
            await controls.ApplyAsync(defaults, default);
            Check(await client.GetActiveThemeAsync() == "(Standard)" && await client.GetColorMapAsync() == "Clear Blue Water", "Launch applies the explicit theme and palette");
            Check(!await client.EnsureAutomaticChangesOffAsync(default), "Automatic changes are off for all six visual families");
            await controls.ExecuteAsync(new() { Experience = "visualization", Command = "next", VisualType = "colorMap" }, defaults, default);
            var changed = await client.GetColorMapAsync();
            Check(changed != "Clear Blue Water", "Next selects another palette");
            await controls.ExecuteAsync(new() { Experience = "visualization", Command = "previous", VisualType = "colorMap" }, defaults, default);
            Check(await client.GetColorMapAsync() == "Clear Blue Water", "Previous restores the palette");
            await controls.ExecuteAsync(new() { Experience = "visualization", Command = "start", VisualType = "sprite", Value = "Plasma", DurationSeconds = 10 }, defaults, default);
            Check((await client.ExecuteAsync("print( GetRunningList( \"Sprite\" ) );")).Contains("Plasma"), "Sprite start accepts a duration");
            await controls.ExecuteAsync(new() { Experience = "visualization", Command = "stop", VisualType = "sprite", TransitionSeconds = 0 }, defaults, default);
            await Until(async () => !(await client.ExecuteAsync("print( GetRunningList( \"Sprite\" ) );")).Contains("Plasma"));
            Console.WriteLine("PASS Sprite stop clears running sprites without stopping the visualizer");
            try { await controls.ExecuteAsync(new() { Experience = "visualization", Command = "select", VisualType = "colorMap", Value = "Missing Saft Palette" }, defaults, default); throw new Exception("Unknown palette succeeded"); }
            catch (ArgumentException) { Console.WriteLine("PASS Unknown palette rejected before interpreter execution"); }
            await controls.ApplyAsync(defaults with { TransitionSeconds = null }, default);
            Check(await client.GetColorMapAsync() == "Clear Blue Water", "Timing-only configuration changes preserve the current visuals");
            await controls.ExecuteAsync(new() { Experience = "visualization", Command = "select", VisualType = "flowField", Value = "Tan Turn", TransitionSeconds = 0, DelaySeconds = .5 }, defaults, default);
            await Until(async () => (await client.CurrentAsync("FlowField", default)) == "Tan Turn", 30);
            Console.WriteLine("PASS A scheduled motion-field command applies its named field");
        }
        finally
        {
            try { if (visual is not null) await visual.StopAsync(); }
            finally
            {
                foreach (var (path, content) in backups) File.WriteAllBytes(path, content);
                Console.WriteLine("Owned Standalone closed; original G-Force preferences restored; independent Toolbar untouched.");
            }
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static async Task Until(Func<Task<bool>> ready, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("G-Force did not report the requested setting.");
            await Task.Delay(50);
        }
    }
}
