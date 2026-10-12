namespace ExperienceX;

public sealed record VisualizationOptions
{
    // Empty discovers the installed Standalone. G-Force must be installed separately.
    public string? ExecutablePath
    {
        get; init;
    }
}
