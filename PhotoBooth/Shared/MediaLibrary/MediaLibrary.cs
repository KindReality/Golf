using System.IO;

namespace PhotoBooth.Media;

public sealed record MediaLibraryEntry(string RelativePath, bool IsFolder)
{
    public string Name => Path.GetFileName(RelativePath);
}

// Paths are relative to a library root, with one representation across configuration and the network.
public static class MediaLibrary
{
    public static string Normalize(string path, bool allowRoot = false)
    {
        if (allowRoot && path == "") return "";
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Length > 1024)
            throw new ArgumentException("A relative media path is required.");
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part != part.Trim() ||
            part.EndsWith('.') || part.Any(char.IsControl) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Media paths must stay inside their library without empty or parent segments.");
        return normalized;
    }
    public static string Resolve(string root, string relative, bool allowRoot = false)
    {
        var path = Normalize(relative, allowRoot);
        var directory = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
            !result.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Media must remain inside its library.");
        return result;
    }
    public static string Parent(string relative)
    {
        var index = relative.LastIndexOf('/');
        return index < 0 ? "" : relative[..index];
    }
    public static IEnumerable<MediaLibraryEntry> Read(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                // Do not follow junctions/symlinks outside the library or into directory cycles.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null) continue;
                var folder = (entry.Attributes & FileAttributes.Directory) != 0;
                yield return new(Normalize(Path.GetRelativePath(root, entry.FullName)), folder);
                if (folder) pending.Push(entry.FullName);
            }
        }
    }
}
