using Controller.Configuration;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class SceneEditorView : UserControl
{
    private readonly TextBox _name;
    private readonly ComboBox _ambient, _entry, _exit;
    private sealed record OptionalPlaylist(string? Name) { public override string ToString() => Name ?? "None"; }
    private static ComboBox OptionalChoice(IEnumerable<string> names, string? selected)
    {
        var box = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; DarkTheme.Choices(box);
        box.Items.Add(new OptionalPlaylist(null)); foreach (var name in names) box.Items.Add(new OptionalPlaylist(name));
        box.SelectedItem = box.Items.Cast<OptionalPlaylist>().First(p => p.Name is null && selected is null || p.Name is not null && ControllerOptions.Same(p.Name, selected));
        return box;
    }
    internal SceneEditorView(SceneOptions scene, ShowConfiguration show, bool startup, Action changed, Action<string> openPlaylist)
    {
        Dock = DockStyle.Fill; BackColor = DarkTheme.Surface;
        var fields = new ShowEditorFields();
        fields.Wide(Note(startup ? "STARTUP SCENE  /  Used when Controller starts" : "SCENE  /  Selecting this scene does not activate it"));
        _name = TextField(scene.Name); fields.Field("Scene name", _name);
        var storyboards = show.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name).ToArray();
        _entry = OptionalChoice(storyboards, scene.ActivatingPlaylist);
        _ambient = Choice(show.Playlists.Where(p => p.Type == "ambient").Select(p => p.Name), scene.AmbientPlaylist);
        _exit = OptionalChoice(storyboards, scene.DeactivatingPlaylist);
        fields.Wide(Note("01  /  ON ACTIVATION")); fields.Field("Activation storyboard", _entry);
        fields.Wide(Button("Edit activation playlist", () => { if (_entry.SelectedIndex > 0) openPlaylist(_entry.Text); }));
        fields.Wide(Note("02  /  WHILE ACTIVE")); fields.Field("Ambient playlist", _ambient);
        fields.Wide(Button("Edit ambient playlist", () => { if (_ambient.Text.Length > 0) openPlaylist(_ambient.Text); }, true));
        fields.Wide(Note("03  /  ON DEACTIVATION")); fields.Field("Deactivation storyboard", _exit);
        fields.Wide(Button("Edit deactivation playlist", () => { if (_exit.SelectedIndex > 0) openPlaylist(_exit.Text); }));
        fields.Wide(Note("Playlists are shared references. Editing one affects every scene that uses it."));
        Controls.Add(ScrollContainer(fields));
        foreach (var control in new Control[] { _name, _ambient, _entry, _exit }) Watch(control, changed);
    }
    internal SceneOptions Read() => new() { Name = _name.Text.Trim(), AmbientPlaylist = _ambient.Text,
        ActivatingPlaylist = (_entry.SelectedItem as OptionalPlaylist)?.Name, DeactivatingPlaylist = (_exit.SelectedItem as OptionalPlaylist)?.Name };
}
