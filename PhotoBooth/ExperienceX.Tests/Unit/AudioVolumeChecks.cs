using System.Text;
using System.Text.Json;
using ExperienceX;
using NAudio.Wave;

internal static class AudioVolumeChecks
{
    public static void Run()
    {
        static ExperienceRequest Parse(string field) => ExperienceRequest.Parse(
            Encoding.UTF8.GetBytes("{\"experience\":\"audio\",\"value\":\"test.wav\"" + field + "}"));
        if (Parse("").VolumePercent != 100 || Parse(",\"volume\":null").VolumePercent != 100 ||
            Parse(",\"volume\":12.5").VolumePercent != 12.5 || Parse(",\"volume\":0").VolumePercent != 0)
            throw new Exception("Audio request volume/default was not parsed.");
        foreach (var value in new[] { "-0.1", "100.1", "\"50\"", "true", "1e400" })
        {
            try
            {
                Parse(",\"volume\":" + value);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException) { continue; }
            throw new Exception("Invalid request volume accepted: " + value);
        }

        var options = new ExperienceOptions { AudioTransitionInSeconds = 0, AudioTransitionOutSeconds = 0, MaxAudioPlaybackLength = 1 };
        foreach (var volume in new[] { 0.0, 12.5, 50, 100 })
        {
            var provider = new RoutedAudioProvider(new TestSamples(), 2, 2, false, 2, options, volume);
            var buffer = new float[96000];
            if (provider.Read(buffer, 0, buffer.Length) != buffer.Length || provider.PositionSeconds != 1 ||
                provider.Read(buffer, 0, 2) != 0 || provider.EndReason != "MaximumPlaybackLength")
                throw new Exception("Volume changed playback timing or maximum duration.");
            for (var i = 0; i < buffer.Length; i++)
                if (Math.Abs(buffer[i] - (i % 2 == 1 ? 0.5 * volume / 100 : 0)) > 1e-6)
                    throw new Exception("Per-track volume or channel isolation failed.");
        }
        var muted = new RoutedAudioProvider(new TestSamples(), 2, null, true, 1, options, 80);
        var samples = new float[200];
        if (muted.Read(samples, 0, samples.Length) != samples.Length || samples.Any(sample => sample != 0))
            throw new Exception("Mute did not override volume.");
        var fading = new RoutedAudioProvider(new TestSamples(), 2, null, false, 1,
            options with
            {
                AudioTransitionInSeconds = 0.5,
                AudioTransitionOutSeconds = 0.5
            }, 25);
        var fadeBuffer = new float[96000];
        fading.Read(fadeBuffer, 0, fadeBuffer.Length);
        foreach (var frame in new[] { 0, 12000, 24000, 36000, 47999 })
            if (Math.Abs(fadeBuffer[frame * 2] - 0.125 * AudioSamples.Gain(frame / 48000.0, 1, 0.5, 0.5)) > 1e-6)
                throw new Exception("Volume did not multiply both fades.");
        var bytesProvider = new RoutedAudioProvider(new TestSamples(), 2, null, false, 1, options, 25);
        bytesProvider.UpdateOptions(options with
        {
            MaxAudioPlaybackLength = 0.5
        });
        var bytes = new byte[800];
        if (((IWaveProvider)bytesProvider).Read(bytes, 0, bytes.Length) != bytes.Length ||
            BitConverter.ToSingle(bytes) != 0.125f)
            throw new Exception("WASAPI byte path or live option update lost per-track volume.");
        foreach (var volume in new[] { -1.0, 101, double.NaN, double.PositiveInfinity })
        {
            try
            {
                new RoutedAudioProvider(new TestSamples(), 2, null, false, 1, options, volume);
            }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception("Invalid provider volume accepted.");
        }
        Console.WriteLine("Passed audio volume defaults/range, channel isolation, mute, both fades, WASAPI samples, live options, and duration checks.");
    }
}
