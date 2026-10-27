using PhotoBooth.Media;

internal static class MediaCacheChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "MediaCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second"); var local = Path.Combine(root, "local");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(local);
            var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            async Task Write(string directory, string name, string content, int seconds)
            {
                var path = MediaLibrary.Resolve(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content); File.SetLastWriteTimeUtc(path, epoch.AddSeconds(seconds));
            }
            await Write(first, "Ghosts/Eye.mp4", "original", 1);
            await Write(first, "Witches/Eye.mp4", "witch", 2);
            Directory.CreateDirectory(Path.Combine(first, "Empty"));
            await Write(local, "local-only.mp4", "retain", 100);
            var cache = new MediaCache();
            try { await cache.SynchronizeAsync("missing", Path.Combine(root, "missing"), local, default); throw new Exception("Missing share accepted."); }
            catch (DirectoryNotFoundException) { }
            Check(await cache.SynchronizeAsync("one", first, local, default), "Initial media copy succeeds.");
            Check(File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "original" && File.ReadAllText(Path.Combine(local, "Witches/Eye.mp4")) == "witch" && Directory.Exists(Path.Combine(local, "Empty")),
                "Category paths and empty folders survive copying.");
            Check(File.GetLastWriteTimeUtc(Path.Combine(local, "Ghosts/Eye.mp4")) == epoch.AddSeconds(1), "Copied modification times are preserved exactly.");

            await Write(second, "Ghosts/Eye.mp4", "newer from another controller", 3);
            await cache.SynchronizeAsync("two", second, local, default);
            Check(File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "newer from another controller", "A newer peer file replaces a copy regardless of Controller ownership.");
            await cache.SynchronizeAsync("one", first, local, default);
            Check(File.GetLastWriteTimeUtc(Path.Combine(local, "Ghosts/Eye.mp4")) == epoch.AddSeconds(3), "An older Controller copy cannot overwrite a newer local file.");
            await cache.SynchronizeAsync("local", local, first, default);
            Check(File.ReadAllText(Path.Combine(first, "Ghosts/Eye.mp4")) == "newer from another controller", "Newer files propagate back to their original Controller.");

            await Write(local, "Ghosts/Eye.mp4", "local edit", 5);
            await Write(second, "Ghosts/Eye.mp4", "remote older", 4);
            await cache.SynchronizeAsync("two", second, local, default);
            Check(File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "local edit", "Newer local edits survive synchronization.");
            await Write(second, "Ghosts/Eye.mp4", "different length at the same timestamp", 5);
            Check(!await cache.SynchronizeAsync("two", second, local, default) && File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "local edit",
                "Equal timestamps with different sizes report an incomplete conflict without overwriting.");
            await Write(second, "Ghosts/Eye.mp4", "same bytes", 5); // Same length as 'local edit': metadata is intentionally the only comparison.
            Check(await cache.SynchronizeAsync("two", second, local, default) && File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "local edit",
                "Matching size and timestamp skips content reads under the agreed metadata-only rule.");
            await Write(second, "Ghosts/Eye.mp4", "latest", 6);
            using (var locked = new FileStream(Path.Combine(local, "Ghosts/Eye.mp4"), FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!await cache.SynchronizeAsync("two", second, local, default), "Locked playback files remain intact and are retried.");
            await cache.SynchronizeAsync("two", second, local, default);
            Check(File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "latest" && !Directory.EnumerateFiles(local, "*.download").Any(), "Retry succeeds without leaving partial downloads.");

            var inventory = MediaInventory.Read(second); var revision = MediaInventory.Revision(inventory.Serialize());
            using (var locked = new FileStream(Path.Combine(second, "Ghosts/Eye.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(MediaInventory.Revision(MediaInventory.Read(second).Serialize()) == revision, "Inventory revisions require metadata only, even while media contents are locked.");
            await Write(second, ".in-progress.download", "ignore", 100);
            Check(MediaInventory.Revision(MediaInventory.Read(second).Serialize()) == revision, "Temporary files and cache metadata do not affect revisions.");
            await Write(local, "Ghosts/Eye.mp4", "older local", 5);
            await Write(second, "Ghosts/Eye.mp4", "latest revision", 7);
            Check(MediaInventory.Revision(MediaInventory.Read(second).Serialize()) != revision && !await cache.SynchronizeAsync("two", second, local, default, inventory),
                "A changed inventory invalidates its revision, and stale advertised metadata cannot overwrite a local file.");
            Check(File.ReadAllText(Path.Combine(local, "Ghosts/Eye.mp4")) == "older local", "Failed stale downloads preserve the complete local copy.");
            File.Delete(Path.Combine(second, "Ghosts/Eye.mp4"));
            await cache.SynchronizeAsync("two", second, local, default);
            Check(File.Exists(Path.Combine(local, "Ghosts/Eye.mp4")) && File.Exists(Path.Combine(local, "local-only.mp4")), "Remote deletions never remove local media or extras.");
            await cache.SynchronizeAsync("local", local, second, default);
            Check(File.Exists(Path.Combine(second, "Ghosts/Eye.mp4")), "Without deletion records another Controller can restore a deleted file.");
            var parsed = MediaInventory.Parse(MediaInventory.Read(local).Serialize());
            Check(parsed.Files.Length == 3, "Media inventories round-trip with all category paths.");
            try { (parsed with { Files = [new("../escape.mp4", 1, epoch)] }).Validate(); throw new Exception("Unsafe inventory accepted."); }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Passed metadata revisions, newer-file synchronization, conflicts, retry, bilateral propagation and no deletion.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
