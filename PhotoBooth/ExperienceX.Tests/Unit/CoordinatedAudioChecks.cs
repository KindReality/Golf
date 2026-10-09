using System.Text;
using ExperienceX;
using NAudio.Wave;

internal static class CoordinatedAudioChecks
{
    public static void Run()
    {
        AudioOutput[] devices = [new("id-a", "Front, Left", 2), new("id-b", "Rear", 2), new("id-c", "Side", 8)];
        var options = new ExperienceOptions
        {
            DefaultAudioDevice = "Room",
            VirtualAudioDevices = new() { ["Room"] = ["id-a", "Rear", "missing"] },
            AudioDeviceLatencyMilliseconds = new() { ["Rear"] = 20, ["id-b"] = 30 }
        };
        options.Validate();
        var selected = AudioDeviceSelection.Resolve(new()
        {
            AudioDevices = ["room", "id-a", "Side"]
        }, options, devices, "id-c");
        if (!selected.Devices.Select(device => device.Id).SequenceEqual(new[] { "id-a", "id-b", "id-c" }) ||
            !selected.Unavailable.SequenceEqual(new[] { "missing" }))
            throw new Exception("Virtual expansion, physical ID deduplication or partial output selection failed.");
        if (AudioDeviceSelection.Resolve(new()
        {
            AudioDevice = "id-a, Rear"
        }, options, devices, "id-c").Devices.Length != 2 ||
            AudioDeviceSelection.Resolve(new()
            {
                AudioDevice = "Front, Left"
            }, options, devices, "id-c").Devices.Single().Id != "id-a" ||
            AudioDeviceSelection.Resolve(new(), options, devices, "id-c").Devices.Length != 2 ||
            AudioDeviceSelection.LatencySeconds(devices[1], options) != 0.03)
            throw new Exception("Comma lists, literal comma names, default group or latency ID precedence failed.");
        var reloaded = ExperienceOptions.Parse("{\"VirtualAudioDevices\":{\"Room\":[\"id-c\"]},\"DefaultAudioDevice\":\"Room\"}");
        if (AudioDeviceSelection.Resolve(new(), reloaded, devices, null).Devices.Single().Id != "id-c")
            throw new Exception("Reloaded virtual device membership was not used.");
        foreach (var json in new[] {
            "{\"VirtualAudioDevices\":null}", "{\"VirtualAudioDevices\":{\"Room\":[]}}",
            "{\"VirtualAudioDevices\":{\"Room\":[null]}}", "{\"VirtualAudioDevices\":{\"Room\":[\"Room\"]}}",
            "{\"VirtualAudioDevices\":{\"Room\":[\"id-a\"],\" room \":[\"id-b\"]}}",
            "{\"AudioDeviceLatencyMilliseconds\":{\"id-a\":-1}}", "{\"AudioDeviceLatencyMilliseconds\":{\"id-a\":5001}}" })
            Reject(() => ExperienceOptions.Parse(json));
        foreach (var fields in new[] { "\"audioDevices\":[]", "\"audioDevices\":[null]", "\"audioDevices\":[\"\"]",
            "\"audioDevices\":[\"id-a\"],\"audioDevice\":\"id-b\"", "\"audioDevices\":\"id-a\"" })
            Reject(() => ExperienceRequest.Parse(Encoding.UTF8.GetBytes("{\"experience\":\"audio\",\"value\":\"test.wav\"," + fields + "}")));
        Reject(() => AudioDeviceSelection.Resolve(new() { AudioDevice = "Rear,,Side" }, options, devices, null));
        Reject(() => AudioDeviceSelection.Resolve(new()
        {
            AudioDevice = "Room"
        }, options,
            [new("collision", "Room", 2)], null));

        // Different device starts, sample rates, and drifting clocks must follow one wall-clock timeline.
        foreach (var rate in new[] { 44100, 48000, 96000 })
            foreach (var ppm in new[] { -250.0, 0, 250 })
            {
                var mapping = new AudioClockMapping(rate, (ulong)rate);
                var cursor = new AudioSynchronizationCursor();
                var actualRate = rate * (1 + ppm / 1_000_000);
                var deviceStart = 100.037;
                var commonStart = 100.4;
                var queued = (long)(rate * 0.05);
                const int sourceRate = 48000;
                double worst = 0;
                for (var stepIndex = 1; stepIndex < 6000; stepIndex++)
                {
                    var time = deviceStart + stepIndex * 0.01;
                    var played = (ulong)((time - deviceStart) * actualRate);
                    if (!mapping.Observe(played, (ulong)(time * 10_000_000)))
                        throw new Exception("Valid clock observation rejected.");
                    var desired = (mapping.PresentationTime(queued) - commonStart) * sourceRate;
                    var step = cursor.Plan(desired, sourceRate, sourceRate / mapping.Rate);
                    if (stepIndex > 100)
                        worst = Math.Max(worst, Math.Abs(cursor.ErrorSeconds));
                    var frames = (int)Math.Round(actualRate * 0.01);
                    for (var frame = 0; frame < frames; frame++)
                        cursor.Advance(step);
                    queued += frames;
                }
                if (worst > 0.002 || Math.Abs(mapping.Rate / actualRate - 1) > 0.0001)
                    throw new Exception($"Clock drift servo failed at {rate} Hz / {ppm} ppm: {worst * 1000:F3} ms.");
            }
        var invalidClock = new AudioClockMapping(48000, 48000);
        if (invalidClock.Observe(0, 1) || !invalidClock.Observe(4800, 1_000_000) || invalidClock.Observe(4000, 900_000))
            throw new Exception("Invalid or nonmonotonic clock observations were accepted.");

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = new float[48000 * 2 * 6];
                for (var frame = 0; frame < samples.Length / 2; frame++)
                {
                    samples[frame * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * frame / 48000));
                    samples[frame * 2 + 1] = 0.25f;
                }
                writer.WriteSamples(samples, 0, samples.Length);
            }
            using var source = new SharedAudioSource(path, 1);
            var resampler = new AudioResampler(48000, 44100);
            var channels = new float[2];
            source.Ensure(12000);
            for (var frame = 1000; frame < 11000; frame += 17)
            {
                var position = frame + 0.37;
                resampler.Read(source, position, channels);
                if (Math.Abs(channels[0] - 0.5 * Math.Sin(2 * Math.PI * 1000 * position / 48000)) > 0.001 ||
                    Math.Abs(channels[1] - 0.25) > 0.00001)
                    throw new Exception("Shared band-limited resampling changed waveform/channel levels.");
            }
            source.Ensure(200000);
            Reject(() => source.Sample(1000, 0));
            source.Ensure(1000000);
            if (Math.Abs(source.DurationSeconds - 6) > 1e-9)
                throw new Exception("Shared decoder EOF timing changed.");
        }
        finally { File.Delete(path); }
        Console.WriteLine("Passed coordinated device/group selection, reloads, malformed requests, missing outputs, latency, 60-second drift simulations, and bounded shared resampling.");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException) { return; }
        throw new Exception("Invalid coordinated audio input was accepted.");
    }
}
