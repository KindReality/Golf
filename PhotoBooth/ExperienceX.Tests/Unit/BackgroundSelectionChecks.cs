using ExperienceX;

internal static class BackgroundSelectionChecks
{
    public static void Run()
    {
        var choice = new BackgroundSelection();
        if (choice.PressW() is not null) throw new Exception("W changed a background without a pending choice.");
        if (choice.PressB("left", false) != new BackgroundChange("left", true) || choice.PendingDisplay is not null)
            throw new Exception("B on a solid background did not request transparency immediately.");
        if (choice.PressB("left", true) is not null || choice.PendingDisplay != "left")
            throw new Exception("B on a transparent background did not wait for a color choice.");
        if (choice.PressB("right", true) != new BackgroundChange("left", false, "#000000") || choice.PendingDisplay is not null)
            throw new Exception("B color choice did not select black on the original display.");
        choice.PressB("right", true);
        if (choice.PressW() != new BackgroundChange("right", false, "#FFFFFF") || choice.PendingDisplay is not null)
            throw new Exception("W color choice did not select white on the original display.");
        choice.PressB("left", true);
        choice.Cancel();
        if (choice.PressW() is not null || choice.PendingDisplay is not null || choice.PressB("right", false)?.Display != "right")
            throw new Exception("Cancelled color choice still affected subsequent input.");
        Console.WriteLine("Passed B solid-to-transparent, transparent-to-choice, B black/W white, locked display target, cancellation and ignored standalone W.");
    }
}
