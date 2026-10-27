using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoBooth.Networking;

namespace Controller.Configuration;

internal sealed record BuildEnvironment(string Environment, int DefaultTriggerCooldownMilliseconds)
{
    public static BuildEnvironment Load(string path)
    {
        var value = JsonSerializer.Deserialize<BuildEnvironment>(File.ReadAllText(path)) ?? throw new ArgumentException("Missing build environment.");
        if (value.Environment is not ("Development" or "Production") || value.DefaultTriggerCooldownMilliseconds != (value.Environment == "Production" ? 10000 : 1000))
            throw new ArgumentException("Build environment must be Development (1000 ms) or Production (10000 ms).");
        return value;
    }
}

internal sealed record ControllerOptions : BroadcastOptions
{
    public ControllerOptions() => UdpSendAttempts = 1;
    public string ControllerId { get; init; } = "";
    public int DefinitionsPort { get; init; } = 54244;
    public bool ConfigurationMode { get; init; }
    public bool ExperienceSupervisionEnabled { get; init; } = true;
    public double PointHitDiameterPercent { get; init; } = 5;
    public PerspectiveDefinition[] Perspectives { get; init; } = [];
    public string? DefaultDisplayName { get; init; }
    public Dictionary<string, string> DisplayAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public ArduinoOptions[] Arduinos { get; init; } = [];
    public ArduinoDiscoveryOptions ArduinoDiscovery { get; init; } = new();
    public TriggerOptions[] Triggers { get; init; } = [];
    public SceneOptions[] Scenes { get; init; } = [];
    public PlaylistOptions[] Playlists { get; init; } = [];
    public PinnedActionOptions[] PinnedActions { get; init; } = [];
    public PeerDiscoveryOptions PeerDiscovery { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Telemetry { get; init; }
    [JsonIgnore] public BuildEnvironment Build { get; init; } = new("Development", 1000);
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    public static ControllerOptions Parse(string text, BuildEnvironment environment)
    {
        using var document = JsonDocument.Parse(text); RejectDuplicates(document.RootElement);
        var value = JsonSerializer.Deserialize<ControllerOptions>(text, Json) ?? throw new ArgumentException("Configuration must be an object.");
        value = value with { Build = environment }; value.Validate(); return value;
    }
    public PlaylistOptions Playlist(string name, string? type = null) => Playlists.FirstOrDefault(p => Same(p.Name, name) && (type is null || p.Type == type)) ?? throw new ArgumentException($"Missing {type ?? ""} playlist: {name}.");
    public SceneOptions Scene(string name) => Scenes.FirstOrDefault(s => Same(s.Name, name)) ?? throw new ArgumentException($"Unknown scene: {name}.");
    private void Validate()
    {
        ValidateNetwork();
        if (PeerDiscovery is null) throw new ArgumentException("PeerDiscovery must be an object.");
        PeerDiscovery.Validate();
        if (PinnedActions is null || PinnedActions.Length > 128 || PinnedActions.Any(p => p is null)) throw new ArgumentException("PinnedActions must contain at most 128 actions.");
        if (PinnedActions.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != PinnedActions.Length) throw new ArgumentException("Choose a unique pinned action name.");
        if (PinnedActions.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != PinnedActions.Length) throw new ArgumentException("Duplicate pinned action ID.");
        foreach (var pin in PinnedActions) pin.Validate(this);
        if (ArduinoDiscovery is null || ArduinoDiscovery.BaudRate is < 300 or > 2000000)
            throw new ArgumentException("Invalid ArduinoDiscovery serial settings.");
        if (!double.IsFinite(PointHitDiameterPercent) || PointHitDiameterPercent is <= 0 or > 100) throw new ArgumentException("PointHitDiameterPercent must be greater than zero and at most 100.");
        if (DefinitionsPort is < 1 or > 65535) throw new ArgumentException("DefinitionsPort must be 1–65535.");
        new ControllerSharedDefinitions { ControllerId = string.IsNullOrWhiteSpace(ControllerId) ? "00000000000000000000000000000000" : ControllerId, ControllerName = EffectiveDeviceName, Hostname = Environment.MachineName, Perspectives = Perspectives }.Validate();
        ControllerDisplaySelection.Validate(DefaultDisplayName, DisplayAliases);
        if (UdpPort is not null) throw new ArgumentException("Use UdpEventsPort and UdpCommandsPort.");
        if (Arduinos is null || Triggers is null || Playlists is null || Scenes is null || Arduinos.Any(a => a is null) || Triggers.Any(t => t is null) || Playlists.Any(p => p is null) || Scenes.Any(s => s is null) || Arduinos.Length > 128 || Triggers.Length > 4096 || Playlists.Length > 4096 || Scenes.Length > 4096)
            throw new ArgumentException("Configuration lists require objects; maximum 128 Arduinos and 4096 triggers/playlists/scenes.");
        Unique(Arduinos.Select(a => a.Name), "Arduino"); Unique(Arduinos.Select(a => a.Port), "serial port");
        Unique(Triggers.Select(t => t.Name), "trigger"); Unique(Playlists.Select(p => p.Name), "playlist"); Unique(Scenes.Select(s => s.Name), "scene");
        foreach (var arduino in Arduinos)
            if (!arduino.Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || !int.TryParse(arduino.Port.AsSpan(3), out var number) || number <= 0 || arduino.BaudRate is < 300 or > 2000000) throw new ArgumentException($"Invalid serial settings: {arduino.Name}.");
        foreach (var playlist in Playlists)
        {
            if (!Same(playlist.AudioOutputMode, "Configured") && !Same(playlist.AudioOutputMode, "Random"))
                throw new ArgumentException($"AudioOutputMode must be Configured or Random: {playlist.Name}.");
            if (playlist.DurationMilliseconds is < 0 or > 86400000) throw new ArgumentException($"Playlist duration must be 0–86400000 milliseconds or empty for automatic: {playlist.Name}.");
            if (playlist.Type == "storyboard")
            {
                if (playlist.DurationMilliseconds is not null) throw new ArgumentException("Storyboard playlists use minimum and maximum duration.");
                if (playlist.MinimumDurationMilliseconds is < 0 or > 86400000 || playlist.MaximumDurationMilliseconds is < 1 or > 86400000 ||
                    playlist.MaximumDurationMilliseconds is { } maximum && maximum < playlist.EffectiveMinimumDurationMilliseconds)
                    throw new ArgumentException($"Playlist {playlist.Name}: maximum duration must be at least the minimum; use 0–86400000 ms for minimum and 1–86400000 ms for maximum.");
            }
            else if (playlist.MinimumDurationMilliseconds is not null || playlist.MaximumDurationMilliseconds is not null)
                throw new ArgumentException("Ambient playlists use scheduling duration, not storyboard duration limits.");
            if (playlist.Type is not ("ambient" or "storyboard") || playlist.Items is null || playlist.Items.Any(i => i is null) || playlist.Items.Length > 1000) throw new ArgumentException($"Invalid playlist type/items: {playlist.Name}.");
            Unique(playlist.Items.Select(i => i.Name), $"item in {playlist.Name}");
            foreach (var item in playlist.Items)
            {
                var itemRoles = RoleList.ParseTargets(item.Roles);
                if (!Same(item.BroadcastMode, "Broadcast") && !Same(item.BroadcastMode, "Random"))
                    throw new ArgumentException($"BroadcastMode must be Broadcast or Random: {playlist.Name}/{item.Name}.");
                if (itemRoles.Length == 0 && Same(item.BroadcastMode, "Random"))
                    throw new ArgumentException($"Random targeting requires a nonempty Roles list: {playlist.Name}/{item.Name}.");
                if (Same(item.BroadcastMode, "Random") && RoleList.RandomChoices(item.Roles).Length == 0)
                    throw new ArgumentException($"Random role selection requires a positive rule or '*': {playlist.Name}/{item.Name}.");
                if (item.DelayMilliseconds is < 0 or > 86400000 || item.CooldownMilliseconds is < 0 or > 86400000 || item.Commands is null || item.Commands.Any(c => c is null) || item.Commands.Length is 0 or > 1000) throw new ArgumentException($"Invalid timing/commands: {playlist.Name}/{item.Name}.");
                if (playlist.Type == "ambient")
                {
                    if (item.DelayMilliseconds != 0 || item.MinIntervalMilliseconds is null or < 1 or > 86400000 || item.MaxIntervalMilliseconds is null or < 1 or > 86400000 || item.MaxIntervalMilliseconds < item.MinIntervalMilliseconds || item.CooldownMilliseconds > item.MaxIntervalMilliseconds) throw new ArgumentException($"Ambient intervals must be valid and include cooldown: {playlist.Name}/{item.Name}.");
                }
                else if (item.MinIntervalMilliseconds is not null || item.MaxIntervalMilliseconds is not null) throw new ArgumentException($"Storyboard items use delays, not frequency: {playlist.Name}/{item.Name}.");
                foreach (var command in item.Commands)
                {
                    if (command.Playlist is { } nested)
                    {
                        Playlist(nested, "storyboard");
                        if (command.Device is not null || command.Payload.ValueKind != JsonValueKind.Undefined || command.Transport != "udp" || command.Type != "command") throw new ArgumentException("Local Playlist references cannot include transport/payload fields.");
                        continue;
                    }
                    if (command.Transport is not ("udp" or "arduino") || command.Type is not ("command" or "event") || command.Payload.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Invalid command: {playlist.Name}/{item.Name}.");
                    if (VideoFadeCommand.IsFade(Text(command.Payload, "command"))) VideoFadeCommand.Validate(command.Payload);
                    VideoPlaybackSpeed.Read(command.Payload);
                    if (command.Transport == "arduino" && (command.Type != "command" || command.Device == "*" || string.IsNullOrWhiteSpace(Text(command.Payload, "command"))))
                        throw new ArgumentException("Serial commands require a named device and a command. The device may be discovered later.");
                    // Check every possible random destination rather than treating the entire list as one packet.
                    foreach (var roles in Same(item.BroadcastMode, "Random") ? RoleList.RandomChoices(item.Roles) : [item.Roles])
                    {
                        var routed = PlaylistRoleTarget.Apply(command, roles);
                        if (Same(playlist.AudioOutputMode, "Random") && Controller.Scheduling.PlaylistAudioTarget.IsAudio(routed))
                            Controller.Scheduling.PlaylistAudioTarget.Validate(routed);
                        BroadcastEnvelope.Create(this, routed.Type, routed.Device!, routed.Payload);
                    }
                    if (command.Type == "event") RequireInput(command.Payload);
                }
            }
        }
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name, HashSet<string> stack)
        {
            if (complete.Contains(name)) return;
            if (!stack.Add(name)) throw new ArgumentException($"Nested playlist cycle at {name}.");
            if (stack.Count > BroadcastEnvelope.MaximumHops) throw new ArgumentException("Nested playlists cannot exceed eight levels.");
            foreach (var child in Playlist(name).Items.SelectMany(i => i.Commands).Where(c => c.Playlist is not null)) Visit(child.Playlist!, stack);
            stack.Remove(name); complete.Add(name);
        }
        foreach (var playlist in Playlists) Visit(playlist.Name, new(StringComparer.OrdinalIgnoreCase));
        foreach (var scene in Scenes)
        {
            if ((scene.AmbientPlaylist is not null) == (scene.Transition is not null))
                throw new ArgumentException($"Scene {scene.Name} must choose either an ambient playlist or a next scene.");
            if (scene.AmbientPlaylist is { } ambient) Playlist(ambient, "ambient");
            if (scene.Transition is { } transition)
            {
                ValidateName(transition.Scene, "next scene", false);
                ValidateName(transition.Device, "scene destination", true);
                if (Same(transition.Device, "*") || Same(transition.Device, EffectiveDeviceName))
                {
                    Scene(transition.Scene);
                    if (Same(transition.Scene, scene.Name)) throw new ArgumentException("Choose a different next scene.");
                }
            }
            if (scene.ActivatingPlaylist is not null) Playlist(scene.ActivatingPlaylist, "storyboard");
        }
        foreach (var trigger in Triggers)
        {
            Playlist(trigger.Playlist, "storyboard");
            if (trigger.CooldownMilliseconds is < 0 or > 86400000 || trigger.IntervalMilliseconds is < 1 or > 86400000) throw new ArgumentException($"Invalid trigger timing: {trigger.Name}.");
            if (trigger.IntervalMilliseconds is not null)
            {
                if (trigger.SourceDevice is not null || trigger.Input is not null || trigger.Event is not null) throw new ArgumentException("A timed trigger cannot also match an event.");
            }
            else { ValidateName(trigger.SourceDevice, "SourceDevice", false); ValidateName(trigger.Input, "Input", false); ValidateName(trigger.Event, "Event", false); }
        }
    }
    public static void RequireInput(JsonElement payload) => ValidateName(Text(payload, "input"), "input", false);
    internal static string? Text(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.EnumerateObject().FirstOrDefault(p => Same(p.Name, name)).Value is { ValueKind: JsonValueKind.String } value ? value.GetString()?.Trim() : null;
    internal static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static void Unique(IEnumerable<string> values, string kind)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in values) { ValidateName(name, kind, false); if (name != name.Trim() || name.Contains('/') || !names.Add(name)) throw new ArgumentException($"Duplicate/invalid {kind}: {name}."); }
    }
    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject()) { if (!names.Add(field.Name)) throw new ArgumentException($"Duplicate field: {field.Name}."); RejectDuplicates(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
