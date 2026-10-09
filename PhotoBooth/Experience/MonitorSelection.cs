namespace Experience;

public static class MonitorSelection
{
    public static string? Select(string? assigned, bool developerMode, IReadOnlyList<string> available, string? primary)
    {
        var requested = assigned?.Trim();
        if (!string.IsNullOrEmpty(requested))
        {
            var match = available.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
            if (match is not null || !developerMode) return match;
        }
        return available.FirstOrDefault(name => string.Equals(name, primary, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault();
    }
}
