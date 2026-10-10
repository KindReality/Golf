namespace ExperienceX;

public sealed record DisplayBackgroundOptions
{
    public string BackgroundColor { get; init; } = "#000000";
    public bool IsTransparent
    {
        get; init;
    }
}
