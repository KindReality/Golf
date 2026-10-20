using System.IO;
namespace PhotoBooth.Media;
internal static class LocalMediaPath
{
    internal static void Ensure(string path, int linkDepth = 0)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new ArgumentException("Network media paths are not allowed. Copy media to a local disk before playback.");
        if (linkDepth >= 32)
            throw new IOException("Media path contains too many redirected links.");

        // Inspect each ancestor too: a local-looking directory may be a link into an SMB share.
        var cursor = Path.GetPathRoot(path)!;
        foreach (var part in path[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo item = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            // Cloud-file placeholders are not symbolic links; the default cache avoids OneDrive entirely.
            if (item.LinkTarget is null) continue;
            var target = item.ResolveLinkTarget(true) ?? throw new IOException("The media link target is unavailable.");
            Ensure(target.FullName, linkDepth + 1);
        }
    }
}
