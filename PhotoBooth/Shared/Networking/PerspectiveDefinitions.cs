using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoBooth.Networking;

public sealed record PerspectiveRectangle
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 100;
    public double Height { get; init; } = 100;
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 100.000001 || Y + Height > 100.000001)
            throw new ArgumentException("Perspective rectangles must be positive and contained within 0–100 percent.");
    }
}

public sealed record PerspectiveDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "perspective1";
    public PerspectiveRectangle Rectangle { get; init; } = new();
    public void Validate()
    {
        if (!Guid.TryParse(Id, out _) || string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || Name.Length > 80 || Name.Any(char.IsControl) || Rectangle is null)
            throw new ArgumentException("Perspectives require a permanent GUID, a name of 1–80 characters and a rectangle.");
        Rectangle.Validate();
    }
}

public sealed record PerspectiveSelection
{
    public string ControllerId { get; init; } = "";
    public string PerspectiveId { get; init; } = "";
    public string ControllerName { get; init; } = "";
    public string Name { get; init; } = "";
    public PerspectiveRectangle Rectangle { get; init; } = new();
    public bool Available { get; init; } = true;
    public void Validate()
    {
        if (!Guid.TryParse(ControllerId, out _) || !Guid.TryParse(PerspectiveId, out _) || Rectangle is null)
            throw new ArgumentException("A selected perspective requires controller and perspective GUIDs and its last known rectangle.");
        Rectangle.Validate();
    }
    public override string ToString() => $"{ControllerName} / {Name}" + (Available ? "" : " (unavailable)");
}

public sealed record ControllerSharedDefinitions
{
    public int SchemaVersion { get; init; } = 1;
    public string ControllerId { get; init; } = "";
    public string ControllerName { get; init; } = "";
    public string Hostname { get; init; } = "";
    public PerspectiveDefinition[] Perspectives { get; init; } = [];
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public const int MaximumBytes = 4 * 1024 * 1024;
    public void Validate()
    {
        if (SchemaVersion != 1 || !Guid.TryParse(ControllerId, out _) || string.IsNullOrWhiteSpace(ControllerName) || ControllerName.Length > 128 || Perspectives is null || Perspectives.Length > 4096)
            throw new ArgumentException("Invalid controller shared definitions.");
        ControllerDiscovery.MediaShare(Hostname);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var perspective in Perspectives)
        {
            if (perspective is null) throw new ArgumentException("Null perspective.");
            perspective.Validate();
            if (!ids.Add(perspective.Id) || !names.Add(perspective.Name)) throw new ArgumentException("Perspective IDs and names must be unique within their controller.");
        }
    }
    public byte[] Serialize()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Shared definitions exceed 4 MB.");
        return bytes;
    }
    public static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public PerspectiveSelection Select(PerspectiveDefinition p) => new() { ControllerId = ControllerId, PerspectiveId = p.Id, ControllerName = ControllerName, Name = p.Name, Rectangle = p.Rectangle };
}
