namespace Experience;

public sealed record AudioOutput(string Id, string Name, int Channels);

public static class OutputSelection
{
    public static string? Monitor(string? requested, string? configured, IReadOnlyList<string> available, string? primary,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var target = (string.IsNullOrWhiteSpace(requested) ? configured : requested)?.Trim();
        if (!string.IsNullOrEmpty(target) && aliases is not null)
        {
            var alias = aliases.FirstOrDefault(a => string.Equals(a.Key.Trim(), target, StringComparison.OrdinalIgnoreCase));
            if (alias.Key is not null) target = alias.Value.Trim();
        }
        // Resolve only one alias level; missing targets never fall back to another display.
        return MonitorSelection.Select(target, false, available, primary);
    }

    public static AudioOutput? Audio(string? requested, string? configured, IReadOnlyList<AudioOutput> available, string? defaultId)
    {
        var target = (string.IsNullOrWhiteSpace(requested) ? configured : requested)?.Trim();
        if (string.IsNullOrEmpty(target)) return available.FirstOrDefault(d => d.Id == defaultId);
        var id = available.FirstOrDefault(d => string.Equals(d.Id, target, StringComparison.OrdinalIgnoreCase));
        if (id is not null) return id;
        var names = available.Where(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase)).ToArray();
        return names.Length == 1 ? names[0] : null; // Ambiguous names must not pick an arbitrary output.
    }
}
