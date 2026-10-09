using ExperienceX;

internal static class MonitorBackgroundChecks
{
    public static void Run()
    {
        const string monitor = @"\\.\DISPLAY2";
        var options = ExperienceOptions.Parse("""
            {"MonitorAliases":{"Projector":"\\\\.\\DISPLAY2"},"MonitorBackgrounds":{" projector ":"#1234aB"}}
            """);
        var color = MonitorBackground.For(monitor, options);
        if (Math.Abs(color.R - 18 / 255f) > 0.0001 || Math.Abs(color.G - 52 / 255f) > 0.0001 || Math.Abs(color.B - 171 / 255f) > 0.0001 || color.A != 1)
            throw new Exception("Monitor background alias or hex color did not resolve.");
        if (MonitorBackground.For(@"\\.\DISPLAY1", options).A != 1 || MonitorBackground.For(monitor, new ExperienceOptions()).A != 1)
            throw new Exception("Unconfigured monitor did not default to opaque black.");
        options = options with
        {
            MonitorBackgrounds = new()
            {
                [monitor.ToLowerInvariant()] = new()
                {
                    BackgroundColor = "#1234AB",
                    IsTransparent = true
                }
            }
        };
        options.Validate();
        if (MonitorBackground.For(monitor, options).A != 0)
            throw new Exception("Explicit transparent monitor background was not transparent.");
        if (options.MonitorBackgrounds.Single().Value.BackgroundColor != "#1234AB")
            throw new Exception("Transparency changed the stored background color.");
        options = ExperienceOptions.Parse("""{"MonitorBackgrounds":{"\\\\.\\DISPLAY2":{"BackgroundColor":"#1234AB","IsTransparent":false}}}""");
        if (Math.Abs(MonitorBackground.For(monitor, options).G - 52 / 255f) > 0.0001)
            throw new Exception("Separate background color configuration failed.");
        foreach (var json in new[]
        {
            """{"MonitorBackgrounds":null}""",
            """{"MonitorBackgrounds":{"Unknown":"#000000"}}""",
            """{"MonitorBackgrounds":{"\\\\.\\DISPLAY2":null}}""",
            """{"MonitorBackgrounds":{"\\\\.\\DISPLAY2":"#00112233"}}""",
            """{"MonitorBackgrounds":{"\\\\.\\DISPLAY2":"#GG1122"}}""",
            """{"MonitorBackgrounds":{"\\\\.\\DISPLAY2":{"BackgroundColor":"transparent","IsTransparent":true}}}""",
            """{"MonitorAliases":{"Projector":"\\\\.\\DISPLAY2"},"MonitorBackgrounds":{"Projector":"transparent","\\\\.\\DISPLAY2":"#000000"}}"""
        })
        {
            try
            {
                ExperienceOptions.Parse(json);
            }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid or ambiguous monitor background configuration was accepted.");
        }
        Console.WriteLine("Passed monitor background defaults, alias/device selection, color parsing and invalid/ambiguous configuration checks.");
    }
}
