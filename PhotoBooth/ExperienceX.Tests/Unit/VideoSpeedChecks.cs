using System.Text.Json;
using ExperienceX;

internal static class VideoSpeedChecks
{
    internal static void Run()
    {
        ExperienceRequest Read(object body) => ExperienceRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(body));
        Check(Read(new { experience = "video", value = "Sample.mp4" }).EffectivePlaybackSpeed == 1, "Omitted video speed stays at normal playback.");
        foreach (var speed in new[] { .5, 1, 1.25, 2 })
            Check(Read(new { experience = "video", value = "Sample.mp4", playbackSpeed = speed, loop = true }).EffectivePlaybackSpeed == speed,
                $"Video playback accepts {speed}× with looping.");
        foreach (var body in new object[] { new { experience = "video", value = "Sample.mp4", playbackSpeed = 0 }, new { experience = "video", value = "Sample.mp4", playbackSpeed = 2.1 },
            new { experience = "audio", value = "Sample.wav", playbackSpeed = 1 }, new { experience = "video", command = "fadeOut", durationSeconds = 1, playbackSpeed = .5 } })
        {
            try { Read(body); throw new Exception("Invalid playback speed accepted."); } catch (ArgumentException) { }
        }
        Check(PlaybackTiming.Evaluate(100, 94, 3, 2).ShouldFade && PlaybackTiming.Evaluate(100, 94, 3, 2).FadeSeconds == 3,
            "Double-speed playback begins its three-second fade six media seconds before the end.");
        Check(!PlaybackTiming.Evaluate(100, 98, 3, .5).ShouldFade && PlaybackTiming.Evaluate(100, 98.5, 3, .5).FadeSeconds == 3,
            "Half-speed playback begins its three-second fade 1.5 media seconds before the end.");
        var options = new ExperienceOptions { TransitionInSeconds = .5, TransitionOutSeconds = 3 };
        var timeline = new VideoPlaybackTimeline(); timeline.Reset(); timeline.Start();
        Check(timeline.AdvanceAt(.25, options, 100, .5, false, 2).Opacity == .5, "Fade-in uses real seconds regardless of playback speed.");
        timeline.AdvanceAt(30, options, 100, 60, false, 2);
        timeline.AdvanceAt(45, options, 100, 94, false, 2);
        Check(timeline.AdvanceAt(46.5, options, 100, 97, false, 2).Opacity == .5 && timeline.AdvanceAt(48, options, 100, 100, true, 2).Complete,
            "Natural-end fade remains three real seconds at double speed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
