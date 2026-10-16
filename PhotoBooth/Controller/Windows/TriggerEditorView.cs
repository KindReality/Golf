using Controller.Configuration;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class TriggerEditorView : UserControl
{
    private readonly TextBox _name, _source, _input, _event;
    private readonly ComboBox _kind, _playlist;
    private readonly CheckBox _enabled, _defaultCooldown;
    private readonly NumericUpDown _interval, _cooldown;
    internal TriggerEditorView(TriggerOptions value, ShowConfiguration show, int defaultCooldown, Action changed, Action<string> openPlaylist)
    {
        Dock = DockStyle.Fill;
        var fields = new ShowEditorFields();
        fields.Wide(Note("TRIGGER  /  Starts a storyboard; it is not a timeline item"));
        _name = TextField(value.Name); fields.Field("Trigger name", _name);
        _enabled = Check("Enabled", value.Enabled); fields.Wide(_enabled);
        _kind = Choice(["Network event", "Timer"], value.IntervalMilliseconds is null ? "Network event" : "Timer"); fields.Field("Listen for", _kind);
        _source = TextField(value.SourceDevice ?? "sensor"); fields.Field("Source device", _source);
        _input = TextField(value.Input ?? "motion"); fields.Field("Input", _input);
        _event = TextField(value.Event ?? "motion.detected"); fields.Field("Event", _event);
        _interval = Number(value.IntervalMilliseconds ?? 10000); _interval.Minimum = 1; fields.Field("Interval (ms)", _interval);
        _playlist = Choice(show.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name), value.Playlist); fields.Field("Storyboard playlist", _playlist);
        fields.Wide(Button("Edit storyboard playlist", () => { if (_playlist.Text.Length > 0) openPlaylist(_playlist.Text); }));
        _defaultCooldown = Check($"Use environment cooldown ({defaultCooldown:N0} ms)", value.CooldownMilliseconds is null); fields.Wide(_defaultCooldown);
        _cooldown = Number(value.CooldownMilliseconds ?? defaultCooldown); fields.Field("Cooldown (ms)", _cooldown);
        fields.Wide(Note("A trigger skips another run while its previous run is busy or cooling down."));
        void State() { var timed = _kind.Text == "Timer"; _source.Enabled = _input.Enabled = _event.Enabled = !timed; _interval.Enabled = timed; _cooldown.Enabled = !_defaultCooldown.Checked; }
        _kind.SelectedIndexChanged += (_, _) => State(); _defaultCooldown.CheckedChanged += (_, _) => State(); State();
        foreach (var c in new Control[] { _name, _enabled, _kind, _source, _input, _event, _interval, _playlist, _defaultCooldown, _cooldown }) Watch(c, changed);
        Controls.Add(ScrollContainer(fields));
    }
    internal TriggerOptions Read() => new() { Name = _name.Text.Trim(), Enabled = _enabled.Checked, Playlist = _playlist.Text,
        IntervalMilliseconds = _kind.Text == "Timer" ? (int)_interval.Value : null, SourceDevice = _kind.Text == "Timer" ? null : _source.Text.Trim(),
        Input = _kind.Text == "Timer" ? null : _input.Text.Trim(), Event = _kind.Text == "Timer" ? null : _event.Text.Trim(), CooldownMilliseconds = _defaultCooldown.Checked ? null : (int)_cooldown.Value };
}
