using PhotoBooth.Networking;

namespace ExperienceX;

public sealed record AudioDeviceOptions
{
    public string Name { get; init; } = "";
    public string Roles { get; init; } = "";

    internal static void Validate(Dictionary<string, AudioDeviceOptions> devices)
    {
        if (devices is null || devices.Count > 512) throw new ArgumentException("AudioDevices requires an object with at most 512 entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in devices)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 512 || entry.Key.Any(char.IsControl) ||
                entry.Key.Trim() == "*" || !names.Add(entry.Key.Trim()) || entry.Value is null ||
                entry.Value.Name is null || entry.Value.Name.Length > 512 || entry.Value.Name.Any(char.IsControl))
                throw new ArgumentException("AudioDevices requires unique physical IDs/names or virtual audio names and valid settings.");
            RoleList.Parse(entry.Value.Roles);
        }
    }

    internal static string RolesFor(AudioOutput output, ExperienceOptions options)
    {
        static bool Same(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        var groups = options.VirtualAudioDevices.Where(group => group.Value.Any(member => Same(member, output.Id) || Same(member, output.Name)))
            .Select(group => group.Key).ToArray();
        return string.Join(",", options.AudioDevices.Where(entry => Same(entry.Key, output.Id) || Same(entry.Key, output.Name) || groups.Any(group => Same(group, entry.Key)))
            .SelectMany(entry => RoleList.Parse(entry.Value.Roles)).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
