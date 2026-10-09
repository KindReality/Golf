namespace ExperienceX;

public sealed record MonitorBackgroundOptions
{
    public string BackgroundColor { get; init; } = "#000000";
    public bool IsTransparent
    {
        get; init;
    }
}
