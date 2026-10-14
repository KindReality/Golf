namespace ExperienceX;

public sealed record ExperienceOptions : PhotoBooth.Networking.BroadcastOptions
{
    public int InventoryPort { get; init; } = 54245;
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
    public Dictionary<string, AudioDeviceOptions> AudioDevices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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
    public Dictionary<string, PhotoBooth.Networking.ControllerSharedDefinitions> ControllerDefinitions { get; init; } = new();
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
    public VisualizationOptions Visualization { get; init; } = new();
    public BlackKeyOptions BlackKey { get; init; } = new();

    public void Validate()
    {
        ValidateNetwork();
        if (InventoryPort is < 1 or > 65535) throw new ArgumentException("InventoryPort must be between 1 and 65535.");
        if (Visualization is null) throw new ArgumentException("Visualization must be an object.");
        Visualization.Validate();
        if (MediaDiscovery is null) throw new ArgumentException("MediaDiscovery must be an object.");
        MediaDiscovery.Validate();
        AudioDeviceSelection.ValidateConfiguration(VirtualAudioDevices, AudioDeviceLatencyMilliseconds);
        AudioDeviceOptions.Validate(AudioDevices);
        if (ConfigurationVersion != ConfigurationSchema.CurrentVersion)
            throw new ArgumentException("ConfigurationVersion must match the supported schema version.");
        if (!double.IsFinite(RequestDeduplicationSeconds) || RequestDeduplicationSeconds is < 0 or > 3600)
            throw new ArgumentException("RequestDeduplicationSeconds must be between zero and 3600.");
        if (!double.IsFinite(PointHitDiameterPercent) || PointHitDiameterPercent is <= 0 or > 100)
            throw new ArgumentException("PointHitDiameterPercent must be greater than zero and at most 100.");
        if (DisplayScreens is null)
            throw new ArgumentException("DisplayScreens must be an object.");
        if (ControllerDefinitions is null || ControllerDefinitions.Count > 128) throw new ArgumentException("Invalid controller definitions cache.");
        foreach (var entry in ControllerDefinitions)
        {
            if (entry.Value is null || entry.Key != entry.Value.ControllerId) throw new ArgumentException("Controller cache identity mismatch.");
            entry.Value.Validate();
        }
        if (DisplayScreens.Values.Where(screens => screens is not null).Sum(screens => screens.Count(s => s?.IsVisualization == true)) > 1)
            throw new ArgumentException("Only one fullscreen visualization screen is supported per Experience instance.");
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
                PhotoBooth.Networking.RoleList.Parse(screen.Roles);
                if (screen.MediaFile is not null)
                {
                    ScreenOptions.ValidateMediaFile(screen.MediaFile);
                    if (!screen.IsVideo) throw new ArgumentException("MediaFile bindings belong only to video/image screens.");
                }
                if (screen.DefaultName is not null) ScreenOptions.ValidateName(screen.DefaultName);
                if (screen.Type is not ("video" or "mat" or "visualization" or "fastled"))
                    throw new ArgumentException("Screen type must be video, mat, visualization or fastled.");
                if (screen.IsNegative && !screen.IsMat)
                    throw new ArgumentException("Only mat screens support IsNegative.");
                if (!double.IsFinite(screen.Brightness) || screen.Brightness is < 0 or > 100)
                    throw new ArgumentException("Screen Brightness must be a percentage from 0 to 100.");
                if (screen.Points is null)
                    throw new ArgumentException("Each screen requires points.");
                if (screen.IsVisualization && screen.Perspective is not null)
                    throw new ArgumentException("Visualization screens cannot have a source perspective.");
                screen.BlackKey?.Validate();
                screen.Perspective?.Validate();
                if (screen.IsLed != (screen.Perspective?.IsLed == true))
                    throw new ArgumentException("LED perspectives belong only to fastled screens; each LED screen needs a perspective.");
                if (screen.IsLed) LedGeometry.Validate(screen.Points, screen.IsLedArray);
                else if (screen.IsPolygon) screen.Points.ValidatePolygon();
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
        MediaPath.ResolveDirectory(VideoDirectory);
        MediaPath.ResolveDirectory(AudioDirectory);
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
