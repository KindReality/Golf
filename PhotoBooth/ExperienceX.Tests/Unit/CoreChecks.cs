using ExperienceX;

internal static class CoreChecks
{
    public static void Run()
    {
        static void Near(ScreenPoint actual, ScreenPoint expected)
        {
            if (Math.Abs(actual.X - expected.X) > 1e-9 || Math.Abs(actual.Y - expected.Y) > 1e-9)
                throw new Exception($"Expected {expected}, got {actual}");
        }

        static void Reject(PointOptions options)
        {
            try
            {
                options.Validate();
            }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid corners were accepted.");
        }

        var identity = new PointOptions();
        identity.Validate();
        for (var y = 0; y <= 10; y++)
            for (var x = 0; x <= 10; x++)
                Near(identity.Map(x / 10.0, y / 10.0), new(x / 10.0, y / 10.0));

        var trapezoid = identity with
        {
            TopLeft = new(0.2, 0),
            TopRight = new(0.8, 0)
        };
        trapezoid.Validate();
        Near(trapezoid.Map(0, 0), trapezoid.TopLeft);
        Near(trapezoid.Map(1, 0), trapezoid.TopRight);
        Near(trapezoid.Map(1, 1), trapezoid.BottomRight);
        Near(trapezoid.Map(0, 1), trapezoid.BottomLeft);
        Near(trapezoid.Map(0.5, 0.5), new(0.5, 0.375));
        // A diagonal must stay straight under the projective transform.
        for (var i = 0; i <= 10; i++)
        {
            var p = trapezoid.Map(i / 10.0, i / 10.0);
            if (Math.Abs((p.X - 0.2) - 0.8 * p.Y) > 1e-9)
                throw new Exception("Bent diagonal.");
        }
        Reject(identity with
        {
            TopLeft = new(double.NaN, 0)
        });
        Reject(identity with
        {
            TopLeft = new(-0.1, 0)
        });
        Reject(identity with
        {
            TopLeft = null!
        });
        Reject(identity with
        {
            TopRight = identity.BottomLeft,
            BottomLeft = identity.TopRight
        });
        Reject(identity with
        {
            TopRight = identity.TopLeft
        });
        Reject(identity with
        {
            TopRight = new(0.1, 0.9)
        });
        Console.WriteLine("Passed identity, perspective corner/center, straight-line, and invalid-corner checks.");
        var defaults = new ExperienceOptions();
        defaults.Validate();
        string[] displays = ["DISPLAY1", "DISPLAY2"];
        if (DisplaySelection.Select(" display2 ", false, displays, "DISPLAY1") != "DISPLAY2")
            throw new Exception("Assigned selection failed.");
        if (DisplaySelection.Select("DISPLAY3", false, displays, "DISPLAY1") is not null)
            throw new Exception("Production fell back.");
        if (DisplaySelection.Select("DISPLAY2", false, ["DISPLAY1"], "DISPLAY1") is not null)
            throw new Exception("Disconnect fell back.");
        if (DisplaySelection.Select("DISPLAY2", false, displays, "DISPLAY1") != "DISPLAY2")
            throw new Exception("Reconnect failed.");
        if (DisplaySelection.Select("DISPLAY3", true, displays, "DISPLAY1") != "DISPLAY1")
            throw new Exception("Developer fallback failed.");
        if (DisplaySelection.Select(null, false, displays, "DISPLAY1") != "DISPLAY1")
            throw new Exception("Default primary failed.");
        if (DisplaySelection.Select("", false, [], null) is not null)
            throw new Exception("Empty display list failed.");
        Console.WriteLine("Passed production/developer display and disconnect/reconnect checks.");
        var beforeFade = PlaybackTiming.Evaluate(100, 50, 1);
        if (beforeFade.ShouldFade || beforeFade.RemainingSeconds != 50) throw new Exception("Video faded before its natural end.");
        var naturalFade = PlaybackTiming.Evaluate(100, 99, 1);
        if (!naturalFade.ShouldFade || naturalFade.FadeSeconds != 1 || naturalFade.StopReason != "MediaEnded") throw new Exception("Natural end scheduling failed.");
        var shortVideo = PlaybackTiming.Evaluate(0.5, 0, 5);
        if (!shortVideo.ShouldFade || shortVideo.FadeSeconds != 0.5) throw new Exception("Short video fade not shortened.");
        var unknownDuration = PlaybackTiming.Evaluate(null, 100000, 1);
        if (unknownDuration.ShouldFade || !double.IsPositiveInfinity(unknownDuration.RemainingSeconds)) throw new Exception("Unknown-duration video stopped prematurely.");
        if (PlaybackTiming.Evaluate(100, 99, 0).ShouldFade || !PlaybackTiming.Evaluate(100, 100, 0).ShouldFade) throw new Exception("Zero fade timing failed.");
        Console.WriteLine("Passed natural-end, long-running video, unknown-duration, short-video, and zero-fade checks.");
        new BlackKeyOptions().Validate();
        foreach (var invalidKey in new[] { new BlackKeyOptions { Threshold = -1 }, new BlackKeyOptions { Softness = double.NaN },
    new BlackKeyOptions { Threshold = 0.9, Softness = 0.2 } })
        {
            try
            {
                invalidKey.Validate();
            }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid black key options accepted.");
        }
        Console.WriteLine("Passed black key configuration checks.");
        var migrated = ExperienceOptions.Parse("{\"DisplayName\":\"DISPLAY2\"}");
        if (migrated.DefaultDisplayName != "DISPLAY2")
            throw new Exception("Legacy configuration migration failed.");
        var renamed = ExperienceOptions.Parse("{\"DisplayName\":\"OLD\",\"DefaultDisplayName\":\"NEW\",\"AudioTransitionInSeconds\":0.2}");
        if (renamed.DefaultDisplayName != "NEW" || renamed.AudioTransitionInSeconds != 0.2)
            throw new Exception("Independent configuration settings failed.");
        if (OutputSelection.Display(null, "DISPLAY2", displays, "DISPLAY1") != "DISPLAY2" ||
            OutputSelection.Display("DISPLAY1", "DISPLAY2", displays, "DISPLAY1") != "DISPLAY1" ||
            OutputSelection.Display(null, "MISSING", displays, "DISPLAY1") is not null)
            throw new Exception("Display command/default routing failed.");
        AudioOutput[] audioDevices = [new("id1", "Speakers", 2), new("id2", "USB", 8)];
        var aliases = new Dictionary<string, string> { ["Left"] = "DISPLAY2", ["Center"] = "DISPLAY1", ["Projector"] = "DISPLAY3" };
        if (OutputSelection.Display(" left ", null, displays, "DISPLAY1", aliases) != "DISPLAY2" ||
            OutputSelection.Display(null, "CENTER", displays, "DISPLAY2", aliases) != "DISPLAY1" ||
            OutputSelection.Display("Projector", "Center", displays, "DISPLAY1", aliases) is not null ||
            OutputSelection.Display("Unknown", "Center", displays, "DISPLAY1", aliases) is not null ||
            OutputSelection.Display("DISPLAY2", "Center", displays, "DISPLAY1", aliases) != "DISPLAY2")
            throw new Exception("Display aliases/defaults/raw names/unavailable output routing failed.");
        var aliasConfig = ExperienceOptions.Parse("{\"DefaultDisplayName\":\"Left\",\"DisplayAliases\":{\"Left\":\"DISPLAY2\"}}");
        if (OutputSelection.Display(null, aliasConfig.DefaultDisplayName, displays, "DISPLAY1", aliasConfig.DisplayAliases) != "DISPLAY2")
            throw new Exception("Display alias configuration was not loaded.");
        foreach (var badAliases in new[] { "null", "{\"Left\":\"\"}", "{\"Left\":null}", "{\"Left\":\"DISPLAY1\",\" left \":\"DISPLAY2\"}" })
        {
            try
            {
                ExperienceOptions.Parse("{\"DisplayAliases\":" + badAliases + "}");
            }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid display aliases accepted.");
        }
        Console.WriteLine("Passed display aliases, default aliases, raw device names, unavailable targets and invalid alias configuration checks.");
        if (OutputSelection.Audio(null, "USB", audioDevices, "id1")?.Id != "id2" ||
            OutputSelection.Audio("id1", "USB", audioDevices, "id2")?.Id != "id1" ||
            OutputSelection.Audio(null, "", audioDevices, "id1")?.Id != "id1" ||
            OutputSelection.Audio(null, "MISSING", audioDevices, "id1") is not null ||
            OutputSelection.Audio("Speakers", null, [new("a", "Speakers", 2), new("b", "Speakers", 2)], "a") is not null)
            throw new Exception("Audio device routing/failure/ambiguity checks failed.");
        var request = ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes("{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":2,\"mute\":true}"));
        if (request.Channel != 2 || !request.Mute || string.IsNullOrEmpty(request.RequestId))
            throw new Exception("Audio request parse failed.");
        foreach (var invalidRequest in new[] {
    "{\"experience\":\"stop\"}",
    "{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":0}",
    "{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":\"left\"}",
    "{\"experience\":\"video\",\"value\":\"test.mp4\",\"mute\":\"true\"}" })
        {
            try
            {
                ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes(invalidRequest));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { continue; }
            throw new Exception("Invalid or removed command accepted.");
        }
        float[] routed = new float[8];
        AudioSamples.Route(new float[] { 0.2f, 0.6f }, routed, 8);
        if (Math.Abs(routed[7] - 0.4) > 1e-6 || routed.Take(7).Any(v => v != 0))
            throw new Exception("Multichannel isolation failed.");
        AudioSamples.Route(new float[] { 0.2f, 0.6f }, routed, null);
        if (routed[0] != 0.2f || routed[1] != 0.6f || routed.Skip(2).Any(v => v != 0))
            throw new Exception("Normal layout failed.");
        if (AudioSamples.Gain(0, 10, 5, 1) != 0 || AudioSamples.Gain(5, 10, 5, 1) != 1 ||
            AudioSamples.Gain(10, 10, 5, 1) != 0 || AudioSamples.Gain(9.5, 10, 5, 1) >= 1)
            throw new Exception("Audio fades failed.");
        var provider = new RoutedAudioProvider(new TestSamples(), 8, 8, false, 0.01,
            defaults with
            {
                AudioTransitionInSeconds = 0,
                AudioTransitionOutSeconds = 0
            });
        var sampleBuffer = new float[8000];
        var samplesRead = provider.Read(sampleBuffer, 0, sampleBuffer.Length);
        if (samplesRead != 480 * 8 || provider.Read(sampleBuffer, 0, sampleBuffer.Length) != 0 ||
            Math.Abs(provider.PositionSeconds - 0.01) > 1e-9)
            throw new Exception("Sample-accurate natural audio end failed.");
        for (var i = 0; i < samplesRead; i++)
            if (sampleBuffer[i] != (i % 8 == 7 ? 0.5f : 0))
                throw new Exception("Output provider leaked to another channel.");
        var mutedProvider = new RoutedAudioProvider(new TestSamples(), 2, 1, true, 1, defaults);
        if (mutedProvider.Read(sampleBuffer, 0, 100) != 100 || sampleBuffer.Take(100).Any(v => v != 0))
            throw new Exception("Mute failed.");
        var naturalEndProvider = new RoutedAudioProvider(new TestSamples(), 2, null, false, 0.005, defaults);
        if (naturalEndProvider.Read(sampleBuffer, 0, sampleBuffer.Length) != 240 * 2)
            throw new Exception("Natural audio deadline failed.");
        Console.WriteLine("Passed default/explicit output routing, configuration migration, command validation, numbered channels, mute, audio fades and sample-accurate natural ends.");

        // Recover known source points through the same inverse transform used by the GPU shader.
        foreach (var warp in new[] {
    new PointOptions { Enabled=true },
    new PointOptions { Enabled=true,TopLeft=new(0.2,0.1),TopRight=new(0.85,0.05),BottomRight=new(0.9,0.95),BottomLeft=new(0.03,0.8) }
})
        {
            warp.Validate();
            var (r0, r1, r2) = VideoTransform.Inverse(warp);
            for (int y = 0; y <= 20; y++)
                for (int x = 0; x <= 20; x++)
                {
                    var mapped = warp.Map(x / 20.0, y / 20.0);
                    var p = new System.Numerics.Vector3((float)mapped.X, (float)mapped.Y, 1);
                    float Dot(System.Numerics.Vector4 r) => System.Numerics.Vector3.Dot(new(r.X, r.Y, r.Z), p);
                    if (Math.Abs(Dot(r0) / Dot(r2) - x / 20.0) > 0.000002 || Math.Abs(Dot(r1) / Dot(r2) - y / 20.0) > 0.000002)
                        throw new Exception("Inverse GPU keystone mapping did not recover source coordinates.");
                }
        }
        Console.WriteLine("DirectX inverse keystone tests passed.");

    }
}

sealed class TestSamples : NAudio.Wave.ISampleProvider
{
    public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Fill(buffer, 0.5f, offset, count);
        return count;
    }
}
