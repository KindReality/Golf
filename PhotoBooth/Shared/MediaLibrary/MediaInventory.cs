using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoBooth.Media;

internal sealed record MediaFileMetadata(string RelativePath, long Length, DateTime LastWriteUtc);

// The revision hashes metadata only. Media contents are never read to build an inventory.
internal sealed record MediaInventory
{
    public int SchemaVersion { get; init; } = 1;
    public MediaFileMetadata[] Files { get; init; } = [];
    public string[] Folders { get; init; } = [];
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly MediaInventory Empty = new();
    internal static readonly string EmptyRevision = Revision(Empty.Serialize());

    internal static MediaInventory Read(string root)
    {
        var files = new List<MediaFileMetadata>();
        var folders = new List<string>();
        foreach (var entry in MediaLibrary.Read(root))
        {
            if (entry.RelativePath.Split('/').Any(part => part.StartsWith('.'))) continue;
            if (entry.IsFolder) folders.Add(entry.RelativePath);
            else
            {
                var file = new FileInfo(MediaLibrary.Resolve(root, entry.RelativePath));
                files.Add(new(entry.RelativePath, file.Length, file.LastWriteTimeUtc));
            }
        }
        return new() { Files = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(), Folders = folders.Order(StringComparer.Ordinal).ToArray() };
    }

    internal void Validate()
    {
        if (SchemaVersion != 1 || Files is null || Folders is null || Files.Length > 20000 || Folders.Length > 20000)
            throw new ArgumentException("Invalid media inventory version or counts.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Path(string path)
        {
            if (MediaLibrary.Normalize(path) != path || path.Split('/').Any(part => part.StartsWith('.')) || !paths.Add(path))
                throw new ArgumentException("Invalid or duplicate media inventory path.");
        }
        foreach (var folder in Folders) Path(folder);
        foreach (var file in Files)
        {
            if (file is null || file.Length < 0 || file.LastWriteUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Invalid media file metadata.");
            Path(file.RelativePath);
        }
    }

    internal byte[] Serialize()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Media inventory exceeds 4 MB.");
        return bytes;
    }
    internal static MediaInventory Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Media inventory exceeds 4 MB.");
        var inventory = JsonSerializer.Deserialize<MediaInventory>(bytes, Json) ?? throw new ArgumentException("Empty media inventory.");
        inventory.Validate();
        return inventory;
    }
    internal static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
