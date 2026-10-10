namespace ExperienceX;

public sealed record ExperienceOptions : PhotoBooth.Networking.BroadcastOptions
{
    public int ConfigurationVersion { get; init; } = ConfigurationSchema.CurrentVersion;
    public string? DefaultDisplayName
    {
        get; init;
    }
    public Dictionary<string, string> DisplayAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DisplayBackgroundOptions> DisplayBackgrounds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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
    public double PointHitDiameterPercent { get; init; } = 5;
    public Dictionary<string, List<ScreenOptions>> DisplayScreens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ScreenOptions> ScreensFor(string display) => DisplayScreens.FirstOrDefault(p =>
        string.Equals(p.Key, display, StringComparison.OrdinalIgnoreCase)).Value ??
        [new ScreenOptions { Points = Points }];
    public PointOptions PointsFor(string display, string? screenId = null) =>
        ScreensFor(display).FirstOrDefault(s => screenId is null || s.Id.Equals(screenId, StringComparison.OrdinalIgnoreCase))?.Points ?? Points;
    public double TransitionInSeconds { get; init; } = 0.5;
    public double TransitionOutSeconds { get; init; } = 3;
    public double AudioTransitionInSeconds { get; init; } = 0.5;
    public double AudioTransitionOutSeconds { get; init; } = 3;
    public string VideoDirectory { get; init; } = "Media";
    public string AudioDirectory { get; init; } = "Media";
    public MediaDiscoveryOptions MediaDiscovery { get; init; } = new();
    public PointOptions Points { get; init; } = new();
    public BlackKeyOptions BlackKey { get; init; } = new();

    public void Validate()
    {
        ValidateNetwork();
        if (MediaDiscovery is null) throw new ArgumentException("MediaDiscovery must be an object.");
        MediaDiscovery.Validate();
        AudioDeviceSelection.ValidateConfiguration(VirtualAudioDevices, AudioDeviceLatencyMilliseconds);
        if (ConfigurationVersion != ConfigurationSchema.CurrentVersion)
            throw new ArgumentException("ConfigurationVersion must match the supported schema version.");
        if (!double.IsFinite(RequestDeduplicationSeconds) || RequestDeduplicationSeconds is < 0 or > 3600)
            throw new ArgumentException("RequestDeduplicationSeconds must be between zero and 3600.");
        if (!double.IsFinite(PointHitDiameterPercent) || PointHitDiameterPercent is <= 0 or > 100)
            throw new ArgumentException("PointHitDiameterPercent must be greater than zero and at most 100.");
        if (DisplayScreens is null)
            throw new ArgumentException("DisplayScreens must be an object.");
        var configuredDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var display in DisplayScreens)
        {
            if (string.IsNullOrWhiteSpace(display.Key) || !configuredDisplays.Add(display.Key) || display.Value is null)
                throw new ArgumentException("DisplayScreens requires unique display names and nonnull arrays.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var screen in display.Value)
            {
                if (screen is null || string.IsNullOrWhiteSpace(screen.Id) || screen.Id.Length > 128 || !ids.Add(screen.Id) || !names.Add(screen.Name))
                    throw new ArgumentException("Screens require unique IDs and names within their display.");
                ScreenOptions.ValidateName(screen.Name);
                if (screen.Type is not ("video" or "mat"))
                    throw new ArgumentException("Screen type must be video or mat.");
                if (screen.Points is null)
                    throw new ArgumentException("Each screen requires points.");
                screen.BlackKey?.Validate();
                if (screen.IsMat) screen.Points.ValidatePolygon();
                else screen.Points.Validate();
            }
        }
        if (DisplayAliases is null)
            throw new ArgumentException("DisplayAliases must be an object.");
        var aliasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in DisplayAliases)
        {
            if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(alias.Value) ||
                alias.Key.Contains('/') || alias.Key.Contains(':') ||
                alias.Key.Trim().StartsWith(@"\\.\", StringComparison.Ordinal) || !aliasNames.Add(alias.Key.Trim()))
                throw new ArgumentException("Display aliases require unique, nonempty names and Windows display targets; names cannot contain slashes, colons or the Windows device prefix.");
        }
        DisplayBackground.Validate(DisplayBackgrounds, DisplayAliases, DisplayScreens.Keys.ToDictionary(name => name, _ => new PointOptions()));
        if (
            !double.IsFinite(TransitionInSeconds) || TransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(TransitionOutSeconds) || TransitionOutSeconds is < 0 or > 3600 ||
            !double.IsFinite(AudioTransitionInSeconds) || AudioTransitionInSeconds is < 0 or > 3600 ||
            !double.IsFinite(AudioTransitionOutSeconds) || AudioTransitionOutSeconds is < 0 or > 3600 ||
            string.IsNullOrWhiteSpace(VideoDirectory) || string.IsNullOrWhiteSpace(AudioDirectory))
            throw new ArgumentException("Invalid Experience configuration.");
        if (Points is null)
            throw new ArgumentException("Points must be an object.");
        Points.Validate();
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
