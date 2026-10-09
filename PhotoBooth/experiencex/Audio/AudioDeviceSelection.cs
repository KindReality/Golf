namespace ExperienceX;

internal sealed record AudioDeviceSelection(AudioOutput[] Devices, string[] Unavailable)
{
    public static AudioDeviceSelection Resolve(ExperienceRequest request, ExperienceOptions options,
        IReadOnlyList<AudioOutput> available, string? defaultId)
    {
        var devices = new Dictionary<string, AudioOutput>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        void Physical(string target)
        {
            var device = OutputSelection.Audio(target, null, available, defaultId);
            if (device is null)
                missing.Add(target);
            else
                devices.TryAdd(device.Id, device);
        }
        void Expand(string target)
        {
            var group = options.VirtualAudioDevices.FirstOrDefault(pair =>
                string.Equals(pair.Key.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase));
            if (group.Key is null)
            {
                Physical(target);
                return;
            }
            if (available.Any(device => string.Equals(device.Id, target.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.Name, target.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A virtual audio device conflicts with a physical device name: " + target);
            foreach (var member in group.Value)
                Physical(member);
        }
        if (request.AudioDevices is not null)
        {
            foreach (var target in request.AudioDevices)
                Expand(target);
        }
        else
        {
            var target = (string.IsNullOrWhiteSpace(request.AudioDevice) ? options.DefaultAudioDevice : request.AudioDevice)?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                var device = available.FirstOrDefault(device => device.Id == defaultId);
                if (device is not null)
                    devices.Add(device.Id, device);
                else
                    missing.Add("System default audio device");
            }
            // Preserve literal device names containing commas; arrays are unambiguous.
            else if (available.Any(device => string.Equals(device.Name, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.Id, target, StringComparison.OrdinalIgnoreCase)) ||
                options.VirtualAudioDevices.Keys.Any(name => string.Equals(name.Trim(), target, StringComparison.OrdinalIgnoreCase)))
                Expand(target);
            else
            {
                var targets = target.Split(',', StringSplitOptions.TrimEntries);
                if (targets.Length > 32 || targets.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("Audio device lists require up to 32 nonempty names/IDs.");
                foreach (var item in targets)
                    Expand(item);
            }
        }
        if (devices.Count > 32)
            throw new ArgumentException("A request can resolve to at most 32 audio outputs.");
        return new(devices.Values.ToArray(), missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static double LatencySeconds(AudioOutput device, ExperienceOptions options)
    {
        var byId = options.AudioDeviceLatencyMilliseconds.FirstOrDefault(pair => string.Equals(pair.Key.Trim(), device.Id, StringComparison.OrdinalIgnoreCase));
        var byName = options.AudioDeviceLatencyMilliseconds.FirstOrDefault(pair => string.Equals(pair.Key.Trim(), device.Name, StringComparison.OrdinalIgnoreCase));
        return (byId.Key is not null ? byId.Value : byName.Value) / 1000;
    }

    public static void ValidateConfiguration(Dictionary<string, string[]> groups, Dictionary<string, double> latency)
    {
        if (groups is null || latency is null)
            throw new ArgumentException("VirtualAudioDevices and AudioDeviceLatencyMilliseconds must be objects.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in groups)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains(',') || !names.Add(pair.Key.Trim()) ||
                pair.Value is null || pair.Value.Length is 0 or > 32 || pair.Value.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("VirtualAudioDevices requires unique nonempty names without commas and lists of 1–32 physical device names/IDs.");
        }
        if (groups.Values.Any(members => members.Any(member => names.Contains(member.Trim()))))
            throw new ArgumentException("Virtual audio devices may contain physical devices only, not other virtual devices.");
        names.Clear();
        foreach (var pair in latency)
            if (string.IsNullOrWhiteSpace(pair.Key) || !names.Add(pair.Key.Trim()) || !double.IsFinite(pair.Value) || pair.Value is < 0 or > 5000)
                throw new ArgumentException("AudioDeviceLatencyMilliseconds requires unique physical names/IDs and delays from 0 to 5000 ms.");
    }
}
