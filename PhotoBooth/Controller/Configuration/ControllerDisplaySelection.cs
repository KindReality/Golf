namespace Controller.Configuration;

internal static class ControllerDisplaySelection
{
    public static string? Target(ControllerOptions options)
    {
        var requested = options.DefaultDisplayName?.Trim();
        if (string.IsNullOrEmpty(requested)) return null;
        var alias = options.DisplayAliases.FirstOrDefault(entry =>
            string.Equals(entry.Key.Trim(), requested, StringComparison.OrdinalIgnoreCase));
        return alias.Key is null ? requested : alias.Value.Trim();
    }

    public static string? Select(ControllerOptions options, IReadOnlyList<string> displays, string? primary)
    {
        var requested = Target(options);
        return displays.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(name => string.Equals(name, primary, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault();
    }

    public static void Validate(string? display, IReadOnlyDictionary<string, string>? aliases)
    {
        static bool Invalid(string text) => text.Trim().Length > 128 || text.Any(char.IsControl) || text.Trim() == "*";
        if (display is not null && Invalid(display)) throw new ArgumentException("DefaultDisplayName must identify one display or be empty for the primary display.");
        if (aliases is null) throw new ArgumentException("DisplayAliases must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
            if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(alias.Value) || Invalid(alias.Key) || Invalid(alias.Value) ||
                alias.Key.Contains('/') || alias.Key.Contains(':') || alias.Key.Trim().StartsWith(@"\\.\", StringComparison.Ordinal) || !names.Add(alias.Key.Trim()))
                throw new ArgumentException("DisplayAliases requires unique, nonempty aliases mapped to Windows display names.");
    }
}
