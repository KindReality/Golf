using System.Drawing;
using System.Drawing.Imaging;
using ExperienceX;

internal static class ScreenMediaBindingChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExperienceX-binding-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            void Check(bool value, string message) { if (!value) throw new Exception(message); }
            var eye = Path.Combine(directory, "Eye.mp4");
            File.WriteAllText(eye, "test video metadata");
            File.WriteAllText(Path.Combine(directory, "Sound.mp3"), "audio");
            File.WriteAllText(Path.Combine(directory, ".pending.download"), "partial");
            Directory.CreateDirectory(Path.Combine(directory, "Other"));
            File.WriteAllText(Path.Combine(directory, "Other", "Nested.mp4"), "nested");
            File.WriteAllText(Path.Combine(directory, "Ghost.v1.mp4"), "dotted basename");
            var catalog = ScreenMediaCatalog.Read(directory);
            var screen = new ScreenOptions { Name = "eYe" };
            var original = catalog.Match(screen)!;
            Check(original.Path == eye && !original.IsImage, "Video basename matching must be case insensitive.");
            Check(catalog.Match(screen with { Name = "Eye.mp4" }) == original, "Explicit filenames must select the same video.");
            Check(catalog.Match(screen with { Name = "Ghost.v1" }) is not null, "Dots within a basename must be retained.");
            Check(catalog.Match(screen with { Name = "Sound" }) is null && catalog.Match(screen with { Name = "Nested" }) is null,
                "Audio, downloads and nested files must not bind video screens.");
            Check(catalog.Match(screen with { Type = "mat" }) is null, "Mat screens must never start media.");
            Check(catalog.Match(screen with { Name = "Ordinary" }) is null, "Renaming away from a media file must unlock playback.");

            var image = Path.Combine(directory, "Eye.png");
            using (var bitmap = new Bitmap(3, 2))
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
                bitmap.SetPixel(2, 1, Color.Blue);
                bitmap.Save(image, ImageFormat.Png);
            }
            var ambiguous = ScreenMediaCatalog.Read(directory);
            var conflict = ambiguous.Match(screen)!;
            Check(conflict.Path is null && conflict.Error?.Contains("Eye.mp4") == true && conflict.Error.Contains("Eye.png"),
                "Ambiguous basenames must remain locked and report both filenames.");
            Check(ambiguous.Match(screen with { Name = "Eye.png" })?.IsImage == true && ambiguous.Match(screen with { Name = "Eye.mp4" })?.Path == eye,
                "Explicit extensions must resolve video/image ambiguity.");
            var pixels = StillImagePixels.Read(image);
            Check(pixels.Width == 3 && pixels.Height == 2 && pixels.Bgra[0] == 0 && pixels.Bgra[1] == 0 &&
                pixels.Bgra[2] == 128 && pixels.Bgra[3] == 128 && pixels.Bgra[20] == 255,
                "Decoded still images must retain BGRA orientation and premultiplied alpha.");
            File.Delete(image); // The decoder must not retain a file lock after loading.
            Check(ScreenMediaCatalog.Read(directory).Match(screen)?.Path == eye, "Removing an ambiguous candidate must restore the video binding.");
            File.AppendAllText(eye, "changed");
            Check(ScreenMediaCatalog.Read(directory).Match(screen) != original, "Replacing a local media file must change its binding fingerprint.");
            File.Delete(eye);
            Check(ScreenMediaCatalog.Read(directory).Match(screen) is null, "Removing a file must release its name binding.");
            Check(ScreenMediaCatalog.Read(Path.Combine(directory, "Missing")).Match(screen) is null, "A missing media folder must allow normal startup.");
            Console.WriteLine("Passed screen media matching, ambiguity, metadata changes, mat/audio exclusions and still-image alpha/file-release checks.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
