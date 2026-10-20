using ExperienceX;

internal static class MediaPathChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExperienceX-media-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Path.Combine(root, "local-profile");
            var expected = Path.Combine(profile, "Home Technologies", "ExperienceX", "Media");
            Check(MediaPath.ResolveDirectory("Media", profile) == expected, "Relative media did not resolve under LocalAppData.");
            var actualDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "ExperienceX", "Media");
            Check(MediaPath.ResolveDirectory(new ExperienceOptions().VideoDirectory) == actualDefault &&
                MediaPath.ResolveDirectory(new ExperienceOptions().AudioDirectory) == actualDefault, "Audio and video default to different or executable-local caches.");

            var share = Path.Combine(root, "controller-share"); Directory.CreateDirectory(share);
            await File.WriteAllTextAsync(Path.Combine(share, "test.mp4"), "video-content");
            await File.WriteAllTextAsync(Path.Combine(share, "test.wav"), "audio-content");
            var cache = new PhotoBooth.Media.MediaCache();
            Check(await cache.SynchronizeAsync("controller", share, expected, default), "Media could not be copied to the profile cache.");
            Directory.Move(share, share + "-offline");
            foreach (var file in new[] { "test.mp4", "test.wav" })
            {
                var path = MediaPath.Resolve("Media", file, profile);
                Check(path == Path.Combine(expected, file) && File.ReadAllText(path).EndsWith("-content"), "Playback required the Controller after media was downloaded.");
            }
            Check(MediaPath.Resolve(expected, "test.mp4") == Path.Combine(expected, "test.mp4"), "An absolute local media directory was rejected.");
            File.Delete(Path.Combine(expected, "test.mp4"));
            try { MediaPath.Resolve("Media", "test.mp4", profile); throw new Exception("Missing media fell back to a remote file."); }
            catch (FileNotFoundException) { }

            foreach (var network in new[] { @"\\offline-server\media", @"\\?\UNC\offline-server\media", "//offline-server/media", "https://server/media", "file://server/media", @"\media", "C:media" })
                Reject(() => MediaPath.ResolveDirectory(network, profile), "A remote/ambiguous media directory was accepted: " + network);
            foreach (var name in new[] { @"\\offline-server\media\test.mp4", "https://server/test.mp4", @"..\outside.mp4", "test.mp4:stream", Path.Combine(root, "absolute.mp4") })
                Reject(() => MediaPath.Resolve(expected, name), "A nonlocal or escaping media request was accepted: " + name);
            Reject(() => ExperienceOptions.Parse("""{"VideoDirectory":"\\\\offline-server\\media"}"""), "Network video configuration was accepted.");
            Reject(() => ExperienceOptions.Parse("""{"AudioDirectory":"\\\\offline-server\\media"}"""), "Network audio configuration was accepted.");
            Reject(() => new SharedAudioSource(@"\\offline-server\media\sound.wav", 1), "Coordinated audio tried to open a network file.");
            using (var video = new MediaEngineSession(() => { }))
                Reject(() => video.Open(@"\\offline-server\media\test.mp4", new() { Value = "test.mp4" }, null!), "The video decoder accepted a network path.");
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Network))
                Reject(() => MediaPath.ResolveDirectory(drive.RootDirectory.FullName), "A mapped network drive was accepted.");

            // Link creation may need Developer Mode or the symbolic-link privilege on Windows.
            var link = Path.Combine(root, "network-link");
            bool created = false;
            try { Directory.CreateSymbolicLink(link, @"\\offline-server\media"); created = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { Console.WriteLine("SKIP network-link fixture: " + ex.Message); }
            if (created)
            {
                Reject(() => MediaPath.ResolveDirectory(link), "A local-looking link to network media was accepted.");
                Directory.Delete(link);
            }
            Check(!Directory.EnumerateFiles(expected, "*.download").Any(), "Copy left partial playback files.");
            Console.WriteLine("Passed profile media defaults, offline cached playback, missing-file rejection, local overrides and network path/decoder guards.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return; }
        throw new Exception(message);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
