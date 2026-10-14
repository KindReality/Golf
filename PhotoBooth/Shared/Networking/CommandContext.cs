namespace PhotoBooth.Networking;

// Optional execution metadata, independent of routing and usable by any command producer.
public sealed record CommandContext
{
    public string? Scene { get; init; }
    public string? Playlist { get; init; }
    public string? Item { get; init; }
    public bool IsAmbient { get; init; }

    public void Validate()
    {
        foreach (var value in new[] { Scene, Playlist, Item })
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
                throw new ArgumentException("Command context names must contain 1–128 characters without control characters.");
    }
}
