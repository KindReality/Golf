using Controller.Application;
using Controller.Configuration;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class ShowEditorPage : UserControl
{
    private readonly ControllerHost _host;
    private readonly ShowEditSession _session;
    private readonly ListView _library = List("Name", "Type");
    private readonly TextBox _search = TextField();
    private readonly Panel _workspace = new() { Dock = DockStyle.Fill, BackColor = DarkTheme.Surface };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = DarkTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _playback = new() { Dock = DockStyle.Fill, ForeColor = DarkTheme.Accent, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _save, _undo, _reload, _run, _cancel, _back;
    private readonly Dictionary<string, Button> _sections = new();
    private readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Stack<(string Section, int Index)> _history = new();
    private string _section = "Scenes";
    private int _index = -1;
    private Control? _view;
    private ControllerOptions _lastOptions;
    private bool _loading, _pending, _saving, _reloadArmed;
    private string? _runId, _confirmSceneChange, _awaiting;
    private long _awaitUntil;
    internal ShowEditSession Session => _session;
    internal bool HasUnsavedChanges => _pending || _session.Dirty;
    internal ShowEditorPage(ControllerHost host)
    {
        _host = host; _lastOptions = host.Options; _session = new(host.Options);
        Dock = DockStyle.Fill; BackColor = DarkTheme.Background; ForeColor = DarkTheme.Text; Font = new("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16, 6, 16, 12), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 56)); layout.RowStyles.Add(new(SizeType.Absolute, 46)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 44));
        var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        title.ColumnStyles.Add(new(SizeType.Percent, 40)); title.ColumnStyles.Add(new(SizeType.Percent, 60));
        title.Controls.Add(new Label { Text = "SCENES / PLAYLISTS", Font = new("Segoe UI", 17, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        title.Controls.Add(_playback, 1, 0); layout.Controls.Add(title, 0, 0);
        _save = Button("Save", () => _ = SaveAsync(), true); _undo = Button("Undo edit", Undo); _reload = Button("Reload", Reload);
        _run = Button("Activate scene", Run); _cancel = Button("Cancel run", CancelRun); _back = Button("← Back", Back);
        layout.Controls.Add(Toolbar(_back, _save, _undo, _reload, _run, _cancel), 0, 1);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Absolute, 238)); columns.ColumnStyles.Add(new(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new(0, 0, 10, 0), Margin = Padding.Empty };
        sidebar.RowStyles.Add(new(SizeType.Absolute, 44)); sidebar.RowStyles.Add(new(SizeType.Absolute, 34)); sidebar.RowStyles.Add(new(SizeType.Absolute, 100)); sidebar.RowStyles.Add(new(SizeType.Percent, 100));
        var tabs = Toolbar();
        foreach (var section in new[] { "Scenes", "Playlists", "Triggers" })
        { var b = Button(section, () => Navigate(section, 0)); b.Padding = new(5, 3, 5, 3); _sections[section] = b; tabs.Controls.Add(b); }
        _search.PlaceholderText = "Find by name…"; _search.Dock = DockStyle.Fill; _search.TextChanged += (_, _) => RenderLibrary();
        sidebar.Controls.Add(tabs, 0, 0); sidebar.Controls.Add(_search, 0, 1); sidebar.Controls.Add(_tools, 0, 2); sidebar.Controls.Add(_library, 0, 3);
        columns.Controls.Add(sidebar, 0, 0); columns.Controls.Add(_workspace, 1, 0); layout.Controls.Add(columns, 0, 2); layout.Controls.Add(_status, 0, 3); Controls.Add(layout);
        _library.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _library.SelectedItems.Count == 0) return;
            var index = (int)_library.SelectedItems[0].Tag!;
            if (index != _index) Navigate(_section, index);
        };
        _library.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { Delete(); e.SuppressKeyPress = true; } };
        _refresh.Tick += (_, _) => RefreshState();
        VisibleChanged += (_, _) => { if (Visible) { _refresh.Start(); RefreshState(); } else _refresh.Stop(); };
        Navigate("Scenes", 0); RefreshState();
        Message("Select a scene or playlist to edit. Playback is separate from selection.");
    }
    private void Message(string text, bool error = false)
    { _status.Text = text; _status.ForeColor = error ? DarkTheme.Error : DarkTheme.Muted; }
    private bool Attempt(Action work)
    { try { work(); return true; } catch (Exception ex) { Message(ex.Message, true); return false; } }
    private void Changed() { if (_loading) return; _pending = true; _reloadArmed = false; _confirmSceneChange = null; _reload.Text = "Reload"; Message("Unsaved draft • Save applies ambient changes; running storyboards keep their current settings."); UpdateActions(); }
    private bool CommitCurrent() => Attempt(() =>
    {
        // Read the actual controls at save/navigation boundaries, rather than
        // relying on a change notification to decide whether their values exist.
        if (_index < 0) return;
        switch (_view)
        {
            case SceneEditorView scene: _session.SetScene(_index, scene.Read()); break;
            case PlaylistEditorView playlist: _session.SetPlaylist(_index, playlist.Read()); break;
            case TriggerEditorView trigger: _session.SetTrigger(_index, trigger.Read()); break;
        }
        _pending = false;
    });
    private int Count => _section == "Scenes" ? _session.Current.Scenes.Length : _section == "Playlists" ? _session.Current.Playlists.Length : _session.Current.Triggers.Length;
    internal void Navigate(string section, int index)
    {
        if (_saving || !CommitCurrent()) { RenderLibrary(); return; }
        _section = section; _index = Math.Min(Math.Max(0, index), Count - 1); _search.Text = "";
        RenderLibrary(); RenderTools(); RenderView(); UpdateActions();
    }
    private void OpenPlaylist(string name)
    {
        if (!CommitCurrent()) return;
        var index = Array.FindIndex(_session.Current.Playlists, p => ControllerOptions.Same(p.Name, name));
        if (index < 0) return; _history.Push((_section, _index)); Navigate("Playlists", index);
    }
    private void Back() { if (_history.Count > 0 && CommitCurrent()) { var previous = _history.Pop(); Navigate(previous.Section, previous.Index); } }
    private void RenderLibrary()
    {
        _loading = true; _library.BeginUpdate(); _library.Items.Clear();
        var show = _session.Current;
        var rows = _section == "Scenes" ? show.Scenes.Select((s, i) => (s.Name, Type: i == 0 ? "Startup" : "Scene", Index: i))
            : _section == "Playlists" ? show.Playlists.Select((p, i) => (p.Name, Type: p.Type == "ambient" ? "Ambient" : "Storyboard", Index: i))
            : show.Triggers.Select((t, i) => (t.Name, Type: t.IntervalMilliseconds is null ? "Event" : "Timer", Index: i));
        foreach (var row in rows.Where(r => r.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
        { var item = new ListViewItem([row.Name, row.Type]) { Tag = row.Index }; _library.Items.Add(item); if (row.Index == _index) item.Selected = true; }
        _library.EndUpdate(); foreach (var pair in _sections) { DarkTheme.Button(pair.Value, pair.Key == _section); pair.Value.Padding = new(5, 3, 5, 3); pair.Value.Font = new("Segoe UI", 9); pair.Value.MinimumSize = new(0, 34); } _loading = false;
    }
    private void RenderTools()
    {
        foreach (Control c in _tools.Controls.Cast<Control>().ToArray()) c.Dispose(); _tools.Controls.Clear();
        if (_section == "Playlists") { _tools.Controls.Add(Button("+ Storyboard", () => Add("storyboard"), true)); _tools.Controls.Add(Button("+ Ambient", () => Add("ambient"))); }
        else _tools.Controls.Add(Button("+ New", () => Add(), true));
        _tools.Controls.Add(Button("Copy", Duplicate)); _tools.Controls.Add(Button("Delete", Delete));
        if (_section == "Scenes") _tools.Controls.Add(Button("Startup", Startup));
        foreach (var button in _tools.Controls.OfType<Button>()) { button.Font = new("Segoe UI", 9); button.Padding = new(5); }
    }
    private string[] Media()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
        var local = Directory.Exists(root) ? PhotoBooth.Media.MediaLibrary.Read(root).Where(e => !e.IsFolder).Select(e => e.RelativePath) : [];
        return local.Concat(_host.Experiences.Snapshot().SelectMany(p => p.Inventory.VideoFiles.Concat(p.Inventory.AudioFiles))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void RenderView()
    {
        _view?.Dispose(); _workspace.Controls.Clear(); _view = null;
        if (_index < 0) { _workspace.Controls.Add(Note("Nothing here yet. Use + New to start.")); return; }
        var show = _session.Current;
        Attempt(() =>
        {
            _view = _section switch
            {
                "Scenes" => new SceneEditorView(show.Scenes[_index], show, _index == 0, Changed, OpenPlaylist, CreateScenePlaylist),
                "Playlists" => new PlaylistEditorView(show.Playlists[_index], show, _host.Options, _host.Experiences.Snapshot(), Media(), _session.PlaylistUses(show.Playlists[_index].Name), Changed,
                    _host.ArduinoStatus.Where(a => a.Connected).Select(a => a.Name).ToArray()),
                _ => new TriggerEditorView(show.Triggers[_index], show, _host.Options.Build.DefaultTriggerCooldownMilliseconds, Changed, OpenPlaylist)
            };
            _workspace.Controls.Add(_view);
        });
    }
    private void CreateScenePlaylist(string phase)
    {
        if (_saving || _section != "Scenes" || _index < 0 || !CommitCurrent()) return;
        var show = _session.Current;
        var scene = show.Scenes[_index];
        var name = ShowEditSession.Unique(scene.Name + " " + phase, show.Playlists.Select(p => p.Name));
        var playlist = new PlaylistOptions { Name = name, Type = phase == "ambient" ? "ambient" : "storyboard" };
        var linked = phase switch
        {
            "activation" => scene with { ActivatingPlaylist = name },
            "ambient" => scene with { AmbientPlaylist = name },
            "deactivation" => scene with { DeactivatingPlaylist = name },
            _ => throw new ArgumentException("Unknown scene phase.")
        };
        _session.Change(show with { Playlists = [.. show.Playlists, playlist], Scenes = show.Scenes.Select((s, i) => i == _index ? linked : s).ToArray() });
        // Dispose the old scene controls before navigation can read their old
        // selection back over the newly assigned reference.
        _view?.Dispose(); _workspace.Controls.Clear(); _view = null; _pending = false;
        _history.Push((_section, _index)); Navigate("Playlists", _session.Current.Playlists.Length - 1);
        Message($"Created and assigned {phase} playlist ‘{name}’. Save keeps it linked to ‘{scene.Name}’.");
    }
    private void Add(string type = "storyboard")
    {
        if (_saving || !CommitCurrent()) return;
        var show = _session.Current;
        if (_section == "Scenes")
        {
            var name = ShowEditSession.Unique("Scene", show.Scenes.Select(s => s.Name));
            var ambient = ShowEditSession.Unique(name + " ambient", show.Playlists.Select(p => p.Name));
            _session.Change(show with { Scenes = [.. show.Scenes, new() { Name = name, AmbientPlaylist = ambient }], Playlists = [.. show.Playlists, new() { Name = ambient, Type = "ambient" }] });
        }
        else if (_section == "Playlists") _session.Change(show with { Playlists = [.. show.Playlists, new() { Name = ShowEditSession.Unique(type == "ambient" ? "Ambient" : "Storyboard", show.Playlists.Select(p => p.Name)), Type = type }] });
        else
        {
            var playlist = show.Playlists.FirstOrDefault(p => p.Type == "storyboard");
            if (playlist is null) { playlist = new() { Name = ShowEditSession.Unique("Storyboard", show.Playlists.Select(p => p.Name)) }; show = show with { Playlists = [.. show.Playlists, playlist] }; }
            _session.Change(show with { Triggers = [.. show.Triggers, new() { Name = ShowEditSession.Unique("Trigger", show.Triggers.Select(t => t.Name)), SourceDevice = "sensor", Input = "motion", Event = "motion.detected", Playlist = playlist.Name }] });
        }
        _index = Count - 1; RenderLibrary(); RenderView(); Changed();
    }
    private void Duplicate()
    {
        if (_index < 0 || _saving || !CommitCurrent()) return;
        var show = _session.Current;
        if (_section == "Scenes") { var value = show.Scenes[_index]; _session.Change(show with { Scenes = [.. show.Scenes, value with { Name = ShowEditSession.Unique(value.Name + " copy", show.Scenes.Select(s => s.Name)) }] }); }
        else if (_section == "Playlists") { var value = show.Playlists[_index]; _session.Change(show with { Playlists = [.. show.Playlists, value with { Name = ShowEditSession.Unique(value.Name + " copy", show.Playlists.Select(s => s.Name)) }] }); }
        else { var value = show.Triggers[_index]; _session.Change(show with { Triggers = [.. show.Triggers, value with { Name = ShowEditSession.Unique(value.Name + " copy", show.Triggers.Select(s => s.Name)) }] }); }
        _index = Count - 1; RenderLibrary(); RenderView(); Changed();
    }
    private void Delete()
    {
        if (_index < 0 || _saving || !CommitCurrent()) return;
        if (!Attempt(() =>
        {
            if (_section == "Scenes") _session.DeleteScene(_index);
            else if (_section == "Playlists") _session.DeletePlaylist(_index);
            else _session.Change(_session.Current with { Triggers = _session.Current.Triggers.Where((_, i) => i != _index).ToArray() });
        })) return;
        _index = Math.Min(_index, Count - 1); RenderLibrary(); RenderView(); Changed();
    }
    private void Startup()
    { if (_index >= 0 && CommitCurrent()) { _session.MakeStartup(_index); _index = 0; RenderLibrary(); RenderView(); Changed(); } }
    private void Undo()
    { if (!CommitCurrent()) return; _session.Undo(); _pending = false; _index = Math.Min(_index, Count - 1); RenderLibrary(); RenderView(); Message(_session.Dirty ? "Draft edit undone." : "Draft matches saved settings."); UpdateActions(); }
    private void Reload()
    {
        if (_saving) return;
        if (HasUnsavedChanges && !_reloadArmed) { _reloadArmed = true; _reload.Text = "Discard draft"; Message("Click Discard draft to reload saved settings. Your unsaved edits will be lost."); return; }
        _session.Reset(_host.Options); _lastOptions = _host.Options; _pending = _reloadArmed = false; _awaiting = null; _reload.Text = "Reload"; _history.Clear();
        _index = Math.Min(_index, Count - 1); RenderLibrary(); RenderView(); Message("Loaded saved settings."); UpdateActions();
    }
    internal async Task SaveAsync()
    {
        if (_saving || !CommitCurrent() || !_host.Options.ConfigurationMode) return;
        var next = _session.Current;
        if (!Attempt(() => next.Validate(_host.Options))) return;
        var active = _host.Scenes.Snapshot().Active;
        if (active is not null && !next.Scenes.Any(s => ControllerOptions.Same(s.Name, active)) && _confirmSceneChange != next.Signature)
        { _confirmSceneChange = next.Signature; Message($"Active scene ‘{active}’ was renamed or removed. Save again to switch to startup scene ‘{next.Scenes[0].Name}’."); return; }
        _saving = true; UpdateActions(); _workspace.Enabled = _library.Enabled = _tools.Enabled = false;
        try
        {
            await _host.SaveShowAsync(_session.Baseline, next);
            if (IsDisposed) return;
            _session.Accept(next); _pending = false; _awaiting = next.Signature; _awaitUntil = Environment.TickCount64 + 4000; _reloadArmed = false; _reload.Text = "Reload";
            RenderLibrary(); Message("Saved • ambient scheduling updates live; running storyboards finish with their original settings.");
        }
        catch (Exception ex) { if (!IsDisposed) { Message(ex.Message, true); _host.Log.Error("ShowConfigurationSaveFailed", ex); } }
        finally { if (!IsDisposed) { _saving = false; _workspace.Enabled = _library.Enabled = _tools.Enabled = true; UpdateActions(); } }
    }
    private void Run()
    {
        if (HasUnsavedChanges || _index < 0) { Message("Save the draft before activating or running it."); return; }
        Attempt(() =>
        {
            string name, kind;
            if (_section == "Scenes")
            { kind = "Scene"; name = _session.Current.Scenes[_index].Name; _host.Scenes.Request(name); }
            else if (_section == "Playlists" && _session.Current.Playlists[_index].Type == "storyboard")
            { kind = "Playlist"; name = _session.Current.Playlists[_index].Name; _runId = _host.Engine.RunManually(name); }
            else return;
            _host.Log.Info("EditorPlaybackRequested", $"{kind} requested: {name}", new { Section = _section, Name = name });
        }); UpdateActions();
    }
    private void CancelRun() { if (_runId is not null) _host.Engine.Cancel(_runId); _runId = null; UpdateActions(); }
    internal void RefreshState()
    {
        if (_saving) return;
        var options = _host.Options;
        if (!ReferenceEquals(options, _lastOptions) || _awaiting is not null)
        {
            var remote = ShowConfiguration.From(options); var signature = remote.Signature;
            if (_awaiting is not null && signature != _awaiting)
            { if (Environment.TickCount64 >= _awaitUntil) Message("Saved file has not been applied yet. Check Controller's activity log or Reload.", true); UpdateActions(); return; }
            _awaiting = null; _lastOptions = options;
            if (signature != _session.Baseline.Signature)
            {
                if (HasUnsavedChanges) Message("Settings changed elsewhere. Draft retained; Reload before saving.", true);
                else { _session.Reset(options); _index = Math.Min(_index, Count - 1); RenderLibrary(); RenderView(); Message("Settings updated from configuration."); }
            }
        }
        var scene = _host.Scenes.Snapshot(); var runs = _host.Engine.Snapshot();
        _playback.Text = $"LIVE  /  {scene.Active ?? "Starting"}  ·  {scene.Phase}  ·  {runs.Length} runs";
        if (_runId is not null && !runs.Any(r => r.Id == _runId)) _runId = null;
        UpdateActions();
    }
    private void UpdateActions()
    {
        var edit = !_saving && _host.Options.ConfigurationMode;
        _save.Enabled = edit && HasUnsavedChanges; _undo.Enabled = edit && (_session.CanUndo || _pending); _reload.Enabled = !_saving;
        _back.Enabled = !_saving && _history.Count > 0; _tools.Enabled = _workspace.Enabled = _library.Enabled = edit;
        _run.Text = _section == "Scenes" ? "Activate scene" : "Run playlist";
        _run.Enabled = edit && !HasUnsavedChanges && _index >= 0 && (_section == "Scenes" || _section == "Playlists" && _session.Current.Playlists[_index].Type == "storyboard");
        _cancel.Enabled = !_saving && _runId is not null;
    }
    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == (Keys.Control | Keys.S)) { _ = SaveAsync(); return true; }
        if (key == (Keys.Control | Keys.Z) && !ControlWindow.TextInputFocused(this)) { Undo(); return true; }
        return base.ProcessCmdKey(ref message, key);
    }
    protected override void Dispose(bool disposing) { if (disposing) { _refresh.Stop(); _refresh.Dispose(); } base.Dispose(disposing); }
}
