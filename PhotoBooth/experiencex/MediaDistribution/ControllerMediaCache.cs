using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Root files only. Copies become visible atomically; source files and local extras are never deleted.
internal sealed class ControllerMediaCache
{
    private sealed record Origin(string Hostname, long Length, DateTime LastWriteUtc, string Hash);
    private const string StateFile = ".controller-media-state.json";
    public async Task<bool> SynchronizeAsync(string hostname, string share, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        var statePath = Path.Combine(destination, StateFile);
        var origins = new Dictionary<string, Origin>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(statePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, Origin>>(await File.ReadAllTextAsync(statePath, token));
                if (saved is not null)
                    foreach (var entry in saved)
                    {
                        if (entry.Value is null || string.IsNullOrWhiteSpace(entry.Value.Hostname) || entry.Value.Length < 0 ||
                            entry.Value.Hash is not { Length: 64 } hash || hash.Any(c => !Uri.IsHexDigit(c)))
                        {
                            Telemetry.Warning("MediaCacheStateInvalid", new { destination, Filename = entry.Key, Reason = "Invalid cached file ownership metadata." });
                            continue;
                        }
                        origins[entry.Key] = entry.Value;
                    }
            }
            catch (JsonException ex) { Telemetry.Warning("MediaCacheStateInvalid", new { destination, Reason = ex.Message }); }
        }
        bool complete = true;
        // SMB enumeration may block in the OS; the caller owns a background worker, never the UI thread.
        foreach (var source in Directory.EnumerateFiles(share, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var name = info.Name;
            if (name == StateFile) continue;
            var target = Path.Combine(destination, name);
            var temporary = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".download");
            try
            {
                var sourceLength = info.Length;
                var sourceDate = info.LastWriteTimeUtc;
                origins.TryGetValue(name, out var origin);
                if (File.Exists(target) && origin is not null && origin.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
                    origin.Length == sourceLength && origin.LastWriteUtc == sourceDate && new FileInfo(target).Length == sourceLength)
                    continue;
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 1024 * 1024, token);
                    await output.FlushAsync(token);
                }
                info.Refresh();
                if (info.Length != sourceLength || info.LastWriteTimeUtc != sourceDate || new FileInfo(temporary).Length != sourceLength)
                    throw new IOException("Media changed while it was being copied; retain the previous local file.");
                var hash = await HashAsync(temporary, token);
                if (File.Exists(target))
                {
                    var existingHash = await HashAsync(target, token);
                    bool sameOwner = origin is not null && origin.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) && existingHash == origin.Hash;
                    if (existingHash != hash && !sameOwner)
                    {
                        Telemetry.Warning("MediaFilenameConflict", new { Hostname = hostname, Filename = name, destination, ExistingController = origin?.Hostname, Action = "KeepFirst" });
                        continue;
                    }
                    // Identical files from another controller do not transfer ownership of the filename.
                    if (existingHash == hash && origin is not null && !origin.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase)) continue;
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, true);
                File.SetLastWriteTimeUtc(target, sourceDate);
                origins[name] = new(hostname, sourceLength, sourceDate, hash);
                await SaveStateAsync(statePath, origins, token);
                Telemetry.Info("MediaFileCached", new { Hostname = hostname, Filename = name, Bytes = sourceLength, destination });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                Telemetry.Warning("MediaFileCopyFailed", new { Hostname = hostname, Filename = name, destination, Reason = ex.Message });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return complete;
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
    private static async Task SaveStateAsync(string path, Dictionary<string, Origin> state, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
