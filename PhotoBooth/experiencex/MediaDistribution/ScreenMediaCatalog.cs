using System.IO;

namespace ExperienceX;

internal sealed record ScreenMediaBinding(string? Path, bool IsImage = false, long Length = 0,
    long LastWriteTicks = 0, string? Error = null);

// Immutable local inventory shared by all displays; no directory I/O on render frames.
internal sealed class ScreenMediaCatalog
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mpg", ".mpeg", ".wmv", ".avi", ".mov", ".ts", ".m2ts" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    private readonly Dictionary<string, ScreenMediaBinding> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ScreenMediaBinding>> _names = new(StringComparer.OrdinalIgnoreCase);
    public static ScreenMediaCatalog Empty { get; } = new("");
    public string DirectoryPath { get; }
    private ScreenMediaCatalog(string directory) => DirectoryPath = directory;

    public static ScreenMediaCatalog Read(string directory)
    {
        var root = MediaPath.ResolveDirectory(directory);
        var catalog = new ScreenMediaCatalog(root);
        if (!Directory.Exists(root)) return catalog;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var extension = System.IO.Path.GetExtension(path);
            bool image = ImageExtensions.Contains(extension);
            if (!image && !VideoExtensions.Contains(extension)) continue;
            MediaPath.EnsureLocalPath(path);
            var file = new FileInfo(path);
            if (!file.Exists) continue;
            var binding = new ScreenMediaBinding(file.FullName, image, file.Length, file.LastWriteTimeUtc.Ticks);
            catalog._files.Add(file.Name, binding);
            var name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            if (!catalog._names.TryGetValue(name, out var matches)) catalog._names.Add(name, matches = []);
            matches.Add(binding);
        }
        return catalog;
    }

    public ScreenMediaBinding? Match(ScreenOptions screen)
    {
        if (!screen.IsVideo) return null;
        // An explicit extension disambiguates files that share a basename.
        if (_files.TryGetValue(screen.Name, out var exact)) return exact;
        if (!_names.TryGetValue(screen.Name, out var matches)) return null;
        if (matches.Count == 1) return matches[0];
        var filenames = matches.Select(m => System.IO.Path.GetFileName(m.Path!)).Order(StringComparer.OrdinalIgnoreCase);
        return new(null, Error: $"Multiple media files match '{screen.Name}': {string.Join(", ", filenames)}. Include the extension in the screen name.");
    }
    public IReadOnlyList<string> Names()
    {
        var result = new List<string>();
        foreach (var file in _files)
        {
            var basename = System.IO.Path.GetFileNameWithoutExtension(file.Key);
            var candidate = Match(new() { Name = basename });
            result.Add(string.Equals(candidate?.Path, file.Value.Path, StringComparison.OrdinalIgnoreCase) ? basename : file.Key);
        }
        return result.Where(name =>
        {
            try { ScreenOptions.ValidateName(name); return true; }
            catch (ArgumentException) { return false; }
        }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public string? BrowseName(ScreenOptions screen)
    {
        var path = Match(screen)?.Path;
        return path is null ? null : Names().FirstOrDefault(name =>
            string.Equals(Match(new() { Name = name })?.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
