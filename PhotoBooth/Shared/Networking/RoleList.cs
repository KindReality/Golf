namespace PhotoBooth.Networking;

// Assigned roles are names. Playback targets additionally allow * and ^name exclusions.
public static class RoleList
{
    public static string[] Parse(string? roles)
    {
        var names = Split(roles);
        if (names.Any(name => !ValidName(name)))
            throw new ArgumentException("Assigned roles require names of 1–64 characters; '*' and a leading '^' are reserved for targets.");
        return names;
    }
    public static string[] ParseTargets(string? roles)
    {
        var targets = Split(roles);
        if (targets.Any(target => !ValidTarget(target)))
            throw new ArgumentException("Role rules require names or '*', optionally prefixed with '^' to deny a match.");
        return targets;
    }
    public static string[] RandomChoices(string? roles)
    {
        var targets = ParseTargets(roles);
        return targets.Where(target => !target.StartsWith('^')).Select(selected => string.Join(",",
            targets.Where(target => target.StartsWith('^') || target.Equals(selected, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }
    private static bool ValidName(string name) => name.Length is > 0 and <= 64 && name == name.Trim() &&
        !name.StartsWith('^') && !name.Contains('*') && !name.Any(char.IsControl);
    private static bool ValidTarget(string target)
    {
        var pattern = target.StartsWith('^') ? target[1..] : target;
        return pattern == "*" || ValidName(pattern);
    }
    private static string[] Split(string? roles)
    {
        if (string.IsNullOrWhiteSpace(roles)) return [];
        if (roles.Length > 16384) throw new ArgumentException("Roles must contain at most 16384 characters.");
        var names = roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 256) throw new ArgumentException("Roles requires at most 256 entries.");
        return names;
    }
    public static bool Matches(string? assigned, string? requested)
    {
        var names = Parse(assigned).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = ParseTargets(requested);
        foreach (var target in targets)
        {
            var excluded = target.StartsWith('^');
            var role = excluded ? target[1..] : target;
            if (role == "*" || names.Contains(role)) return !excluded;
        }
        return false;
    }
}
