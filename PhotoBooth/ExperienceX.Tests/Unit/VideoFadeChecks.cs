using System.Diagnostics;
using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;

internal static class VideoFadeChecks
{
    internal static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        ExperienceRequest Parse(object value) => ExperienceRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        var fade = Parse(new { experience = "video", command = "fadeOut", durationSeconds = 2, roles = "^Other,*" });
        Check(fade.IsFade && !fade.IsStop && fade.Value == "" && fade.DurationSeconds == 2 && fade.HasRoles, "Fade commands require no media file and support ordinary role rules.");
        Check(Parse(new { experience = "VIDEO", command = "FADEIN", durationSeconds = 0 }).IsFade, "Fade names ignore case and zero duration is immediate.");
        foreach (var body in new object[] {
            new { experience = "video", command = "fadeOut" }, new { experience = "audio", command = "fadeIn", durationSeconds = 1 },
            new { experience = "video", command = "fadeOut", durationSeconds = -1 }, new { experience = "video", command = "fadeOut", durationSeconds = 86401 },
            new { experience = "video", command = "fadeOut", durationSeconds = 1, loop = true },
            new { experience = "video", command = "fadeOut", durationSeconds = 1, roles = "Front", screen = "*" },
            new { experience = "video", command = "fadeOut", durationSeconds = 1, playbackRequestId = "play" } })
        {
            try { Parse(body); throw new Exception("Invalid fade accepted."); } catch (ArgumentException) { }
        }
        long now = 0;
        var opacity = new ScreenOpacityFade(() => now);
        void At(double seconds) => now = (long)(seconds * Stopwatch.Frequency);
        Check(opacity.Value == 1 && !opacity.Animating, "Screen opacity starts at full visibility.");
        opacity.Start(0, 2); At(1);
        Check(Math.Abs(opacity.Value - .5) < 1e-6 && opacity.Animating, "Fade out interpolates screen opacity over its requested duration.");
        opacity.Start(1, 1); At(1.5);
        Check(Math.Abs(opacity.Value - .75) < 1e-6, "Reversing a fade starts at its current opacity without a jump.");
        At(2); Check(opacity.Value == 1 && !opacity.Animating, "Fade in reaches full visibility at its deadline.");
        opacity.Start(0, 0); At(100);
        Check(opacity.Value == 0 && !opacity.Animating, "A completed fade holds transparent until another fade command.");
        opacity.Start(1, .25); At(100.25);
        Check(opacity.Value == 1, "Fade in restores visibility after an extended transparent hold.");
        var priorities = new VideoPlaybackPriority();
        using var lease = priorities.TryBegin(CommandPriority.High, VideoPlaybackIdentity.For(@"C:\Media\Eye.mp4", "DISPLAY", new() { ScreenId = "a" }));
        Check(priorities.BlockingPriorityFor(CommandPriority.Medium, "display", "A") == CommandPriority.High &&
            priorities.BlockingPriorityFor(CommandPriority.High, "DISPLAY", "a") is null && priorities.BlockingPriorityFor(CommandPriority.Low, "DISPLAY", "b") is null,
            "Fade priority checks include queued playback and are isolated to the targeted screen.");
    }
}
