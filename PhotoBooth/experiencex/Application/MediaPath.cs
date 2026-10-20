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

    internal static void EnsureLocalPath(string path, int linkDepth = 0) => PhotoBooth.Media.LocalMediaPath.Ensure(path, linkDepth);
}
