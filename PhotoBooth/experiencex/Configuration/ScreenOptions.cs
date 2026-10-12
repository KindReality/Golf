namespace ExperienceX;

// Id survives renaming. List order defines both compositing order and overlap selection order.
public sealed record ScreenOptions
{
    public string Id { get; init; } = "screen1";
    public string Name { get; init; } = "screen1";
    public string? DefaultName { get; init; }
    public string Type { get; init; } = "video";
    public string SizingMode { get; init; } = "fit";
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
    [System.Text.Json.Serialization.JsonIgnore] public bool IsMat => Type == "mat";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVideo => Type == "video";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVisualization => Type == "visualization";
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
        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            DefaultName = name,
            Type = type,
            Points = type == "visualization" || existing.Count == 0 ? new() : new()
            {
                Enabled = true,
                TopLeft = new(.25, .25),
                TopRight = new(.75, .25),
                BottomLeft = new(.25, .75),
                BottomRight = new(.75, .75)
            }
        };
    }
}
