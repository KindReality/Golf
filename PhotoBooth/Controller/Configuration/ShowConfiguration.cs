using System.Text.Json;

namespace Controller.Configuration;

// Only the editable show graph. Network, hardware, perspectives and machine settings stay outside it.
internal sealed record ShowConfiguration(SceneOptions[] Scenes, PlaylistOptions[] Playlists, TriggerOptions[] Triggers)
{
    internal static ShowConfiguration From(ControllerOptions options) => new(options.Scenes, options.Playlists, options.Triggers);
    internal string Signature => JsonSerializer.Serialize(this, ControllerOptions.Json);
    internal ControllerOptions ApplyTo(ControllerOptions options) => options with { Scenes = Scenes, Playlists = Playlists, Triggers = Triggers };
    internal void Validate(ControllerOptions options)
    {
        if (Scenes.Length == 0) throw new ArgumentException("Keep at least one scene. The first scene is used at startup.");
        ControllerOptions.Parse(JsonSerializer.Serialize(ApplyTo(options), ControllerOptions.Json), options.Build);
    }
}
