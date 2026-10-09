using Experience;

static void Near(KeystonePoint actual, KeystonePoint expected)
{
    if (Math.Abs(actual.X - expected.X) > 1e-9 || Math.Abs(actual.Y - expected.Y) > 1e-9)
        throw new Exception($"Expected {expected}, got {actual}");
}

static void Reject(KeystoneOptions options)
{
    try { options.Validate(); }
    catch (ArgumentException) { return; }
    throw new Exception("Invalid corners were accepted.");
}

var identity = new KeystoneOptions();
identity.Validate();
for (var y = 0; y <= 10; y++)
for (var x = 0; x <= 10; x++)
    Near(identity.Map(x / 10.0, y / 10.0), new(x / 10.0, y / 10.0));

var trapezoid = identity with { TopLeft = new(0.2, 0), TopRight = new(0.8, 0) };
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
    if (Math.Abs((p.X - 0.2) - 0.8 * p.Y) > 1e-9) throw new Exception("Bent diagonal.");
}
Reject(identity with { TopLeft = new(double.NaN, 0) });
Reject(identity with { TopLeft = new(-0.1, 0) });
Reject(identity with { TopLeft = null! });
Reject(identity with { TopRight = identity.BottomLeft, BottomLeft = identity.TopRight });
Reject(identity with { TopRight = identity.TopLeft });
Reject(identity with { TopRight = new(0.1, 0.9) });
Console.WriteLine("Passed identity, perspective corner/center, straight-line, and invalid-corner checks.");
var defaults = new ExperienceOptions();
defaults.Validate();
if (defaults.MaxVideoPlaybackLength != 10) throw new Exception("Expected ten-second default.");
var configured = System.Text.Json.JsonSerializer.Deserialize<ExperienceOptions>("{\"MaxVideoPlaybackLength\":2.5}",
    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
configured.Validate();
if (configured.MaxVideoPlaybackLength != 2.5) throw new Exception("Configuration key not read.");
foreach (var invalid in new[] { 0.0, -1, double.NaN, double.PositiveInfinity, 86401 })
{
    try { (defaults with { MaxVideoPlaybackLength = invalid }).Validate(); }
    catch (ArgumentException) { continue; }
    throw new Exception("Invalid maximum playback length accepted.");
}
Console.WriteLine("Passed playback length defaults, JSON setting, and validation checks.");
string[] displays = ["DISPLAY1", "DISPLAY2"];
if (MonitorSelection.Select(" display2 ", false, displays, "DISPLAY1") != "DISPLAY2") throw new Exception("Assigned selection failed.");
if (MonitorSelection.Select("DISPLAY3", false, displays, "DISPLAY1") is not null) throw new Exception("Production fell back.");
if (MonitorSelection.Select("DISPLAY2", false, ["DISPLAY1"], "DISPLAY1") is not null) throw new Exception("Disconnect fell back.");
if (MonitorSelection.Select("DISPLAY2", false, displays, "DISPLAY1") != "DISPLAY2") throw new Exception("Reconnect failed.");
if (MonitorSelection.Select("DISPLAY3", true, displays, "DISPLAY1") != "DISPLAY1") throw new Exception("Developer fallback failed.");
if (MonitorSelection.Select(null, false, displays, "DISPLAY1") != "DISPLAY1") throw new Exception("Default primary failed.");
if (MonitorSelection.Select("", false, [], null) is not null) throw new Exception("Empty display list failed.");
var mutexName = "PhotoBooth.Test." + Guid.NewGuid().ToString("N");
using (var first = new Mutex(true, mutexName, out var admitted))
{
    if (!admitted) throw new Exception("First instance rejected.");
    using (var second = new Mutex(true, mutexName, out var duplicateAdmitted))
        if (duplicateAdmitted) throw new Exception("Second instance admitted.");
    first.ReleaseMutex();
}
using (var replacement = new Mutex(true, mutexName, out var restartAdmitted))
{
    if (!restartAdmitted) throw new Exception("Restart rejected.");
    replacement.ReleaseMutex();
}
Console.WriteLine("Passed production/developer monitor, disconnect/reconnect, and single-instance mutex checks.");
var beforeFade = PlaybackTiming.Evaluate(10, 8, 100, 8, 1);
if (beforeFade.ShouldFade || beforeFade.RemainingSeconds != 2) throw new Exception("Fade started early.");
var maximumFade = PlaybackTiming.Evaluate(10, 9, 100, 9, 1);
if (!maximumFade.ShouldFade || maximumFade.FadeSeconds != 1 || maximumFade.StopReason != "MaximumPlaybackLength") throw new Exception("Maximum fade scheduling failed.");
var naturalFade = PlaybackTiming.Evaluate(10, 3, 4, 3, 1);
if (!naturalFade.ShouldFade || naturalFade.StopReason != "MediaEnded") throw new Exception("Natural end scheduling failed.");
var shortVideo = PlaybackTiming.Evaluate(10, 0, 0.5, 0, 5);
if (!shortVideo.ShouldFade || shortVideo.FadeSeconds != 0.5) throw new Exception("Short video fade not shortened.");
if (PlaybackTiming.Evaluate(10, 11, null, 11, 1).RemainingSeconds != 0) throw new Exception("Deadline missed.");
if (PlaybackTiming.Evaluate(10, 9, null, 9, 0).ShouldFade) throw new Exception("Zero fade started early.");
Console.WriteLine("Passed natural/maximum deadline, short-video, and zero-fade checks.");
new BlackKeyOptions().Validate();
foreach (var invalidKey in new[] { new BlackKeyOptions { Threshold = -1 }, new BlackKeyOptions { Softness = double.NaN },
    new BlackKeyOptions { Threshold = 0.9, Softness = 0.2 } })
{
    try { invalidKey.Validate(); }
    catch (ArgumentException) { continue; }
    throw new Exception("Invalid black key options accepted.");
}
Console.WriteLine("Passed black key configuration checks.");
var configTestPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
try
{
    var reader = new ConfigurationFileReader();
    if (reader.Read(configTestPath) != "{}") throw new Exception("Missing configuration default failed.");
    File.WriteAllText(configTestPath, "{\"value\":1}");
    if (reader.Read(configTestPath) != "{\"value\":1}") throw new Exception("Configuration creation not detected.");
    File.WriteAllText(configTestPath, "{\"value\":22}");
    if (reader.Read(configTestPath) != "{\"value\":22}") throw new Exception("Configuration edit not detected.");
    File.Delete(configTestPath);
    if (reader.Read(configTestPath) != "{}") throw new Exception("Configuration deletion not detected.");
}
finally { File.Delete(configTestPath); }
Console.WriteLine("Passed configuration creation, edit, and deletion checks.");

var migrated = ExperienceOptions.Parse("{\"MonitorName\":\"DISPLAY2\",\"maxPlaybackLength\":7}");
if (migrated.DefaultMonitorName != "DISPLAY2" || migrated.MaxVideoPlaybackLength != 7 || migrated.MaxAudioPlaybackLength != 10)
    throw new Exception("Legacy configuration migration failed.");
var renamed = ExperienceOptions.Parse("{\"MonitorName\":\"OLD\",\"DefaultMonitorName\":\"NEW\",\"MaxAudioPlaybackLength\":3,\"AudioTransitionInSeconds\":0.2}");
if (renamed.DefaultMonitorName != "NEW" || renamed.MaxAudioPlaybackLength != 3 || renamed.AudioTransitionInSeconds != 0.2)
    throw new Exception("Independent configuration settings failed.");
foreach (var invalid in new[] { 0.0, -1, double.NaN, 86401 })
{
    try { (defaults with { MaxAudioPlaybackLength = invalid }).Validate(); }
    catch (ArgumentException) { continue; }
    throw new Exception("Invalid audio playback limit accepted.");
}
if (OutputSelection.Monitor(null, "DISPLAY2", displays, "DISPLAY1") != "DISPLAY2" ||
    OutputSelection.Monitor("DISPLAY1", "DISPLAY2", displays, "DISPLAY1") != "DISPLAY1" ||
    OutputSelection.Monitor(null, "MISSING", displays, "DISPLAY1") is not null)
    throw new Exception("Monitor command/default routing failed.");
AudioOutput[] audioDevices = [new("id1", "Speakers", 2), new("id2", "USB", 8)];
var aliases = new Dictionary<string, string> { ["Left"] = "DISPLAY2", ["Center"] = "DISPLAY1", ["Projector"] = "DISPLAY3" };
if (OutputSelection.Monitor(" left ", null, displays, "DISPLAY1", aliases) != "DISPLAY2" ||
    OutputSelection.Monitor(null, "CENTER", displays, "DISPLAY2", aliases) != "DISPLAY1" ||
    OutputSelection.Monitor("Projector", "Center", displays, "DISPLAY1", aliases) is not null ||
    OutputSelection.Monitor("Unknown", "Center", displays, "DISPLAY1", aliases) is not null ||
    OutputSelection.Monitor("DISPLAY2", "Center", displays, "DISPLAY1", aliases) != "DISPLAY2")
    throw new Exception("Monitor aliases/defaults/raw names/unavailable output routing failed.");
var aliasConfig = ExperienceOptions.Parse("{\"DefaultMonitorName\":\"Left\",\"MonitorAliases\":{\"Left\":\"DISPLAY2\"}}");
if (OutputSelection.Monitor(null, aliasConfig.DefaultMonitorName, displays, "DISPLAY1", aliasConfig.MonitorAliases) != "DISPLAY2")
    throw new Exception("Monitor alias configuration was not loaded.");
foreach (var badAliases in new[] { "null", "{\"Left\":\"\"}", "{\"Left\":null}", "{\"Left\":\"DISPLAY1\",\" left \":\"DISPLAY2\"}" })
{
    try { ExperienceOptions.Parse("{\"MonitorAliases\":" + badAliases + "}"); }
    catch (ArgumentException) { continue; }
    throw new Exception("Invalid monitor aliases accepted.");
}
Console.WriteLine("Passed monitor aliases, default aliases, raw device names, unavailable targets and invalid alias configuration checks.");
if (OutputSelection.Audio(null, "USB", audioDevices, "id1")?.Id != "id2" ||
    OutputSelection.Audio("id1", "USB", audioDevices, "id2")?.Id != "id1" ||
    OutputSelection.Audio(null, "", audioDevices, "id1")?.Id != "id1" ||
    OutputSelection.Audio(null, "MISSING", audioDevices, "id1") is not null ||
    OutputSelection.Audio("Speakers", null, [new("a", "Speakers", 2), new("b", "Speakers", 2)], "a") is not null)
    throw new Exception("Audio device routing/failure/ambiguity checks failed.");
var request = ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes("{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":2,\"mute\":true}"));
if (request.Channel != 2 || !request.Mute || string.IsNullOrEmpty(request.RequestId)) throw new Exception("Audio request parse failed.");
foreach (var invalidRequest in new[] {
    "{\"experience\":\"stop\"}",
    "{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":0}",
    "{\"experience\":\"audio\",\"value\":\"test.wav\",\"channel\":\"left\"}",
    "{\"experience\":\"video\",\"value\":\"test.mp4\",\"mute\":\"true\"}" })
{
    try { ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes(invalidRequest)); }
    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { continue; }
    throw new Exception("Invalid or removed command accepted.");
}
float[] routed = new float[8];
AudioSamples.Route(new float[] { 0.2f, 0.6f }, routed, 8);
if (Math.Abs(routed[7] - 0.4) > 1e-6 || routed.Take(7).Any(v => v != 0)) throw new Exception("Multichannel isolation failed.");
AudioSamples.Route(new float[] { 0.2f, 0.6f }, routed, null);
if (routed[0] != 0.2f || routed[1] != 0.6f || routed.Skip(2).Any(v => v != 0)) throw new Exception("Normal layout failed.");
if (AudioSamples.Gain(0, 10, 5, 1) != 0 || AudioSamples.Gain(5, 10, 5, 1) != 1 ||
    AudioSamples.Gain(10, 10, 5, 1) != 0 || AudioSamples.Gain(9.5, 10, 5, 1) >= 1)
    throw new Exception("Audio fades failed.");
var provider = new RoutedAudioProvider(new TestSamples(), 8, 8, false, 20,
    defaults with { MaxAudioPlaybackLength = 0.01, AudioTransitionInSeconds = 0, AudioTransitionOutSeconds = 0 });
var sampleBuffer = new float[8000];
var samplesRead = provider.Read(sampleBuffer, 0, sampleBuffer.Length);
if (samplesRead != 480 * 8 || provider.Read(sampleBuffer, 0, sampleBuffer.Length) != 0 ||
    Math.Abs(provider.PositionSeconds - 0.01) > 1e-9) throw new Exception("Sample-accurate maximum failed.");
for (var i = 0; i < samplesRead; i++)
    if (sampleBuffer[i] != (i % 8 == 7 ? 0.5f : 0)) throw new Exception("Output provider leaked to another channel.");
var mutedProvider = new RoutedAudioProvider(new TestSamples(), 2, 1, true, 1, defaults);
if (mutedProvider.Read(sampleBuffer, 0, 100) != 100 || sampleBuffer.Take(100).Any(v => v != 0)) throw new Exception("Mute failed.");
var naturalEndProvider = new RoutedAudioProvider(new TestSamples(), 2, null, false, 0.005, defaults);
if (naturalEndProvider.Read(sampleBuffer, 0, sampleBuffer.Length) != 240 * 2 || naturalEndProvider.EndReason != "MediaEnded")
    throw new Exception("Natural audio deadline failed.");
Console.WriteLine("Passed default/explicit output routing, configuration migration, command validation, numbered channels, mute, audio fades and sample-accurate limits.");

sealed class TestSamples : NAudio.Wave.ISampleProvider
{
    public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Fill(buffer, 0.5f, offset, count);
        return count;
    }
}
