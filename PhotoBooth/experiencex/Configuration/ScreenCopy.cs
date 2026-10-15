using System.Text.Json;

namespace ExperienceX;

// Configuration only: copied screens never contain decoders, playback requests or display ownership.
internal sealed record ScreenCopy
{
    public int Version { get; init; } = 1;
    public string SourceDisplay { get; init; } = "";
    public ScreenOptions Screen { get; init; } = new();
    internal const string ClipboardFormat = "SaftTechnologies.Experience.Screen.v1";
    private const int MaximumCharacters = 256 * 1024;
    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(SourceDisplay) || SourceDisplay.Length > 256 ||
            Screen is null || !Screen.IsVideo && !Screen.IsMat)
            throw new ArgumentException("Copy a video, image or mat screen in Experience first.");
        new ExperienceOptions { DisplayScreens = new() { ["Clipboard"] = [Screen] } }.Validate();
    }
    internal string Encode()
    {
        Validate();
        var json = JsonSerializer.Serialize(this);
        if (json.Length > MaximumCharacters) throw new ArgumentException("This screen is too large to copy.");
        return json;
    }
    internal static ScreenCopy Decode(string json)
    {
        if (json.Length > MaximumCharacters) throw new ArgumentException("The copied screen is too large.");
        var copied = JsonSerializer.Deserialize<ScreenCopy>(json) ?? throw new ArgumentException("The clipboard screen is empty.");
        copied.Validate();
        return copied;
    }
}
