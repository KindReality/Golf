using ExperienceX;

internal static class ApplicationLifetimeChecks
{
    public static async Task Run()
    {
        if (typeof(ExperienceOptions).Assembly.GetName().Name != "ExperienceX")
            throw new Exception("Tests are not using the built application assembly.");
        var state = new RendererStateMachine();
        try
        {
            state.TransitionTo(RendererState.Playing);
            throw new Exception("Invalid startup transition accepted.");
        }
        catch (InvalidOperationException) { }
        foreach (var next in new[] { RendererState.Idle, RendererState.Opening, RendererState.Playing, RendererState.FadingOut, RendererState.Idle, RendererState.Failed })
        {
            state.TransitionTo(next);
            if (state.Current != next)
                throw new Exception("Renderer transition failed.");
        }
        try
        {
            state.TransitionTo(RendererState.Idle);
            throw new Exception("Failed worker resumed without recreation.");
        }
        catch (InvalidOperationException) { }

        var options = new ExperienceOptions();
        var timeline = new VideoPlaybackTimeline();
        timeline.Reset();
        if (timeline.Started || timeline.Opacity != 0)
            throw new Exception("Video timeline did not reset transparently.");
        timeline.Start();
        var middle = timeline.AdvanceAt(0.25, options, 100, 0.25, false);
        if (middle.Opacity != 0.5 || middle.Complete)
            throw new Exception("Video fade-in timing changed.");
        var playing = timeline.AdvanceAt(50, options, 100, 50, false);
        if (playing.Complete || playing.FadeStarted || playing.Opacity != 1)
            throw new Exception("Long-running video did not continue playing.");
        var fade = timeline.AdvanceAt(97, options, 100, 97, false);
        if (!fade.FadeStarted || fade.FadeSeconds != 3)
            throw new Exception("Fade-out did not start before the natural end.");
        if (timeline.AdvanceAt(98.5, options, 100, 98.5, false).Opacity != 0.5 || !timeline.AdvanceAt(100, options, 100, 100, false).Complete)
            throw new Exception("Video fade did not finish at the natural end.");
        timeline.Reset();
        timeline.Start();
        if (timeline.AdvanceAt(0, options, 0.5, 0, false).FadeSeconds != 0.5 || !timeline.AdvanceAt(0.5, options, 0.5, 0.5, true).Complete)
            throw new Exception("Short-video fade changed.");

        var scope = new AsyncTaskScope("test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Run(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { cancelled.SetResult(); }
        }, "Cancelable work");
        await started.Task;
        await scope.DisposeAsync();
        if (!cancelled.Task.IsCompleted)
            throw new Exception("Scope returned before cancelling and draining its task.");
        bool ranAfterStop = false;
        scope.Run(_ => { ranAfterStop = true; return Task.CompletedTask; }, "After stop");
        await scope.DisposeAsync();
        if (ranAfterStop)
            throw new Exception("Disposed scope accepted new work.");
        Console.WriteLine("Passed application assembly reference, renderer transitions, extracted playback timeline, and owned-task shutdown checks.");
    }
}
