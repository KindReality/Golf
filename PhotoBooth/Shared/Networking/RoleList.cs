namespace PhotoBooth.Networking;

// Configuration and playback commands use the same comma-separated, case-insensitive names.
public static class RoleList
{
    public static string[] Parse(string? roles)
    {
        if (string.IsNullOrWhiteSpace(roles)) return [];
        if (roles.Length > 16384) throw new ArgumentException("Roles must contain at most 16384 characters.");
        var names = roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 256 || names.Any(name => name.Length > 64 || name == "*" || name.Any(char.IsControl)))
            throw new ArgumentException("Roles requires up to 256 names of 1–64 characters, without control characters or '*'.");
        return names;
    }
    public static bool Matches(string? assigned, string? requested)
        => Parse(assigned).Intersect(Parse(requested), StringComparer.OrdinalIgnoreCase).Any();
}
