namespace ExperienceX;

// Id survives renaming. List order defines both compositing order and overlap selection order.
public sealed record ScreenOptions
{
    public string Id { get; init; } = "screen1";
    public string Name { get; init; } = "screen1";
    public string? DefaultName { get; init; }
    // Explicit binding lets uniquely named screens share the same local media file.
    public string? MediaFile { get; init; }
    public string Type { get; init; } = "video";
    // Mat polygons can cover their exterior instead of their interior.
    public bool IsNegative { get; init; }
    public string Roles { get; init; } = "";
    public double Brightness { get; init; } = 100;
    public PhotoBooth.Networking.PerspectiveSelection? Perspective { get; init; }
    public PointOptions Points { get; init; } = new();
    // Null uses the application defaults; an explicit value belongs only to this video screen.
    public BlackKeyOptions? BlackKey { get; init; }
    internal BlackKeyOptions EffectiveBlackKey(ExperienceOptions options) => BlackKey ?? options.BlackKey;

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "*" || name != name.Trim() || name.Length > 80 || name.Any(char.IsControl) || name.Contains('/') || name.Contains(':'))
            throw new ArgumentException("Screen names must contain 1–80 characters, without slashes, colons or surrounding spaces.");
    }
    internal static void ValidateMediaFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file != file.Trim() || file.Length > 255 || file is "." or ".." ||
            file.Any(char.IsControl) || file.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || file.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("MediaFile must be a local media filename without a directory.");
    }
    internal static string AvailableMediaName(string requested, IEnumerable<ScreenOptions> others)
    {
        var names = others.SelectMany(s => new[] { s.Name, s.DefaultName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested)) return requested;
        for (int i = 2; ; i++)
        {
            var suffix = "-" + i;
            var candidate = requested[..Math.Min(requested.Length, 80 - suffix.Length)] + suffix;
            if (!names.Contains(candidate)) return candidate;
        }
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsMat => Type == "mat";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVideo => Type == "video";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVisualization => Type == "visualization";
    // Visualization points describe its owned inverse mat, not the external fullscreen window.
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPolygon => IsMat || IsVisualization;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsLed => Type == "fastled";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsLedArray => IsLed && Perspective?.Type == "pixelArray";
    internal static string AvailableDefaultName(IReadOnlyList<ScreenOptions> existing)
    {
        int number = 1;
        while (existing.Any(s => string.Equals(s.Name, $"screen{number}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.DefaultName, $"screen{number}", StringComparison.OrdinalIgnoreCase))) number++;
        return $"screen{number}";
    }
    internal static string DefaultFor(ScreenOptions screen, IReadOnlyList<ScreenOptions> existing)
    {
        if (screen.DefaultName is not null) return screen.DefaultName;
        static bool Generated(string name) => name.StartsWith("screen", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(6), out var number) && number > 0;
        var others = existing.Where(s => s.Id != screen.Id).ToArray();
        var candidate = Generated(screen.Name) ? screen.Name : Generated(screen.Id) ? screen.Id : AvailableDefaultName(others);
        return others.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.DefaultName, candidate, StringComparison.OrdinalIgnoreCase)) ? AvailableDefaultName(others) : candidate;
    }
    internal static ScreenOptions New(IReadOnlyList<ScreenOptions> existing, string type = "video")
    {
        if (type == "visualization" && existing.Any(s => s.IsVisualization))
            throw new InvalidOperationException("This display already has a visualization screen.");
        var name = AvailableDefaultName(existing);
        if (type == "visualization")
        {
            name = "gforce";
            int suffix = 2;
            while (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.DefaultName, name, StringComparison.OrdinalIgnoreCase))) name = "gforce" + suffix++;
        }
        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            DefaultName = name,
            Type = type,
            Points = new()
        };
    }
    internal static int InsertionIndex(IReadOnlyList<ScreenOptions> existing, ScreenOptions added)
    {
        if (added.IsVisualization) return 0;
        if (added.IsVideo || added.IsMat)
        {
            for (var index = existing.Count - 1; index >= 0; index--)
                if (existing[index].Type == added.Type) return index + 1;
        }
        return existing.Count;
    }
}
