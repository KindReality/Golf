using System.Globalization;
using Vortice.Mathematics;

namespace ExperienceX;

internal static class MonitorBackground
{
    public static Color4 Parse(string value)
    {
        var text = value?.Trim();
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            throw new ArgumentException("BackgroundColor must be an opaque #RRGGBB color; use IsTransparent to control transparency.");
        return new((rgb >> 16) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    }

    internal static string Resolve(string key, IReadOnlyDictionary<string, string> aliases)
    {
        var name = key.Trim();
        return aliases.FirstOrDefault(alias => string.Equals(alias.Key.Trim(), name, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? name;
    }

    public static void Validate(Dictionary<string, MonitorBackgroundOptions> backgrounds, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, KeystoneOptions> detectedDisplays)
    {
        if (backgrounds is null)
            throw new ArgumentException("MonitorBackgrounds must be an object.");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in backgrounds)
        {
            var target = Resolve(entry.Key, aliases);
            if (string.IsNullOrWhiteSpace(entry.Key) ||
                (!target.StartsWith(@"\\.\", StringComparison.Ordinal) && !detectedDisplays.Keys.Any(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase))) ||
                !targets.Add(target))
                throw new ArgumentException("MonitorBackgrounds requires a configured alias or Windows monitor name, with only one background per monitor.");
            if (entry.Value is null)
                throw new ArgumentException("MonitorBackgrounds entries must be nonnull objects.");
            Parse(entry.Value.BackgroundColor);
        }
    }

    public static Color4 For(string monitor, ExperienceOptions options)
    {
        foreach (var entry in options.MonitorBackgrounds)
            if (string.Equals(Resolve(entry.Key, options.MonitorAliases), monitor, StringComparison.OrdinalIgnoreCase))
                return entry.Value.IsTransparent ? new(0, 0, 0, 0) : Parse(entry.Value.BackgroundColor);
        return new(0, 0, 0, 1);
    }

    internal static string? KeyFor(string monitor, ExperienceOptions options) => options.MonitorBackgrounds.Keys.FirstOrDefault(key =>
        string.Equals(Resolve(key, options.MonitorAliases), monitor, StringComparison.OrdinalIgnoreCase));
}
