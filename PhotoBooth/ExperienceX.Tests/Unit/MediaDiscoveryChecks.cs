using System.Collections.Concurrent;
using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;

internal static class MediaDiscoveryChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExperienceX-media-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second"); var local = Path.Combine(root, "local");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(local);
            Directory.CreateDirectory(Path.Combine(first, "nested"));
            await File.WriteAllTextAsync(Path.Combine(first, "ping.mp4"), "first-version");
            await File.WriteAllTextAsync(Path.Combine(first, "sound.wav"), "audio-version");
            await File.WriteAllTextAsync(Path.Combine(first, "nested", "excluded.mp4"), "nested");
            await File.WriteAllTextAsync(Path.Combine(local, "local-only.mp4"), "retain");
            var cache = new ControllerMediaCache();
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "first-version" && File.Exists(Path.Combine(local, "sound.wav")) &&
                !File.Exists(Path.Combine(local, "excluded.mp4")) && File.ReadAllText(Path.Combine(local, "local-only.mp4")) == "retain", "Root files copy locally; subfolders and local extras are retained correctly");
            await File.WriteAllTextAsync(Path.Combine(second, "ping.mp4"), "other-controller");
            await File.WriteAllTextAsync(Path.Combine(second, "other.wav"), "other-media");
            await cache.SynchronizeAsync("host-two", second, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "first-version" && File.Exists(Path.Combine(local, "other.wav")), "Controller filename conflicts keep the first file while unrelated files still copy");
            await File.WriteAllTextAsync(Path.Combine(first, "ping.mp4"), "updated-first-version");
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "updated-first-version", "Media updates from the owning Controller replace the cached version");
            await File.WriteAllTextAsync(Path.Combine(local, "ping.mp4"), "local-edit");
            await File.WriteAllTextAsync(Path.Combine(first, "ping.mp4"), "later-remote-version");
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "local-edit", "A locally edited media file is retained on conflict");
            using (var locked = new FileStream(Path.Combine(local, "sound.wav"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await File.WriteAllTextAsync(Path.Combine(first, "sound.wav"), "updated-audio-version");
                Check(!await cache.SynchronizeAsync("host-one", first, local, default), "Locked playback files retain their old copy and report an incomplete sync");
            }
            Check(!Directory.EnumerateFiles(local, "*.download").Any(), "Failed copies leave no partial playback/download files");
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "sound.wav")) == "updated-audio-version", "A previously locked file updates when it is available");

            await File.WriteAllTextAsync(Path.Combine(local, ".controller-media-state.json"), "{\"ping.mp4\":null,\"sound.wav\":{}}");
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "local-edit", "Invalid ownership metadata cannot crash synchronization or overwrite local media");

            var clientOptions = new ExperienceOptions { DeviceName = "experience-client", VideoDirectory = Path.Combine(root, "video"), AudioDirectory = Path.Combine(root, "audio"), MediaDiscovery = new() { Attempts = 1 } };
            var sent = new ConcurrentQueue<BroadcastEnvelope>();
            await using var service = new ControllerMediaDiscovery(() => clientOptions, hostname => hostname == "host-one" ? first : second,
                (bytes, _, _) => { sent.Enqueue(BroadcastEnvelope.Parse(bytes)); return Task.CompletedTask; });
            service.Configure(clientOptions);
            await Until(() => sent.Count == 1);
            var probe = sent.Single();
            Check(ControllerDiscovery.IsRequest(probe) && probe.Device == "*", "Startup discovery is broadcast to every Controller");
            BroadcastEnvelope Reply(string hostname, string id) => BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new() { DeviceName = hostname }, "event", clientOptions.EffectiveDeviceName,
                new { @event = ControllerDiscovery.ResponseEvent, input = "controller", hostname, discoveryRequestId = id }));
            service.Receive(Reply("host-one", "not-our-probe"));
            Check(!Directory.Exists(clientOptions.VideoDirectory), "Unrelated discovery replies cannot start media transfers");
            service.Receive(Reply("host-one", probe.RequestId));
            service.Receive(Reply("host-one", probe.RequestId));
            await Until(() => File.Exists(Path.Combine(clientOptions.VideoDirectory, "sound.wav")) && File.Exists(Path.Combine(clientOptions.AudioDirectory, "sound.wav")));
            service.Receive(Reply("host-two", probe.RequestId));
            await Until(() => File.Exists(Path.Combine(clientOptions.VideoDirectory, "other.wav")) && File.Exists(Path.Combine(clientOptions.AudioDirectory, "other.wav")));
            Check(File.ReadAllText(Path.Combine(clientOptions.VideoDirectory, "ping.mp4")) == "later-remote-version", "Multiple discovered Controllers populate both local audio and video directories without filename races");
            try { ControllerDiscovery.MediaShare(@"host\other-share"); throw new Exception("An injected UNC path was accepted as a hostname."); }
            catch (ArgumentException) { }
            Check(ControllerDiscovery.MediaShare("ALPHA") == @"\\ALPHA\media", "Responses build only the fixed hostname/media UNC share");
            clientOptions = clientOptions with { MediaDiscovery = new() { Enabled = false } };
            service.Configure(clientOptions);
            service.Receive(Reply("new-host", probe.RequestId));
            Check(sent.Count == 1, "Disabling media discovery cancels discovery without sending more requests");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Passed Controller discovery correlation, multiple hosts, root-only local caching, atomic replacement, conflicts, locked files, UNC validation and disable/shutdown.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Media discovery check timed out."); await Task.Delay(10); }
    }
}
