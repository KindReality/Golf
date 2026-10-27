using System.Text.Json;
using System.Text.Json.Serialization;
namespace Controller.Configuration;

internal sealed record SceneOptions
{
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AmbientPlaylist { get; init; }
    public string? ActivatingPlaylist { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SceneTransitionOptions? Transition { get; init; }
}
internal sealed record SceneTransitionOptions
{
    public required string Scene { get; init; }
    public string Device { get; init; } = "*";
}
internal sealed record PlaylistOptions
{
    public required string Name { get; init; }
    public string Type { get; init; } = "storyboard";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DurationMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MinimumDurationMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaximumDurationMilliseconds { get; init; }
    [JsonIgnore] internal int EffectiveMinimumDurationMilliseconds => MinimumDurationMilliseconds ?? 5000;
    public string AudioOutputMode { get; init; } = "Configured";
    public PlaylistItem[] Items { get; init; } = [];
}
internal sealed record PlaylistItem
{
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public int DelayMilliseconds { get; init; }
    public int CooldownMilliseconds { get; init; }
    public int? MinIntervalMilliseconds { get; init; }
    public int? MaxIntervalMilliseconds { get; init; }
    public string Roles { get; init; } = "";
    public string BroadcastMode { get; init; } = "Broadcast";
    public PlaylistCommand[] Commands { get; init; } = [];
}
internal sealed record PlaylistCommand
{
    [JsonIgnore] public PhotoBooth.Networking.CommandPriority Priority { get; init; } = PhotoBooth.Networking.CommandPriority.Medium;
    [JsonIgnore] public PhotoBooth.Networking.CommandContext? Context { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Playlist { get; init; }
    public string Transport { get; init; } = "udp";
    public string Type { get; init; } = "command";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Device { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Payload { get; init; }
}
internal sealed record ArduinoOptions
{
    public required string Name { get; init; }
    public required string Port { get; init; }
    public bool Enabled { get; init; } = true;
    public int BaudRate { get; init; } = 115200;
    public bool DtrEnable { get; init; } = true;
}
internal sealed record ArduinoDiscoveryOptions
{
    public bool Enabled { get; init; } = true;
    public int BaudRate { get; init; } = 115200;
}
internal sealed record TriggerOptions
{
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public string? SourceDevice { get; init; }
    public string? Input { get; init; }
    public string? Event { get; init; }
    public int? IntervalMilliseconds { get; init; }
    public int? CooldownMilliseconds { get; init; }
    public required string Playlist { get; init; }
}
