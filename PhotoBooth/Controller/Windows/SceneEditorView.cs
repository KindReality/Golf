using Controller.Configuration;
using Controller.Scheduling;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class SceneEditorView : UserControl
{
    private readonly TextBox _name, _destination;
    private readonly ComboBox _ambient, _entry, _after, _next;
    private readonly Label _estimate = new() { Text = "Estimating from activation media…", AccessibleName = "Scene transition estimate",
        ForeColor = DarkTheme.Accent, Height = 56, Dock = DockStyle.Top, Padding = new(0, 6, 0, 8), AutoEllipsis = true };
    private CancellationTokenSource? _estimateStop;
    private static ComboBox OptionalChoice(IEnumerable<string> names, string? selected)
    {
        var box = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; DarkTheme.Choices(box);
        // Use the same string items as other native dropdowns. The first index,
        // rather than its display text, represents an unassigned playlist.
        box.Items.Add("(None)"); foreach (var name in names) box.Items.Add(name);
        box.SelectedIndex = selected is null ? 0 : Enumerable.Range(1, box.Items.Count - 1)
            .First(i => ControllerOptions.Same(box.Items[i] as string, selected));
        return box;
    }
    private static string? OptionalSelection(ComboBox box) => box.SelectedIndex switch
    {
        0 => null,
        > 0 => box.Items[box.SelectedIndex] as string ?? throw new ArgumentException("Choose a playlist."),
        _ => throw new ArgumentException("Choose a playlist or (None).")
    };
    internal SceneEditorView(SceneOptions scene, ShowConfiguration show, bool startup, Action changed, Action<string> openPlaylist, Action<string>? createPlaylist = null,
        Func<SceneOptions, ShowConfiguration, CancellationToken, Task<SceneTransitionEstimate>>? estimateTransition = null)
    {
        Dock = DockStyle.Fill; BackColor = DarkTheme.Surface;
        var fields = new ShowEditorFields();
        fields.Wide(Note(startup ? "STARTUP SCENE  /  Used when Controller starts" : "SCENE  /  Selecting this scene does not activate it"));
        _name = TextField(scene.Name); fields.Field("Scene name", _name);
        var storyboards = show.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name).ToArray();
        _entry = OptionalChoice(storyboards, scene.ActivatingPlaylist);
        _ambient = Choice(show.Playlists.Where(p => p.Type == "ambient").Select(p => p.Name), scene.AmbientPlaylist);
        _after = Choice(["Run ambient playlist", "Go to scene"], scene.Transition is null ? "Run ambient playlist" : "Go to scene");
        _next = Choice(show.Scenes.Where(s => !ControllerOptions.Same(s.Name, scene.Name)).Select(s => s.Name), scene.Transition?.Scene);
        // A named remote destination can define a scene absent from this Controller.
        _next.DropDownStyle = ComboBoxStyle.DropDown;
        if (scene.Transition is { } transition) _next.Text = transition.Scene;
        _destination = TextField(scene.Transition?.Device ?? "*");
        _destination.PlaceholderText = "* = all devices";
        fields.Wide(Note("01  /  ON ACTIVATION")); fields.Field("Activation storyboard", _entry);
        fields.Wide(Actions("activation", () => OptionalSelection(_entry)));
        fields.Wide(Note("02  /  AFTER ACTIVATION")); fields.Field("After activation", _after);
        var ambientFields = new ShowEditorFields(); ambientFields.Field("Ambient playlist", _ambient);
        ambientFields.Wide(Actions("ambient", () => _ambient.SelectedItem as string));
        var transitionFields = new ShowEditorFields();
        transitionFields.Field("Next scene", _next);
        transitionFields.Field("Destination device", _destination);
        transitionFields.Wide(_estimate);
        var afterFields = new Panel { Dock = DockStyle.Top };
        afterFields.Controls.Add(ambientFields); afterFields.Controls.Add(transitionFields);
        fields.Wide(afterFields);
        fields.Wide(Note("Playlists are shared references. Editing one affects every scene that uses it."));
        Controls.Add(ScrollContainer(fields));
        foreach (var control in new Control[] { _name, _ambient, _entry, _after, _next, _destination }) Watch(control, changed);
        _after.SelectedIndexChanged += (_, _) => UpdateMode();
        _entry.SelectedIndexChanged += (_, _) => RefreshEstimate();
        transitionFields.SizeChanged += (_, _) => { if (_after.SelectedIndex == 1) afterFields.Height = transitionFields.Height; };
        ambientFields.SizeChanged += (_, _) => { if (_after.SelectedIndex == 0) afterFields.Height = ambientFields.Height; };
        UpdateMode();
        void UpdateMode()
        {
            var transitioning = _after.SelectedIndex == 1;
            ambientFields.Visible = !transitioning; transitionFields.Visible = transitioning;
            afterFields.Height = transitioning ? transitionFields.Height : ambientFields.Height;
            if (transitioning) RefreshEstimate(); else _estimateStop?.Cancel();
        }
        async void RefreshEstimate()
        {
            _estimateStop?.Cancel(); _estimateStop?.Dispose();
            if (_after.SelectedIndex != 1) return;
            var stop = _estimateStop = new(); var token = stop.Token;
            _estimate.Text = "Estimating from activation media…";
            try
            {
                var draft = scene with { ActivatingPlaylist = OptionalSelection(_entry), AmbientPlaylist = null,
                    Transition = new() { Scene = _next.Text, Device = _destination.Text } };
                var estimate = estimateTransition is not null ? await estimateTransition(draft, show, token) :
                    await SceneTransitionTiming.EstimateAsync(draft, new() { Scenes = show.Scenes, Playlists = show.Playlists }, MediaDurations.ReadMilliseconds, token);
                if (IsDisposed || token.IsCancellationRequested) return;
                _estimate.Text = $"Transition after {estimate.DurationMilliseconds / 1000:0.###} s from scene start" +
                    (estimate.HasUnknownMedia ? " • Some media durations are unavailable." : " • Bounded by the activation playlist’s duration limits.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsDisposed || token.IsCancellationRequested) return;
                _estimate.Text = "Unable to estimate playlist timing: " + ex.Message;
            }
        }
        Control Actions(string phase, Func<string?> selected)
        {
            var edit = Button("Edit " + phase + " playlist", () => { if (selected() is { } name) openPlaylist(name); }, phase == "ambient");
            return createPlaylist is null ? edit : Toolbar(edit, Button("New " + phase + " playlist", () => createPlaylist(phase)));
        }
    }
    internal SceneOptions Read() => new() { Name = _name.Text.Trim(),
        AmbientPlaylist = _after.SelectedIndex == 0 ? _ambient.SelectedItem as string ?? throw new ArgumentException("Choose an ambient playlist.") : null,
        Transition = _after.SelectedIndex == 1 ? new() { Scene = string.IsNullOrWhiteSpace(_next.Text) ? throw new ArgumentException("Choose a next scene.") : _next.Text.Trim(),
            Device = _destination.Text.Trim() } : null,
        ActivatingPlaylist = OptionalSelection(_entry) };
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _estimateStop?.Cancel(); _estimateStop?.Dispose(); }
        base.Dispose(disposing);
    }
}
