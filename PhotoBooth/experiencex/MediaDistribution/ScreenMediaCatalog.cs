using System.IO;
using PhotoBooth.Media;

namespace ExperienceX;

internal sealed record ScreenMediaBinding(string? Path, bool IsImage = false, long Length = 0,
    long LastWriteTicks = 0, string? Error = null, string? RelativePath = null);

// Immutable local inventory shared by all displays; no directory I/O on render frames.
internal sealed class ScreenMediaCatalog
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mpg", ".mpeg", ".wmv", ".avi", ".mov", ".ts", ".m2ts" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    private readonly Dictionary<string, ScreenMediaBinding> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ScreenMediaBinding>> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MediaLibraryEntry> _entries = [];
    public static ScreenMediaCatalog Empty { get; } = new("");
    public string DirectoryPath { get; }
    private ScreenMediaCatalog(string directory) => DirectoryPath = directory;
    public static ScreenMediaCatalog Read(string directory)
    {
        var root = MediaPath.ResolveDirectory(directory);
        var catalog = new ScreenMediaCatalog(root);
        foreach (var entry in MediaLibrary.Read(root))
        {
            if (entry.IsFolder) { catalog._entries.Add(entry); continue; }
            var extension = Path.GetExtension(entry.RelativePath);
            bool image = ImageExtensions.Contains(extension);
            if (!image && !VideoExtensions.Contains(extension)) continue;
            var path = MediaLibrary.Resolve(root, entry.RelativePath);
            MediaPath.EnsureLocalPath(path);
            var file = new FileInfo(path);
            if (!file.Exists) continue;
            var binding = new ScreenMediaBinding(file.FullName, image, file.Length, file.LastWriteTimeUtc.Ticks, RelativePath:entry.RelativePath);
            catalog._files.Add(entry.RelativePath, binding);
            catalog._entries.Add(entry);
            foreach (var name in new[] { file.Name, Path.GetFileNameWithoutExtension(file.Name) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!catalog._names.TryGetValue(name, out var matches)) catalog._names.Add(name, matches = []);
                matches.Add(binding);
            }
        }
        return catalog;
    }
    public ScreenMediaBinding? Match(ScreenOptions screen)
    {
        if (!screen.IsVideo || screen.MediaFile == "") return null;
        if (screen.MediaFile is not null)
            return _files.TryGetValue(MediaLibrary.Normalize(screen.MediaFile), out var bound) ? bound : new(null, Error:$"Bound media '{screen.MediaFile}' is unavailable locally.");
        if (_files.TryGetValue(screen.Name, out var exact)) return exact;
        if (!_names.TryGetValue(screen.Name, out var matches)) return null;
        if (matches.Count == 1) return matches[0];
        return new(null, Error:$"Multiple media files match '{screen.Name}': {string.Join(", ", matches.Select(m=>m.RelativePath).Order(StringComparer.OrdinalIgnoreCase))}. Select a file with the media browser.");
    }
    internal IReadOnlyList<MediaLibraryEntry> Entries(string directory) => _entries
        .Where(entry=>MediaLibrary.Parent(entry.RelativePath).Equals(directory,StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(entry=>entry.IsFolder).ThenBy(entry=>entry.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    internal bool HasFolder(string path) => path == "" || _entries.Any(e=>e.IsFolder&&e.RelativePath.Equals(path,StringComparison.OrdinalIgnoreCase));
}
