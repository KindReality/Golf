using System.Collections.Concurrent;
using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;

internal static class MediaCacheChecks
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
            Directory.CreateDirectory(Path.Combine(first,"Ghosts","Deep"));
            Directory.CreateDirectory(Path.Combine(first,"Witches"));
            await File.WriteAllTextAsync(Path.Combine(first,"Ghosts","Deep","Same.mp4"),"ghost-video");
            await File.WriteAllTextAsync(Path.Combine(first,"Witches","Same.mp4"),"witch-video");
            await File.WriteAllTextAsync(Path.Combine(local, "local-only.mp4"), "retain");
            var cache = new PhotoBooth.Media.MediaCache();
            try { await cache.SynchronizeAsync("missing",Path.Combine(root,"missing-share"),local,default);throw new Exception("An unavailable Controller share was reported as an empty successful sync"); }
            catch(DirectoryNotFoundException) { }
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local,"Ghosts","Deep","Same.mp4"))=="ghost-video"&&File.ReadAllText(Path.Combine(local,"Witches","Same.mp4"))=="witch-video",
                "Nested categories keep duplicate filenames distinct in the local cache");
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "first-version" && File.Exists(Path.Combine(local, "sound.wav")) &&
                File.Exists(Path.Combine(local, "nested", "excluded.mp4")) && !File.Exists(Path.Combine(local, "excluded.mp4")) && File.ReadAllText(Path.Combine(local, "local-only.mp4")) == "retain", "Media folders are preserved locally without flattening; local extras are retained");
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

            await cache.SynchronizeAsync("local-gateway", first, local, default, acceptManagedOwnershipTransfer: true);
            await File.WriteAllTextAsync(Path.Combine(first, "sound.wav"), "gateway-updated");
            await cache.SynchronizeAsync("local-gateway", first, local, default, acceptManagedOwnershipTransfer: true);
            Check(File.ReadAllText(Path.Combine(local, "sound.wav")) == "gateway-updated" && File.ReadAllText(Path.Combine(local, "ping.mp4")) == "local-edit",
                "Moving managed caches behind the local Controller keeps future updates working while preserving local edits");

            await File.WriteAllTextAsync(Path.Combine(local, ".controller-media-state.json"), "{\"ping.mp4\":null,\"sound.wav\":{}}");
            await cache.SynchronizeAsync("host-one", first, local, default);
            Check(File.ReadAllText(Path.Combine(local, "ping.mp4")) == "local-edit", "Invalid ownership metadata cannot crash synchronization or overwrite local media");

        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Passed hierarchical local caching, atomic replacement, ownership conflicts and locked-file retry.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Media discovery check timed out."); await Task.Delay(10); }
    }
}
