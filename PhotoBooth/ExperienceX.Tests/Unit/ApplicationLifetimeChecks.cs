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
        var middle = timeline.AdvanceAt(2.5, options, 100, 2.5, false);
        if (middle.Opacity != 0.5 || middle.Complete)
            throw new Exception("Video fade-in timing changed.");
        timeline.AdvanceAt(8, options, 100, 8, false);
        var fade = timeline.AdvanceAt(9, options, 100, 9, false);
        if (!fade.FadeStarted || fade.FadeSeconds != 1)
            throw new Exception("Fade-out did not start before the maximum.");
        if (timeline.AdvanceAt(9.5, options, 100, 9.5, false).Opacity != 0.5 || !timeline.AdvanceAt(10, options, 100, 10, false).Complete)
            throw new Exception("Video fade did not finish at its deadline.");
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
