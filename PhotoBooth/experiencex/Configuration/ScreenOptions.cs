namespace ExperienceX;

// Id survives renaming. List order defines both compositing order and overlap selection order.
public sealed record ScreenOptions
{
    public string Id { get; init; } = "screen1";
    public string Name { get; init; } = "screen1";
    public string Type { get; init; } = "video";
    public string SizingMode { get; init; } = "fit";
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
    internal static ScreenOptions New(IReadOnlyList<ScreenOptions> existing, string type = "video")
    {
        int number = 1;
        while (existing.Any(s => s.Name.Equals($"screen{number}", StringComparison.OrdinalIgnoreCase)))
            number++;
        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"screen{number}",
            Type = type,
            Points = existing.Count == 0 ? new() : new()
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
