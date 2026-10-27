namespace PhotoBooth.Networking;

// Suggestions are names, never routing rules. Keep the original rules untouched.
internal static class RoleCatalog
{
    internal static string[] Names(IEnumerable<string?> rules) => rules.SelectMany(RoleList.ParseTargets)
        .Select(role => role.StartsWith('^') ? role[1..] : role).Where(role => role != "*")
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
