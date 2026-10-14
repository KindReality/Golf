using Controller.Application;
using PhotoBooth.Networking;
using System.Windows.Forms.Integration;

namespace Controller.Windows;

internal sealed class PerspectiveEditor : Form
{
    private readonly ControllerHost _host;
    private readonly Action? _switchExperience;
    private readonly Action<PerspectiveDefinition?, Action<PerspectiveDefinition>>? _openLed;
    private readonly PerspectivePreview _preview = new();
    private readonly ElementHost _canvas = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _media = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly ComboBox _perspectives = new DarkComboBox { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _name = new() { Width = 180 };
    private readonly NumericUpDown[] _coordinates = Enumerable.Range(0, 4).Select(_ => new NumericUpDown { Width = 78, DecimalPlaces = 2, Maximum = 100, Increment = .1m }).ToArray();
    private readonly TrackBar _seek = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button _play = new() { Text = "Pause", AutoSize = true };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 150 };
    private PerspectiveDefinition[] _values;
    private PerspectiveDefinition[]? _undo;
    private sealed record MovementUndo(string Id, PerspectiveRectangle Before, PerspectiveRectangle After);
    private MovementUndo? _movementUndo;
    private Task _saveTask = Task.CompletedTask;
    internal PerspectivePreview Preview => _preview;
    private PerspectiveDefinition[]? _awaiting;
    private DateTime _awaitUntil;
    private string? _selected;
    private bool _updating, _saving, _add, _seeking;
    private bool Typing => _name.ContainsFocus || _coordinates.Any(c => c.ContainsFocus);
    private sealed record Choice(PerspectiveDefinition Value) { public override string ToString() => Value.Name; }
    public PerspectiveEditor(ControllerHost host, Action<PerspectiveDefinition?, Action<PerspectiveDefinition>>? openLed = null, Action? switchExperience = null)
    {
        _switchExperience = switchExperience; _openLed = openLed; _host = host; _values = host.Options.Perspectives;
        Text = "Controller • Perspectives"; Font = new("Segoe UI", 10); BackColor = DarkTheme.Background; ForeColor = DarkTheme.Text;
        ClientSize = new(1120, 780); MinimumSize = new(980, 660); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5, BackColor = BackColor };
        layout.RowStyles.Add(new(SizeType.Absolute, 85)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 45)); layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.Controls.Add(_media, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        toolbar.Controls.Add(_perspectives);
        Button Button(string text, Action clicked) { var b = new Button { Text = text, AutoSize = true }; DarkTheme.Button(b); b.Click += (_, _) => clicked(); toolbar.Controls.Add(b); return b; }
        Button("Add perspective", Add); Button("LED settings", EditLed); Button("Rename", () => _name.Focus()); Button("Delete", Delete); Button("Reset", Reset); Button("Undo", Undo);
         layout.Controls.Add(toolbar, 0, 1);
        _preview.Shortcut = HandleShortcut;
        _canvas.Child = _preview; layout.Controls.Add(_canvas, 0, 2);
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        fields.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new(0, 8, 0, 0) }); fields.Controls.Add(_name);
        for (int i = 0; i < 4; i++) { fields.Controls.Add(new Label { Text = new[] { "X %", "Y %", "Width %", "Height %" }[i], AutoSize = true, Padding = new(4, 8, 0, 0) }); fields.Controls.Add(_coordinates[i]); }
        var apply = new Button { Text = "Apply", AutoSize = true }; DarkTheme.Button(apply); apply.Click += (_, _) => CommitFields(); fields.Controls.Add(apply); layout.Controls.Add(fields, 0, 3);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; bottom.ColumnStyles.Add(new(SizeType.Absolute, 70)); bottom.ColumnStyles.Add(new(SizeType.Percent, 50)); bottom.ColumnStyles.Add(new(SizeType.Percent, 50));
        DarkTheme.Button(_play); _play.Click += (_, _) => { _preview.TogglePlay(); _play.Text = _preview.Playing ? "Pause" : "Play"; }; bottom.Controls.Add(_play, 0, 0); bottom.Controls.Add(_seek, 1, 0); bottom.Controls.Add(_status, 2, 0); layout.Controls.Add(bottom, 0, 4); Controls.Add(layout);
        _seek.MouseDown += (_, _) => _seeking = true; _seek.MouseUp += (_, _) => { _preview.Seek(_seek.Value / 1000.0 * _preview.Duration); _seeking = false; };
        _seek.KeyUp += (_, _) => _preview.Seek(_seek.Value / 1000.0 * _preview.Duration);
        _name.BackColor = DarkTheme.Background; _name.ForeColor = DarkTheme.Text;
        foreach (var c in _coordinates) { c.BackColor = BackColor; c.ForeColor = ForeColor; c.KeyDown += FieldKey; }
        _name.KeyDown += FieldKey;
        _perspectives.SelectedIndexChanged += (_, _) => { if (!_updating && _perspectives.SelectedItem is Choice c) { _selected = c.Value.Id; Render(); _canvas.Focus(); } };
        _preview.Selected += id => { _selected = id; UpdateFields(); };
        _preview.RenameRequested += () => { _name.Focus(); _name.SelectAll(); };
        _preview.Completed += (_, next) => Save(_values.Select(p => p.Id == next.Id ? next : p).ToArray());
        _preview.Failed += reason => _status.Text = "Preview: " + reason;
        _refresh.Tick += (_, _) => RefreshState();
        Load += (_, _) => { RefreshMedia(); Render(); _refresh.Start(); };
        FormClosed += (_, _) => { _refresh.Stop(); _refresh.Dispose(); _preview.Stop(); _canvas.Dispose(); };
    }
    private void RefreshMedia()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
        Directory.CreateDirectory(root);
        var extensions = new HashSet<string>([".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".mkv", ".wmv", ".png", ".jpg", ".jpeg", ".bmp", ".gif"], StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root).Where(p => extensions.Contains(Path.GetExtension(p))).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var button = new Button { Text = Path.GetFileName(path), Width = 150, Height = 62, Margin = new Padding(3), Tag = path };
            DarkTheme.Button(button);
            button.Click += (_, _) => { try { _preview.Open(path); _play.Text = _preview.Playing ? "Pause" : "Play"; _status.Text = Path.GetFileName(path) + " • preview only"; } catch (Exception ex) { _status.Text = ex.Message; } };
            _media.Controls.Add(button);
        }
        if (_media.Controls.Count > 0) ((Button)_media.Controls[0]).PerformClick();
        else _status.Text = "No local preview media";
    }
    private void RefreshState()
    {
        _preview.HitDiameterPercent = _host.Options.PointHitDiameterPercent;
        if (!_host.Options.ConfigurationMode) { Close(); return; }
        if (_awaiting is not null)
        {
            if (_host.Options.Perspectives.SequenceEqual(_awaiting)) _awaiting = null;
            else if (DateTime.UtcNow < _awaitUntil) return;
            else _awaiting = null;
        }
        if (!_saving && !_host.Options.Perspectives.SequenceEqual(_values))
        { _values = _host.Options.Perspectives; _undo = null; _movementUndo = null; Render(); _preview.Flash(true); _status.Text = "Perspectives updated"; }
        if (!_seeking && _preview.Duration > 0) _seek.Value = Math.Clamp((int)(_preview.Position / _preview.Duration * 1000), 0, 1000);
    }
    private void Render()
    {
        if (!_values.Any(p => p.Id == _selected)) _selected = _values.FirstOrDefault()?.Id;
        _preview.SetPerspectives(_values.Any(p=>p.Id==_selected && p.IsLed) ? [] : _values.Where(p => !p.IsLed).ToArray(), _selected); UpdateFields();
    }
    private void UpdateFields()
    {
        _updating = true;
        var value = _values.FirstOrDefault(p => p.Id == _selected);
        _perspectives.Items.Clear(); foreach (var p in _values) _perspectives.Items.Add(new Choice(p));
        _perspectives.SelectedIndex = Array.FindIndex(_values, p => p.Id == _selected);
        _name.Enabled = value is not null; _name.Text = value?.Name ?? "";
        var r = value?.Rectangle ?? new(); var coords = new[] { r.X, r.Y, r.Width, r.Height };
        for (int i = 0; i < 4; i++) { _coordinates[i].Enabled = value is not null && !value.IsLed; _coordinates[i].Value = (decimal)Math.Clamp(coords[i], 0, 100); }
        _updating = false;
    }
    private void FieldKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Tab) { CommitFields(); _canvas.Focus(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape) { UpdateFields(); _canvas.Focus(); e.SuppressKeyPress = true; }
    }
    private void CommitFields()
    {
        var p = _values.FirstOrDefault(p => p.Id == _selected); if (p is null) return;
        Save(_values.Select(v => v.Id != p.Id ? v : v with { Name = _name.Text.Trim(), Rectangle = new() { X = (double)_coordinates[0].Value, Y = (double)_coordinates[1].Value, Width = (double)_coordinates[2].Value, Height = (double)_coordinates[3].Value } }).ToArray());
    }
    private void Save(PerspectiveDefinition[] next, bool undo = false)
    {
        if (!_saving) _saveTask = SaveAsync(next, undo);
    }
    private async Task SaveAsync(PerspectiveDefinition[] next, bool undo)
    {
        if (_saving) return; _saving = true; _preview.IsEnabled = false;
        try
        {
            foreach (var p in next) p.Validate();
            var previous = _values; await _host.SavePerspectivesAsync(previous, next);
            if (IsDisposed) return;
            if (!undo) _undo = previous;
            if (undo) { _undo = null; _movementUndo = null; }
            else
            {
                var movements = previous.Join(next, before => before.Id, after => after.Id, (before, after) => new MovementUndo(before.Id, before.Rectangle, after.Rectangle))
                    .Where(change => change.Before != change.After).ToArray();
                if (movements.Length == 1) _movementUndo = movements[0];
                if (_movementUndo is { } movement && !next.Any(value => value.Id == movement.Id)) _movementUndo = null;
            }
            _values = next; _awaiting = next; _awaitUntil = DateTime.UtcNow.AddSeconds(3); Render(); _preview.Flash(true); _status.Text = "Saved • announced immediately";
        }
        catch (Exception ex) { if (!IsDisposed) { _values = _host.Options.Perspectives; _undo = null; _movementUndo = null; Render(); _preview.Flash(false); _status.Text = ex.Message; } _host.Log.Error("PerspectiveEditFailed", ex); }
        finally { _saving = false; _preview.IsEnabled = true; }
    }
    private void Add()
    {
        var n = 1; while (_values.Any(p => p.Name.Equals("perspective" + n, StringComparison.OrdinalIgnoreCase))) n++;
        var p = new PerspectiveDefinition { Name = "perspective" + n, Rectangle = _values.Length == 0 ? new() : new() { X = 25, Y = 25, Width = 50, Height = 50 } };
        _selected = p.Id; Save([.. _values, p]); _canvas.Focus();
    }
    private void EditLed() => EditLed(false);
    private void EditLed(bool create)
    {
        var original = create ? null : _values.FirstOrDefault(p => p.Id == _selected && p.IsLed);
        if (_openLed is not null) { _openLed(original, result => ApplyLed(original, result)); return; }
        using var editor = new LedPerspectiveEditor(original);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is not { } result) return;
        ApplyLed(original, result);
    }
    private void ApplyLed(PerspectiveDefinition? original, PerspectiveDefinition result)
    {
        _selected = result.Id;
        Save(original is null ? [.. _values, result] : _values.Select(p => p.Id == result.Id ? result : p).ToArray());
    }
    private void Delete() { if (_selected is not null) Save(_values.Where(p => p.Id != _selected).ToArray()); }
    private void Reset() { if (_selected is not null) Save(_values.Select(p => p.Id == _selected ? p with { Rectangle = new() } : p).ToArray()); }
    private void Undo() { if (_saving) return; if (_preview.Editing) { _preview.CancelEdit(); return; } if (_undo is { } undo) { _undo = null; Save(undo, true); } }
    private void UndoMovement()
    {
        if (_saving) return;
        if (_preview.Editing) { _preview.CancelEdit(); return; }
        if (_movementUndo is not { } movement || !_values.Any(value => value.Id == movement.Id && value.Rectangle == movement.After)) return;
        Save(_values.Select(value => value.Id == movement.Id ? value with { Rectangle = movement.Before } : value).ToArray(), true);
    }
    private async void MoveLayer(bool front)
    {
        if (_saving || _selected is not { } selected) return;
        _preview.FinishKeyboardEdit();
        await _saveTask;
        if (IsDisposed || !_host.Options.ConfigurationMode || _selected != selected) return;
        var index = Array.FindIndex(_values, value => value.Id == selected);
        if (index < 0 || index == (front ? _values.Length - 1 : 0)) return;
        var next = _values.ToList(); var value = next[index]; next.RemoveAt(index);
        next.Insert(front ? next.Count : 0, value);
        Save(next.ToArray());
    }
    protected override bool ProcessCmdKey(ref Message m, Keys key) => HandleShortcut(key) || base.ProcessCmdKey(ref m, key);
    private bool HandleShortcut(Keys key)
    {
        if (!_host.Options.ConfigurationMode) return false;
        if (key == (Keys.Control | Keys.Z) && !Typing) { UndoMovement(); return true; }
        if (!Typing && key is Keys.OemOpenBrackets or Keys.OemCloseBrackets) { MoveLayer(key == Keys.OemCloseBrackets); return true; }
        if (_perspectives.ContainsFocus) return false;
        if (Typing)
        {
            if (key is Keys.Enter or Keys.Tab) { CommitFields(); _canvas.Focus(); return true; }
            if (key == Keys.Escape) { UpdateFields(); _canvas.Focus(); return true; }
            return false;
        }
        if (key == Keys.E && _switchExperience is not null) { _switchExperience(); return true; }
        if (_canvas.ContainsFocus || _preview.IsKeyboardFocusWithin)
        {
            if (key is Keys.Tab or (Keys.Shift | Keys.Tab)) { _preview.NextPoint((key & Keys.Shift) != 0); return true; }
            var code = key & Keys.KeyCode;
            if (code is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            { _preview.Nudge(code == Keys.Left ? -1 : code == Keys.Right ? 1 : 0, code == Keys.Up ? -1 : code == Keys.Down ? 1 : 0, (key & Keys.Shift) != 0 ? 10 : 1); return true; }
            if (key == Keys.Enter) { _preview.FinishKeyboardEdit(); return true; }
        }

        if (key == Keys.A) { _add = true; _status.Text = "Add • P: media perspective • L: LED perspective"; return true; }
        if (key == Keys.P && _add) { _add = false; Add(); return true; }
        if (key == Keys.L && _add) { _add = false; EditLed(true); return true; }
        _add = false;
        if (key == Keys.N) { _name.Focus(); _name.SelectAll(); return true; }
        if (key == Keys.Delete) { Delete(); return true; }
        if (key == Keys.R) { Reset(); return true; }
        if (key == Keys.Z) { Undo(); return true; }
        if (key == Keys.Escape) { _preview.CancelEdit(); return true; }
        return false;
    }
}
