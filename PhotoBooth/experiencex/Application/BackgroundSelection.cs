namespace ExperienceX;

internal sealed record BackgroundChange(string Display, bool IsTransparent, string? Color = null);

// A color choice stays attached to the display where B opened it, even if the mouse moves.
internal sealed class BackgroundSelection
{
    public string? PendingDisplay { get; private set; }
    public BackgroundChange? PressB(string display, bool transparent)
    {
        if (PendingDisplay is not null)
            return Choose("#000000");
        if (!transparent)
            return new(display, true);
        PendingDisplay = display;
        return null;
    }
    public BackgroundChange? PressW() => Choose("#FFFFFF");
    private BackgroundChange? Choose(string color)
    {
        if (PendingDisplay is not { } display) return null;
        PendingDisplay = null;
        return new(display, false, color);
    }
    public void Cancel() => PendingDisplay = null;
}
