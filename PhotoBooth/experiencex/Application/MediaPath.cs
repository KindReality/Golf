using System.IO;

namespace ExperienceX;

public static class MediaPath
{
    public static string Resolve(string directory, string value, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new ArgumentException("A local media filename is required.");
        var root = Path.GetFullPath(directory, baseDirectory);
        var path = Path.GetFullPath(Path.Combine(root, value));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Media must be inside its configured directory.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file is unavailable.", path);
        return path;
    }
}
