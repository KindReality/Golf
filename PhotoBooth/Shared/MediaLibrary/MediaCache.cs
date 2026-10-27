using System.IO;
using PhotoBooth.Diagnostics;

namespace PhotoBooth.Media;

// Newer UTC modification times win regardless of origin. Missing source files never delete local files.
internal sealed class MediaCache
{
    public async Task<bool> SynchronizeAsync(string hostname, string share, string destination, CancellationToken token, MediaInventory? inventory = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(share)) throw new DirectoryNotFoundException("The Controller media share is unavailable.");
        LocalMediaPath.Ensure(destination);
        Directory.CreateDirectory(destination);
        inventory ??= MediaInventory.Read(share);
        inventory.Validate();
        bool complete = true;
        foreach (var folder in inventory.Folders)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var target = MediaLibrary.Resolve(destination, folder);
                LocalMediaPath.Ensure(target);
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                Telemetry.Warning("MediaFileCopyFailed", new { Hostname = hostname, Filename = folder, destination, Reason = ex.Message });
            }
        }
        bool NeedsCopy(string target, MediaFileMetadata file)
        {
            if (!File.Exists(target)) return true;
            var local = new FileInfo(target);
            if (local.LastWriteTimeUtc < file.LastWriteUtc) return true;
            if (local.LastWriteTimeUtc == file.LastWriteUtc && local.Length != file.Length)
            {
                complete = false;
                Telemetry.Warning("MediaFilenameConflict", new { Hostname = hostname, Filename = file.RelativePath, destination,
                    Reason = "Equal modification timestamps but different file sizes.", LocalBytes = local.Length, RemoteBytes = file.Length, file.LastWriteUtc, Action = "KeepLocal" });
            }
            return false;
        }
        foreach (var file in inventory.Files)
        {
            token.ThrowIfCancellationRequested();
            var name = file.RelativePath;
            var target = MediaLibrary.Resolve(destination, name);
            var source = MediaLibrary.Resolve(share, name);
            var temporary = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".download");
            try
            {
                LocalMediaPath.Ensure(target);
                if (!NeedsCopy(target, file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var info = new FileInfo(source);
                void CheckSource()
                {
                    info.Refresh();
                    if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc)
                        throw new IOException("Media changed after its inventory was published; retain the previous local file and retry.");
                }
                CheckSource();
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 1024 * 1024, token);
                    await output.FlushAsync(token);
                }
                CheckSource();
                if (new FileInfo(temporary).Length != file.Length) throw new IOException("The media download is incomplete.");
                token.ThrowIfCancellationRequested();
                LocalMediaPath.Ensure(target);
                if (!NeedsCopy(target, file)) continue; // A newer local edit may have appeared during the download.
                File.SetLastWriteTimeUtc(temporary, file.LastWriteUtc);
                File.Move(temporary, target, true);
                Telemetry.Info("MediaFileCached", new { Hostname = hostname, Filename = name, Bytes = file.Length, file.LastWriteUtc, destination });
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
}
