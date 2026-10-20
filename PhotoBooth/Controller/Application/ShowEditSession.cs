using Controller.Configuration;
using System.Text.Json;

namespace Controller.Application;

// Draft changes never reach the scheduler until the whole graph passes validation and is saved.
internal sealed class ShowEditSession(ControllerOptions options)
{
    private readonly List<ShowConfiguration> _undo = [];
    private string _baselineSignature = ShowConfiguration.From(options).Signature;
    private string _signature = ShowConfiguration.From(options).Signature;
    internal ShowConfiguration Baseline { get; private set; } = ShowConfiguration.From(options);
    internal ShowConfiguration Current { get; private set; } = ShowConfiguration.From(options);
    internal bool Dirty => _signature != _baselineSignature;
    internal bool CanUndo => _undo.Count > 0;
    internal void Reset(ControllerOptions options) => Accept(ShowConfiguration.From(options));
    internal void Accept(ShowConfiguration value) { Baseline = Current = value; _baselineSignature = _signature = value.Signature; _undo.Clear(); }
    internal void Change(ShowConfiguration next)
    {
        var signature = next.Signature;
        if (signature == _signature) return;
        _undo.Add(Current); if (_undo.Count > 50) _undo.RemoveAt(0); Current = next; _signature = signature;
    }
    internal void Undo() { if (_undo.Count == 0) return; Current = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _signature = Current.Signature; }
    internal static string Unique(string prefix, IEnumerable<string> names)
    {
        var used = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(prefix)) return prefix;
        for (var i = 2; ; i++) if (!used.Contains(prefix + " " + i)) return prefix + " " + i;
    }
    private static void CheckName(string name, IEnumerable<string> others)
    {
        PhotoBooth.Networking.BroadcastOptions.ValidateName(name, "name", false);
        if (name != name.Trim() || name.Contains('/') || others.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a unique name without slashes or surrounding spaces.");
    }
    internal void SetScene(int index, SceneOptions value)
    {
        CheckName(value.Name, Current.Scenes.Where((_, i) => i != index).Select(s => s.Name));
        var old = Current.Scenes[index].Name;
        Change(Current with { Scenes = Current.Scenes.Select((s, i) => i == index ? value : s).Select(s =>
            s.Transition is { } next && ControllerOptions.Same(next.Scene, old) ? s with { Transition = next with { Scene = value.Name } } : s).ToArray() });
    }
    internal void SetPlaylist(int index, PlaylistOptions value)
    {
        CheckName(value.Name, Current.Playlists.Where((_, i) => i != index).Select(p => p.Name));
        var old = Current.Playlists[index].Name;
        string Rename(string name) => ControllerOptions.Same(old, name) ? value.Name : name;
        string? Optional(string? name) => name is null ? null : Rename(name);
        var playlists = Current.Playlists.Select((p, i) => i == index ? value : p).Select(p => p with
        { Items = p.Items.Select(item => item with { Commands = item.Commands.Select(c => c.Playlist is null ? c : c with { Playlist = Rename(c.Playlist) }).ToArray() }).ToArray() }).ToArray();
        Change(Current with { Playlists = playlists,
            Scenes = Current.Scenes.Select(s => s with { AmbientPlaylist = Optional(s.AmbientPlaylist), ActivatingPlaylist = Optional(s.ActivatingPlaylist) }).ToArray(),
            Triggers = Current.Triggers.Select(t => t with { Playlist = Rename(t.Playlist) }).ToArray() });
    }
    internal void SetTrigger(int index, TriggerOptions value)
    {
        CheckName(value.Name, Current.Triggers.Where((_, i) => i != index).Select(t => t.Name));
        Change(Current with { Triggers = Current.Triggers.Select((t, i) => i == index ? value : t).ToArray() });
    }
    internal string[] PlaylistUses(string name) => Current.Scenes.Where(s => new[] { s.AmbientPlaylist, s.ActivatingPlaylist }.Any(p => ControllerOptions.Same(p, name))).Select(s => "Scene: " + s.Name)
        .Concat(Current.Triggers.Where(t => ControllerOptions.Same(t.Playlist, name)).Select(t => "Trigger: " + t.Name))
        .Concat(Current.Playlists.Where(p => p.Items.Any(i => i.Commands.Any(c => ControllerOptions.Same(c.Playlist, name)))).Select(p => "Playlist: " + p.Name)).ToArray();
    internal void DeletePlaylist(int index)
    {
        var uses = PlaylistUses(Current.Playlists[index].Name);
        if (uses.Length > 0) throw new ArgumentException("Reassign references before deleting. Used by " + string.Join("; ", uses));
        Change(Current with { Playlists = Current.Playlists.Where((_, i) => i != index).ToArray() });
    }
    internal void DeleteScene(int index)
    {
        if (Current.Scenes.Length == 1) throw new ArgumentException("Keep at least one scene.");
        var name = Current.Scenes[index].Name;
        if (Current.Scenes.Any(s => ControllerOptions.Same(s.Transition?.Scene, name)))
            throw new ArgumentException("Reassign next-scene references before deleting this scene.");
        Change(Current with { Scenes = Current.Scenes.Where((_, i) => i != index).ToArray() });
    }
    internal void MakeStartup(int index) => Change(Current with { Scenes = new[] { Current.Scenes[index] }.Concat(Current.Scenes.Where((_, i) => i != index)).ToArray() });
    internal static PlaylistCommand DefaultCommand() => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", screen = "*", mute = true, loop = false }) };
    internal static PlaylistItem NewItem(string name, bool ambient) => new() { Name = name, MinIntervalMilliseconds = ambient ? 10000 : null, MaxIntervalMilliseconds = ambient ? 30000 : null, Commands = [DefaultCommand()] };
    // Commands on the network use literal names; only explicit local Playlist references are rewritten.
    internal static string CommandSummary(PlaylistCommand command)
    {
        if (command.Playlist is not null) return "Playlist • " + command.Playlist;
        var payload = command.Payload;
        string? Text(string field) => ControllerOptions.Text(payload, field);
        return command.Type == "event" ? "Event • " + Text("event") :
            (Text("experience") ?? Text("command") ?? "Command") + " • " + (Text("value") ?? command.Device ?? "*");
    }
}
