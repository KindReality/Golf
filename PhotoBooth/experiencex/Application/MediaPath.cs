using System.IO;

namespace ExperienceX;

public static class MediaPath
{
    // Relative settings belong to the user's local application profile, never the executable/OneDrive.
    public static string ResolveDirectory(string directory, string? localApplicationData = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A local media directory is required.");
        directory = Environment.ExpandEnvironmentVariables(directory);
        if (directory.Contains("://", StringComparison.Ordinal) ||
            (Path.IsPathRooted(directory) && !Path.IsPathFullyQualified(directory)))
            throw new ArgumentException("Media directories must be local filesystem paths.");
        var localRoot = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new InvalidOperationException("The local application data folder is unavailable.");
        var profile = Path.Combine(localRoot, "Home Technologies", "ExperienceX");
        var root = Path.GetFullPath(directory, profile);
        EnsureLocalPath(root);
        return root;
    }

    public static string Resolve(string directory, string value, string? localApplicationData = null)
    {
        var root = ResolveDirectory(directory, localApplicationData);
        var path = PhotoBooth.Media.MediaLibrary.Resolve(root, value);
        EnsureLocalPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file is unavailable.", path);
        return path;
    }

    internal static void EnsureLocalPath(string path, int linkDepth = 0)
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
            EnsureLocalPath(target.FullName, linkDepth + 1);
        }
    }
}
