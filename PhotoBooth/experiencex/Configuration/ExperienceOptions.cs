namespace ExperienceX;

public sealed record ExperienceOptions : PhotoBooth.Networking.BroadcastOptions
{
    public int ConfigurationVersion { get; init; } = ConfigurationSchema.CurrentVersion;
    public string? DefaultMonitorName
    {
        get; init;
    }
    public Dictionary<string, string> MonitorAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MonitorBackgroundOptions> MonitorBackgrounds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DefaultAudioDevice
    {
        get; init;
    }
    public Dictionary<string, string[]> VirtualAudioDevices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // Additional downstream delay not reported by WASAPI (TV DSP, wireless receivers, etc.).
    public Dictionary<string, double> AudioDeviceLatencyMilliseconds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DeveloperMode
    {
        get; init;
    }
    [System.Text.Json.Serialization.JsonPropertyName("configurationMode")]
    public bool ConfigurationMode
    {
        get; init;
    }
    public bool CalibrationGridEnabled { get; init; } = true;
    public double KeystoneHitDiameterPercent { get; init; } = 5;
    public Dictionary<string, KeystoneOptions> DisplayKeystones { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public KeystoneOptions KeystoneFor(string monitor) => DisplayKeystones.FirstOrDefault(p =>
        string.Equals(p.Key, monitor, StringComparison.OrdinalIgnoreCase)).Value ?? Keystone;
    public double TransitionInSeconds { get; init; } = 5;
    public double TransitionOutSeconds { get; init; } = 1;
    public double MaxVideoPlaybackLength { get; init; } = 10;
    public double MaxAudioPlaybackLength { get; init; } = 10;
    public double AudioTransitionInSeconds { get; init; } = 5;
    public double AudioTransitionOutSeconds { get; init; } = 1;
    public string VideoDirectory { get; init; } = "Media";
    public string AudioDirectory { get; init; } = "Media";
    public KeystoneOptions Keystone { get; init; } = new();
    public BlackKeyOptions BlackKey { get; init; } = new();

    public void Validate()
    {
        ValidateNetwork();
        AudioDeviceSelection.ValidateConfiguration(VirtualAudioDevices, AudioDeviceLatencyMilliseconds);
        if (ConfigurationVersion != ConfigurationSchema.CurrentVersion)
            throw new ArgumentException("ConfigurationVersion must match the supported schema version.");
        if (!double.IsFinite(RequestDeduplicationSeconds) || RequestDeduplicationSeconds is < 0 or > 3600)
            throw new ArgumentException("RequestDeduplicationSeconds must be between zero and 3600.");
        if (!double.IsFinite(KeystoneHitDiameterPercent) || KeystoneHitDiameterPercent is <= 0 or > 100)
            throw new ArgumentException("KeystoneHitDiameterPercent must be greater than zero and at most 100.");
        if (DisplayKeystones is null)
            throw new ArgumentException("DisplayKeystones must be an object.");
        var displayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in DisplayKeystones)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !displayNames.Add(item.Key) || item.Value is null)
                throw new ArgumentException("DisplayKeystones requires unique monitor names and nonnull settings.");
            item.Value.Validate();
        }
        if (MonitorAliases is null)
            throw new ArgumentException("MonitorAliases must be an object.");
        var aliasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in MonitorAliases)
        {
            if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(alias.Value) ||
                alias.Key.Trim().StartsWith(@"\\.\", StringComparison.Ordinal) || !aliasNames.Add(alias.Key.Trim()))
                throw new ArgumentException("Monitor aliases require unique, nonempty names and Windows monitor targets; names cannot use the Windows device prefix.");
        }
        MonitorBackground.Validate(MonitorBackgrounds, MonitorAliases, DisplayKeystones);
        if (UdpPort is < 1 or > 65535 ||
            !double.IsFinite(TransitionInSeconds) || TransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(TransitionOutSeconds) || TransitionOutSeconds is < 0 or > 3600 ||
            !double.IsFinite(MaxVideoPlaybackLength) || MaxVideoPlaybackLength is <= 0 or > 86400 ||
            !double.IsFinite(MaxAudioPlaybackLength) || MaxAudioPlaybackLength is <= 0 or > 86400 ||
            !double.IsFinite(AudioTransitionInSeconds) || AudioTransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(AudioTransitionOutSeconds) || AudioTransitionOutSeconds is < 0 or > 3600 ||
            string.IsNullOrWhiteSpace(VideoDirectory) || string.IsNullOrWhiteSpace(AudioDirectory))
            throw new ArgumentException("Invalid Experience configuration.");
        if (Keystone is null)
            throw new ArgumentException("Keystone must be an object.");
        Keystone.Validate();
        if (BlackKey is null)
            throw new ArgumentException("BlackKey must be an object.");
        BlackKey.Validate();
    }

    public static ExperienceOptions Parse(string json)
    {
        var root = ConfigurationSchema.Read(json);
        var options = System.Text.Json.JsonSerializer.Deserialize<ExperienceOptions>(root.ToJsonString(),
            ConfigurationSchema.Json)!;
        options.Validate();
        return options;
    }
}
