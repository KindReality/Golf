using ExperienceX;
using PhotoBooth.Networking;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

internal static class ReliabilityChecks
{
    public static void Run()
    {
        var cache = new RequestDeduplicator(3);
        var start = Stopwatch.GetTimestamp();
        if (!cache.Accept("sender", "a", 60, start) || cache.Accept("sender", "a", 60, start + 1) || !cache.Accept("other", "a", 60, start + 1))
            throw new Exception("Request ID sender isolation/deduplication failed.");
        cache.Accept("sender", "b", 60, start + 2);
        cache.Accept("sender", "c", 60, start + 3);
        if (cache.Count != 3 || !cache.Accept("sender", "a", 60, start + 4))
            throw new Exception("Deduplication cache did not remain bounded.");
        if (!cache.Accept("sender", "a", 60, start + 61 * Stopwatch.Frequency) || !cache.Accept("sender", "a", 0, start))
            throw new Exception("Deduplication expiration/disable failed.");
        var mailbox = new RendererMailbox();
        int value = -1, replaced = 0;
        for (int i = 0; i < 10000; i++)
        {
            var next = i;
            mailbox.Post("Preview", () => value = next, () => replaced++);
        }
        if (mailbox.Count != 1 || replaced != 9999 || !mailbox.TryTake(out var latest))
            throw new Exception("Preview flood did not coalesce.");
        latest();
        if (value != 9999)
            throw new Exception("Latest preview was not selected.");
        for (int i = 0; i < RendererMailbox.Capacity; i++)
            mailbox.Post("Category" + i, () => { }, () => replaced++);
        mailbox.Close();
        if (mailbox.Count != 0 || replaced != 9999 + RendererMailbox.Capacity)
            throw new Exception("Closing a mailbox stranded pending confirmations.");
        var recovery = new RendererRecovery();
        var now = DateTimeOffset.UtcNow;
        foreach (var seconds in new[] { 1, 2, 5, 10, 30, 30 })
        {
            recovery.Failed(now);
            if (recovery.Due(now.AddSeconds(seconds - 0.1)) || !recovery.Due(now.AddSeconds(seconds)))
                throw new Exception("Recovery retry backoff failed.");
            recovery.Begin();
        }
        recovery.Ready();
        if (recovery.Attempts != 0 || recovery.Due(now))
            throw new Exception("Recovery did not reset after success.");
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var store = new KeystoneConfigurationStore(path);
            store.EnsureDisplays(["DISPLAY1"]);
            var before = File.ReadAllText(path);
            store.EnsureDisplays(["DISPLAY1", "DISPLAY2"]);
            if (File.ReadAllText(path + ".bak").Trim() != before.Trim())
                throw new Exception("Atomic replacement did not preserve the previous valid configuration.");
            File.WriteAllText(path, "{");
            if (!store.RecoverAtStartup())
                throw new Exception("Startup did not recover a corrupted configuration.");
            if (ExperienceOptions.Parse(File.ReadAllText(path)).DisplayKeystones.Count != 1 || Directory.GetFiles(folder, "*.rejected-*").Length != 1)
                throw new Exception("Backup recovery did not retain the rejected file.");
            File.WriteAllText(path, " ");
            try
            {
                store.EnsureDisplays(["DISPLAY1"]);
                throw new Exception("An empty in-progress external file was overwritten.");
            }
            catch (JsonException) { }
            if (File.ReadAllText(path) != " ")
                throw new Exception("Invalid live configuration was modified.");
            if (Directory.GetFiles(folder, "*.tmp").Length != 0)
                throw new Exception("Atomic save left temporary files.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed request deduplication, command coalescing, recovery backoff and atomic configuration checks.");
    }
}
