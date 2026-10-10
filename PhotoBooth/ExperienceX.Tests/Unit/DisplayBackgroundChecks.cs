using ExperienceX;

internal static class DisplayBackgroundChecks
{
    public static void Run()
    {
        const string display = @"\\.\DISPLAY2";
        var options = ExperienceOptions.Parse("""
            {"DisplayAliases":{"Projector":"\\\\.\\DISPLAY2"},"DisplayBackgrounds":{" projector ":"#1234aB"}}
            """);
        var color = DisplayBackground.For(display, options);
        if (Math.Abs(color.R - 18 / 255f) > 0.0001 || Math.Abs(color.G - 52 / 255f) > 0.0001 || Math.Abs(color.B - 171 / 255f) > 0.0001 || color.A != 1)
            throw new Exception("Display background alias or hex color did not resolve.");
        if (DisplayBackground.For(@"\\.\DISPLAY1", options).A != 1 || DisplayBackground.For(display, new ExperienceOptions()).A != 1)
            throw new Exception("Unconfigured display did not default to opaque black.");
        options = options with
        {
            DisplayBackgrounds = new()
            {
                [display.ToLowerInvariant()] = new()
                {
                    BackgroundColor = "#1234AB",
                    IsTransparent = true
                }
            }
        };
        options.Validate();
        if (DisplayBackground.For(display, options).A != 0)
            throw new Exception("Explicit transparent display background was not transparent.");
        if (options.DisplayBackgrounds.Single().Value.BackgroundColor != "#1234AB")
            throw new Exception("Transparency changed the stored background color.");
        options = ExperienceOptions.Parse("""{"DisplayBackgrounds":{"\\\\.\\DISPLAY2":{"BackgroundColor":"#1234AB","IsTransparent":false}}}""");
        if (Math.Abs(DisplayBackground.For(display, options).G - 52 / 255f) > 0.0001)
            throw new Exception("Separate background color configuration failed.");
        foreach (var json in new[]
        {
            """{"DisplayBackgrounds":null}""",
            """{"DisplayBackgrounds":{"Unknown":"#000000"}}""",
            """{"DisplayBackgrounds":{"\\\\.\\DISPLAY2":null}}""",
            """{"DisplayBackgrounds":{"\\\\.\\DISPLAY2":"#00112233"}}""",
            """{"DisplayBackgrounds":{"\\\\.\\DISPLAY2":"#GG1122"}}""",
            """{"DisplayBackgrounds":{"\\\\.\\DISPLAY2":{"BackgroundColor":"transparent","IsTransparent":true}}}""",
            """{"DisplayAliases":{"Projector":"\\\\.\\DISPLAY2"},"DisplayBackgrounds":{"Projector":"transparent","\\\\.\\DISPLAY2":"#000000"}}"""
        })
        {
            try
            {
                ExperienceOptions.Parse(json);
            }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid or ambiguous display background configuration was accepted.");
        }
        Console.WriteLine("Passed display background defaults, alias/device selection, color parsing and invalid/ambiguous configuration checks.");
    }
}
