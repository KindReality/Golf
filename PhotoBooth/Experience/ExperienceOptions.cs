namespace Experience;

public sealed record ExperienceOptions
{
    public string? DefaultMonitorName { get; init; }
    public Dictionary<string, string> MonitorAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DefaultAudioDevice { get; init; }
    public bool DeveloperMode { get; init; }
    public double TransitionInSeconds { get; init; } = 5;
    public double TransitionOutSeconds { get; init; } = 1;
    public double MaxVideoPlaybackLength { get; init; } = 10;
    public double MaxAudioPlaybackLength { get; init; } = 10;
    public double AudioTransitionInSeconds { get; init; } = 5;
    public double AudioTransitionOutSeconds { get; init; } = 1;
    public int UdpPort { get; init; } = 21324;
    public string VideoDirectory { get; init; } = "Media";
    public string AudioDirectory { get; init; } = "Media";
    public KeystoneOptions Keystone { get; init; } = new();
    public BlackKeyOptions BlackKey { get; init; } = new();

    public void Validate()
    {
        if (MonitorAliases is null) throw new ArgumentException("MonitorAliases must be an object.");
        var aliasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in MonitorAliases)
        {
            if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(alias.Value) ||
                alias.Key.Trim().StartsWith(@"\\.\", StringComparison.Ordinal) || !aliasNames.Add(alias.Key.Trim()))
                throw new ArgumentException("Monitor aliases require unique, nonempty names and Windows monitor targets; names cannot use the Windows device prefix.");
        }
        if (UdpPort is < 1 or > 65535 ||
            !double.IsFinite(TransitionInSeconds) || TransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(TransitionOutSeconds) || TransitionOutSeconds is < 0 or > 3600 ||
            !double.IsFinite(MaxVideoPlaybackLength) || MaxVideoPlaybackLength is <= 0 or > 86400 ||
            !double.IsFinite(MaxAudioPlaybackLength) || MaxAudioPlaybackLength is <= 0 or > 86400 ||
            !double.IsFinite(AudioTransitionInSeconds) || AudioTransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(AudioTransitionOutSeconds) || AudioTransitionOutSeconds is < 0 or > 3600 ||
            string.IsNullOrWhiteSpace(VideoDirectory) || string.IsNullOrWhiteSpace(AudioDirectory))
            throw new ArgumentException("Invalid Experience configuration.");
        if (Keystone is null) throw new ArgumentException("Keystone must be an object.");
        Keystone.Validate();
        if (BlackKey is null) throw new ArgumentException("BlackKey must be an object.");
        BlackKey.Validate();
    }

    public static ExperienceOptions Parse(string json)
    {
        // Keep deployed configurations safe during the setting-name migration.
        var root = System.Text.Json.Nodes.JsonNode.Parse(json,
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true })
            as System.Text.Json.Nodes.JsonObject ?? throw new System.Text.Json.JsonException("Configuration must be an object.");
        if (!root.ContainsKey("DefaultMonitorName") && root.TryGetPropertyValue("MonitorName", out var monitor))
            root["DefaultMonitorName"] = monitor?.DeepClone();
        if (!root.ContainsKey("MaxVideoPlaybackLength") && root.TryGetPropertyValue("maxPlaybackLength", out var maximum))
            root["MaxVideoPlaybackLength"] = maximum?.DeepClone();
        var options = System.Text.Json.JsonSerializer.Deserialize<ExperienceOptions>(root.ToJsonString(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        options.Validate();
        return options;
    }
}
