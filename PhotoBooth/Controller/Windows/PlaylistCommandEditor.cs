using System.Text.Json;
using System.Text.Json.Nodes;
using Controller.Configuration;
using Controller.Networking;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class PlaylistCommandEditor : UserControl
{
    private readonly ComboBox _kind;
    private readonly ShowEditorFields _fields;
    private readonly bool _manual;
    private readonly ShowConfiguration _show;
    private readonly ControllerOptions _options;
    private readonly ExperiencePeer[] _peers;
    private readonly string[] _media;
    private readonly string[] _serialDevices;
    private readonly Action _changed;
    private PlaylistCommand _original;
    private string _loadedKind;
    private ComboBox? _device, _value, _target;
    private CheckBox? _loop, _mute;
    private NumericUpDown? _volume, _channel;
    private TextBox? _raw, _event, _input;
    private bool _loading;
    internal PlaylistCommandEditor(PlaylistCommand command, ShowConfiguration show, ControllerOptions options, ExperiencePeer[] peers, string[] media, Action changed, string[]? serialDevices = null, bool manual = false)
    {
        _manual = manual; _fields = new(manual ? 2 : 1);
        _show = show; _options = options; _peers = peers; _media = media; _changed = changed; _original = command;
        _serialDevices = options.Arduinos.Select(a => a.Name).Concat(serialDevices ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Dock = DockStyle.Top; AutoSize = false; Height = 480; BackColor = DarkTheme.Surface;
        _loadedKind = Kind(command);
        string[] kinds = ["Video", "Audio", "Scene command", "Local playlist", "Network playlist", "Event", "Arduino", "Advanced JSON"];
        _kind = Choice(manual ? _loadedKind == "Event" ? ["Event"] : kinds.Where(k => k != "Event") : kinds, _loadedKind);
        if (manual) { Dock = DockStyle.Fill; Controls.Add(ScrollContainer(_fields)); }
        else { var layout = new ShowEditorFields(); layout.Field("Command type", _kind); layout.Wide(_fields); Controls.Add(layout); }
        Render();
        _kind.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            // Changing types is deliberate. Advanced JSON starts from the current form when valid.
            if (_kind.Text == "Advanced JSON")
            {
                try { _original = ReadKind(_loadedKind); }
                catch (Exception) { _original = command; }
            }
            else _original = DefaultFor(_kind.Text);
            _loadedKind = _kind.Text; Render(); _changed();
        };
    }
    private static string Kind(PlaylistCommand c)
    {
        if (c.Playlist is not null) return "Local playlist";
        if (c.Transport == "arduino") return "Arduino";
        if (c.Type == "event") return "Event";
        if (ControllerOptions.Text(c.Payload, "command") is { } cmd) return cmd == "scene" ? "Scene command" : cmd == "playlist" ? "Network playlist" : "Advanced JSON";
        var p = c.Payload;
        if (!string.IsNullOrWhiteSpace(ControllerOptions.Text(p, "roles"))) return "Advanced JSON";
        if (p.ValueKind == JsonValueKind.Object && p.EnumerateObject().Any(f => f.Name.Equals("audioDevices", StringComparison.OrdinalIgnoreCase))) return "Advanced JSON";
        return ControllerOptions.Text(p, "experience") is "video" ? "Video" : ControllerOptions.Text(p, "experience") is "audio" ? "Audio" : "Advanced JSON";
    }
    private PlaylistCommand DefaultFor(string kind) => kind switch
    {
        "Video" => Application.ShowEditSession.DefaultCommand(),
        "Audio" => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "audio", value = "", audioDevice = "*", volume = 100 }) },
        "Local playlist" => new() { Playlist = _show.Playlists.FirstOrDefault(p => p.Type == "storyboard")?.Name ?? "" },
        "Scene command" => new() { Device = _options.EffectiveDeviceName, Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = _show.Scenes.FirstOrDefault()?.Name ?? "" }) },
        "Network playlist" => new() { Device = _options.EffectiveDeviceName, Payload = JsonSerializer.SerializeToElement(new { command = "playlist", value = _show.Playlists.FirstOrDefault(p => p.Type == "storyboard")?.Name ?? "" }) },
        "Event" => new() { Type = "event", Device = "*", Payload = JsonSerializer.SerializeToElement(new { input = "motion", @event = "motion.detected" }) },
        "Arduino" => new() { Transport = "arduino", Device = _serialDevices.FirstOrDefault() ?? "", Payload = JsonSerializer.SerializeToElement(new { command = "status" }) },
        _ => Application.ShowEditSession.DefaultCommand()
    };
    private JsonObject Payload() => _original.Payload.ValueKind == JsonValueKind.Object ? JsonNode.Parse(_original.Payload.GetRawText(), new() { PropertyNameCaseInsensitive = true })!.AsObject() : new();
    private void Render()
    {
        _loading = true;
        if (_manual) _kind.Parent?.Controls.Remove(_kind);
        foreach (Control c in _fields.Controls.Cast<Control>().ToArray()) c.Dispose();
        _fields.Controls.Clear(); _fields.RowStyles.Clear(); _fields.RowCount = 0;
        _device = _value = _target = null; _loop = _mute = null; _volume = _channel = null; _raw = _event = _input = null;
        string T(string name, string fallback = "") => ControllerOptions.Text(_original.Payload, name) ?? fallback;
        JsonNode? Node(string name) => Payload()[name];
        void Field(string label, Control c) { _fields.Field(label, c); Watch(c, () => { if (!_loading) _changed(); }); }
        if (_manual && _loadedKind != "Event") _fields.Field("Command type", _kind);
        if (_loadedKind == "Advanced JSON")
        {
            _raw = TextField(JsonSerializer.Serialize(_original, ControllerOptions.Json), true); _raw.Font = new("Consolas", 9); _raw.Height = 240;
            Field("Command JSON", _raw); _fields.Wide(Note("Complete command object: Transport, Type, Device, Payload, or a local Playlist reference. Existing custom fields are preserved."));
        }
        else if (_loadedKind == "Local playlist")
        { _value = Choice(_show.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name), _original.Playlist); Field("Storyboard", _value); }
        else
        {
            var devices = new[] { "*", _options.EffectiveDeviceName }.Concat(_peers.Select(p => p.Inventory.DeviceName));
            if (_loadedKind == "Arduino") devices = _serialDevices;
            _device = Choice(devices, _original.Device ?? "*", true); Field("Destination device", _device);
            if (_loadedKind is "Video" or "Audio")
            {
                var audio = _loadedKind == "Audio";
                var extensions = audio ? Application.LivePlayback.Audio : Application.LivePlayback.Videos;
                _value = Choice(_media.Where(f => extensions.Contains(Path.GetExtension(f))), T("value"), true); Field("Media file", _value);
                var targets = new[] { "*" }.Concat(audio ? _peers.SelectMany(p => p.Inventory.Speakers.Select(s => s.Name)) : _peers.SelectMany(p => p.Inventory.Screens.Where(s => s.Kind == "video").Select(s => s.Display + "/" + s.Name)));
                _target = Choice(targets, T(audio ? "audioDevice" : "screen", "*"), true); Field(audio ? "Audio output" : "Screen", _target);
                _volume = Number(Node("volume")?.GetValue<decimal>() ?? 100, 100); Field("Volume (%)", _volume);
                _mute = Check("Mute", Node("mute")?.GetValue<bool>() ?? false); Watch(_mute, _changed);
                if (!audio) { _loop = Check("Loop video", Node("loop")?.GetValue<bool>() ?? false); Watch(_loop, _changed); }
                if (_manual)
                {
                    var playback = new FlowLayoutPanel { Height = 32, WrapContents = false };
                    playback.Controls.Add(_mute); if (_loop is not null) playback.Controls.Add(_loop);
                    _fields.Field("Playback", playback);
                }
                else { _fields.Wide(_mute); if (_loop is not null) _fields.Wide(_loop); }
                if (audio) { _channel = Number(Node("channel")?.GetValue<int>() ?? 0, 128); Field("Channel (0 = original)", _channel); }
                if (!_manual) _fields.Wide(Note("Item roles override direct outputs. Paths are relative to the local media library. Advanced JSON supports coordinated audioDevices arrays and other parameters."));
            }
            else if (_loadedKind is "Scene command" or "Network playlist")
            { _value = Choice(_loadedKind == "Scene command" ? _show.Scenes.Select(s => s.Name) : _show.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name), T("value"), true); Field(_loadedKind == "Scene command" ? "Scene" : "Playlist", _value); }
            else if (_loadedKind == "Event")
            { _input = TextField(T("input")); _event = TextField(T("event")); Field("Input", _input); Field("Event name", _event); }
            else
            { _raw = TextField(_original.Payload.GetRawText(), true); _raw.Font = new("Consolas", 9); Field("Arduino payload JSON", _raw); }
        }
        _loading = false;
    }
    internal PlaylistCommand Read() => ReadKind(_loadedKind);
    private PlaylistCommand ReadKind(string kind)
    {
        if (kind == "Advanced JSON") return JsonSerializer.Deserialize<PlaylistCommand>(_raw!.Text, ControllerOptions.Json) ?? throw new ArgumentException("Missing command JSON.");
        if (kind == "Local playlist") return new() { Playlist = _value!.Text };
        var body = Payload();
        if (kind is "Video" or "Audio")
        {
            var file = _value!.Text.Trim(); PhotoBooth.Media.MediaLibrary.Normalize(file);
            body["value"] = file; body[kind == "Audio" ? "audioDevice" : "screen"] = _target!.Text.Trim();
            body["mute"] = _mute!.Checked; body["volume"] = _volume!.Value;
            if (kind == "Video") body["loop"] = _loop!.Checked;
            else { if (_channel!.Value > 0) body["channel"] = (int)_channel.Value; else body.Remove("channel"); }
        }
        else if (kind is "Scene command" or "Network playlist") body["value"] = _value!.Text.Trim();
        else if (kind == "Event") { body["input"] = _input!.Text.Trim(); body["event"] = _event!.Text.Trim(); }
        else body = JsonNode.Parse(_raw!.Text) as JsonObject ?? throw new ArgumentException("Arduino payload must be a JSON object.");
        return _original with { Device = _device!.Text.Trim(), Payload = JsonSerializer.SerializeToElement(body) };
    }
}
