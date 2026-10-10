using System.Diagnostics;
using System.Text;
using ExperienceX;

internal static class VideoLoopChecks
{
    public static void Run()
    {
        ExperienceRequest Parse(string json) => ExperienceRequest.Parse(Encoding.UTF8.GetBytes(json));
        if (Parse("{\"experience\":\"video\",\"value\":\"ping.mp4\"}").Loop || !Parse("{\"experience\":\"video\",\"value\":\"ping.mp4\",\"loop\":true}").Loop)
            throw new Exception("Video looping defaults or explicit loop parsing failed.");
        var stop = Parse("{\"experience\":\"video\",\"command\":\"stop\",\"playbackRequestId\":\"original\"}");
        if (!stop.IsStop || stop.PlaybackRequestId != "original") throw new Exception("Playback-specific stop was not parsed.");
        foreach (var invalid in new[] {
            "{\"experience\":\"video\",\"command\":\"stop\"}",
            "{\"experience\":\"audio\",\"value\":\"sound.wav\",\"loop\":true}",
            "{\"experience\":\"audio\",\"command\":\"stop\",\"playbackRequestId\":\"a\"}",
            "{\"experience\":\"video\",\"value\":\"ping.mp4\",\"command\":\"unknown\"}",
            "{\"experience\":\"video\",\"command\":\"stop\",\"playbackRequestId\":\"a\",\"loop\":true}" })
        {
            try { Parse(invalid); throw new Exception("Invalid loop/stop was accepted."); }
            catch (ArgumentException) { }
        }
        var options = new ExperienceOptions { TransitionInSeconds = .5, TransitionOutSeconds = 3 };
        var timeline = new VideoPlaybackTimeline();
        timeline.Reset(true); timeline.Start();
        if (timeline.AdvanceAt(.25, options, 2, .25, false).Opacity != .5) throw new Exception("Loop did not fade in once.");
        foreach (var (elapsed, position, ended) in new[] { (1d, 1d, false), (1.99, 1.99, false), (2.01, .01, true), (4.1, .1, false) })
        {
            var frame = timeline.AdvanceAt(elapsed, options, 2, position, ended);
            if (frame.Complete || frame.FadeStarted || frame.Opacity != 1) throw new Exception("Loop ended or faded during repetition.");
        }
        timeline.StopAt(5, 3);
        var fading = timeline.AdvanceAt(5, options, 2, 1, false);
        if (!fading.FadeStarted || fading.FadeSeconds != 3 || fading.Complete) throw new Exception("Stop did not start the configured fade.");
        timeline.StopAt(6, 3);
        if (timeline.AdvanceAt(6.5, options, 2, .5, true).Opacity != .5 || !timeline.AdvanceAt(8, options, 2, 0, false).Complete)
            throw new Exception("Stop did not continue across a loop or duplicates extended its fade.");
        timeline.Reset(false); timeline.Start();
        if (!timeline.AdvanceAt(7, options, 10, 7, false).FadeStarted) throw new Exception("One-shot natural-end fade changed.");
        var stops = new VideoStopHistory();
        stops.Record("Controller", "old", 60, 0);
        if (!stops.Contains("controller", "old", 1) || stops.Contains("other", "old", 1) || stops.Contains("controller", "new", 1) ||
            stops.Contains("controller", "old", 61 * Stopwatch.Frequency)) throw new Exception("Stop history isolation/expiry failed.");
        Console.WriteLine("Passed video loop parsing, one fade-in across repetitions, scene stop fade, duplicate stop deadlines, one-shot regression and source/ID stop isolation.");
    }
}
