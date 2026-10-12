namespace ExperienceX;

internal static class ScreenMediaNameCycle
{
    public static string Next(string defaultName, string current, IReadOnlyList<string> media, int direction)
    {
        var names = new[] { defaultName }.Concat(media.Where(name => !name.Equals(defaultName, StringComparison.OrdinalIgnoreCase))).ToArray();
        int index = Array.FindIndex(names, name => name.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        return names[(index + (direction < 0 ? -1 : 1) + names.Length) % names.Length];
    }
}
