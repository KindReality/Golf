using Controller.Application;
using Controller.Configuration;
using Controller.Networking;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class PlaylistEditorView : UserControl
{
    private PlaylistOptions _value;
    private readonly ShowConfiguration _show;
    private readonly ControllerOptions _options;
    private readonly ExperiencePeer[] _peers;
    private readonly string[] _media;
    private readonly string[] _serialDevices;
    private readonly Action _changed;
    private readonly TextBox _name;
    private readonly TextBox _duration;
    private readonly ComboBox _audioOutputMode;
    private readonly Label _error = new() { AutoSize = true, ForeColor = DarkTheme.Error, Dock = DockStyle.Top };
    private readonly ListView _items = List("Item", "Timing (ms)", "Commands");
    private readonly PlaylistTimingView _timing = new();
    private readonly Panel _inspector = new() { Dock = DockStyle.Fill, Padding = new(10), BackColor = DarkTheme.Surface };
    private TextBox? _itemName;
    private ComboBox? _roles, _mode;
    private CheckBox? _enabled, _subwoofer;
    private NumericUpDown? _delay, _minimum, _maximum, _cooldown;
    private ListView? _commands;
    private Panel? _commandPanel;
    private PlaylistCommandEditor? _commandEditor;
    private List<PlaylistCommand> _commandValues = [];
    private int _item = -1, _command = -1, _dragItem = -1;
    private bool _loading;
    internal PlaylistEditorView(PlaylistOptions playlist, ShowConfiguration show, ControllerOptions options, ExperiencePeer[] peers, string[] media, string[] uses, Action changed, string[]? serialDevices = null)
    {
        _value = playlist; _show = show; _options = options; _peers = peers; _media = media; _changed = changed;
        _serialDevices = serialDevices ?? [];
        Dock = DockStyle.Fill; BackColor = DarkTheme.Background;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 168)); layout.RowStyles.Add(new(SizeType.Absolute, 108));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 24));
        var heading = new ShowEditorFields(); _name = TextField(playlist.Name); heading.Field(playlist.Type == "ambient" ? "Ambient playlist" : "Storyboard playlist", _name);
        _duration = TextField(playlist.DurationMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        _duration.PlaceholderText = playlist.Type == "ambient" ? "Empty = indefinite" : "Empty = automatic";
        heading.Field("Duration (ms)", _duration); Watch(_duration, changed);
        _duration.AccessibleName = "Playlist duration milliseconds";
        _audioOutputMode = Choice(["Configured", "Random"], playlist.AudioOutputMode);
        heading.Field("Audio outputs", _audioOutputMode); Watch(_audioOutputMode, changed);
        var usage = Note(uses.Length == 0 ? "Standalone playlist • available to manual and network commands" : "Used by " + string.Join(" · ", uses)); usage.Height = 30; heading.Wide(usage);
        Watch(_name, changed); layout.Controls.Add(heading, 0, 0); layout.Controls.Add(_timing, 0, 1);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 43)); columns.ColumnStyles.Add(new(SizeType.Percent, 57));
        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        listPanel.RowStyles.Add(new(SizeType.Absolute, 94)); listPanel.RowStyles.Add(new(SizeType.Percent, 100));
        listPanel.Controls.Add(Toolbar(Button("+ Item", () => EditItems("add"), true), Button("Copy", () => EditItems("copy")), Button("↑", () => EditItems("up")), Button("↓", () => EditItems("down")), Button("Delete", () => EditItems("delete"))), 0, 0);
        listPanel.Controls.Add(_items, 0, 1); columns.Controls.Add(listPanel, 0, 0); columns.Controls.Add(_inspector, 1, 0); layout.Controls.Add(columns, 0, 2); layout.Controls.Add(_error, 0, 3); Controls.Add(layout);
        _items.SelectedIndexChanged += (_, _) => { if (!_loading && _items.SelectedIndices.Count > 0) SelectItem(_items.SelectedIndices[0]); };
        _timing.Selected = SelectItem;
        _items.AllowDrop = true; _items.ItemDrag += (_, e) => { _dragItem = ((ListViewItem)e.Item!).Index; _items.DoDragDrop("show-item", DragDropEffects.Move); };
        _items.DragEnter += (_, e) => { if (_dragItem >= 0) e.Effect = DragDropEffects.Move; };
        _items.DragDrop += (_, e) =>
        {
            var hit = _items.GetItemAt(_items.PointToClient(new(e.X, e.Y)).X, _items.PointToClient(new(e.X, e.Y)).Y);
            if (hit is not null && _dragItem >= 0) MoveItem(_dragItem, hit.Index); _dragItem = -1;
        };
        RenderItems(); if (_value.Items.Length > 0) SelectItem(0);
    }
    private bool Attempt(Action action)
    { try { action(); _error.Text = ""; return true; } catch (Exception ex) { _error.Text = ex.Message; return false; } }
    private void Changed() { if (!_loading) _changed(); }
    internal PlaylistOptions Read()
    {
        CommitItem(); int? duration = null;
        if (!string.IsNullOrWhiteSpace(_duration.Text))
        {
            if (!int.TryParse(_duration.Text, out var parsed) || parsed is < 0 or > 86400000)
                throw new ArgumentException("Playlist duration must be 0–86400000 milliseconds, or empty for automatic/indefinite.");
            duration = parsed;
        }
        return _value with { Name = _name.Text.Trim(), DurationMilliseconds = duration, AudioOutputMode = _audioOutputMode.Text };
    }
    private void CommitCommand()
    { if (_commandEditor is not null && _command >= 0) _commandValues[_command] = _commandEditor.Read(); }
    private void CommitItem()
    {
        if (_item < 0 || _itemName is null) return;
        CommitCommand();
        var next = _value.Items.ToArray(); next[_item] = next[_item] with { Name = _itemName.Text.Trim(), Enabled = _enabled!.Checked,
            DelayMilliseconds = _delay is null ? 0 : (int)_delay.Value, MinIntervalMilliseconds = _minimum is null ? null : (int)_minimum.Value,
            MaxIntervalMilliseconds = _maximum is null ? null : (int)_maximum.Value, CooldownMilliseconds = (int)_cooldown!.Value,
            Roles = _roles!.Text.Trim(), BroadcastMode = _mode!.Text, IncludeSubwoofer = _subwoofer!.Checked, Commands = _commandValues.ToArray() };
        _value = _value with { Items = next };
    }
    private void RenderItems()
    {
        _loading = true; _items.BeginUpdate(); _items.Items.Clear();
        foreach (var i in _value.Items) _items.Items.Add(new ListViewItem([(i.Enabled ? "" : "OFF · ") + i.Name,
            _value.Type == "ambient" ? $"{i.MinIntervalMilliseconds:N0}–{i.MaxIntervalMilliseconds:N0}" : $"+{i.DelayMilliseconds:N0}", i.Commands.Length.ToString()]));
        if (_item >= 0 && _item < _items.Items.Count) { _items.Items[_item].Selected = true; _items.EnsureVisible(_item); }
        _items.EndUpdate(); _timing.Set(_value, _item); _loading = false;
    }
    private void SelectItem(int index)
    {
        if (index < 0 || index >= _value.Items.Length || index == _item) return;
        if (!Attempt(CommitItem)) { RenderItems(); return; }
        _item = index; RenderItems(); RenderInspector();
    }
    private void RenderInspector()
    {
        _loading = true;
        foreach (Control c in _inspector.Controls.Cast<Control>().ToArray()) c.Dispose(); _inspector.Controls.Clear(); _commandEditor = null; _command = -1; _itemName = null;
        if (_item < 0 || _item >= _value.Items.Length) { _loading = false; return; }
        var item = _value.Items[_item]; var fields = new ShowEditorFields();
        _itemName = TextField(item.Name); fields.Field("Item name", _itemName);
        _enabled = Check("Enabled", item.Enabled); fields.Wide(_enabled);
        _delay = _minimum = _maximum = null;
        if (_value.Type == "ambient")
        { _minimum = Number(item.MinIntervalMilliseconds ?? 10000); _minimum.Minimum = 1; _maximum = Number(item.MaxIntervalMilliseconds ?? 30000); _maximum.Minimum = 1; fields.Field("Minimum interval (ms)", _minimum); fields.Field("Maximum interval (ms)", _maximum); }
        else { _delay = Number(item.DelayMilliseconds); fields.Field("Wait before item (ms)", _delay); }
        _cooldown = Number(item.CooldownMilliseconds); fields.Field("Cooldown (ms)", _cooldown);
        _roles = Choice(LivePlayback.Roles(_peers).Select(r => r.Name), item.Roles, true); fields.Field("Roles (comma-separated)", _roles);
        _mode = Choice(["Broadcast", "Random"], item.BroadcastMode); fields.Field("Role selection", _mode);
        _subwoofer = Check("Include subwoofer", item.IncludeSubwoofer); fields.Wide(_subwoofer);
        fields.Wide(Note(_value.Type == "ambient" ? "Each enabled item runs independently at a random interval. Cooldown must fit within its maximum interval." : "Items run in order. The delay is measured after the previous item's commands are dispatched; media can continue playing."));
        _commandValues = item.Commands.ToList();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.RowStyles.Add(new(SizeType.Absolute, 48)); root.RowStyles.Add(new(SizeType.Percent, 100));
        var body = new Panel { Dock = DockStyle.Fill };
        var itemPage = ScrollContainer(fields);
        var commandsPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        commandsPage.RowStyles.Add(new(SizeType.Absolute, 96)); commandsPage.RowStyles.Add(new(SizeType.Absolute, 100)); commandsPage.RowStyles.Add(new(SizeType.Percent, 100));
        commandsPage.Controls.Add(Toolbar(Button("+ Command", () => EditCommands("add"), true), Button("Copy", () => EditCommands("copy")), Button("↑", () => EditCommands("up")), Button("↓", () => EditCommands("down")), Button("Delete", () => EditCommands("delete"))), 0, 0);
        _commands = List("Command", "Destination"); commandsPage.Controls.Add(_commands, 0, 1);
        _commandPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; commandsPage.Controls.Add(_commandPanel, 0, 2);
        _commands.SelectedIndexChanged += (_, _) => { if (!_loading && _commands.SelectedIndices.Count > 0) SelectCommand(_commands.SelectedIndices[0]); };
        Button? itemTab = null, commandTab = null;
        void Tab(bool commands)
        { itemPage.Visible = !commands; commandsPage.Visible = commands; DarkTheme.Button(itemTab!, !commands); DarkTheme.Button(commandTab!, commands); }
        itemTab = Button("Item", () => Tab(false)); commandTab = Button("Commands", () => Tab(true));
        root.Controls.Add(Toolbar(itemTab, commandTab), 0, 0); body.Controls.Add(itemPage); body.Controls.Add(commandsPage); root.Controls.Add(body, 0, 1);
        _inspector.Controls.Add(root); Tab(false);
        foreach (var c in new Control?[] { _itemName, _enabled, _delay, _minimum, _maximum, _cooldown, _roles, _mode, _subwoofer }.OfType<Control>()) Watch(c, Changed);
        foreach (var number in new[] { _delay, _minimum, _maximum }.OfType<NumericUpDown>()) number.ValueChanged += (_, _) =>
        { if (!_loading && Attempt(CommitItem)) RenderItems(); };
        RenderCommands(); _loading = false; if (_commandValues.Count > 0) SelectCommand(0);
    }
    private void RenderCommands()
    {
        if (_commands is null) return;
        var loading = _loading; _loading = true; _commands.Items.Clear();
        foreach (var c in _commandValues) _commands.Items.Add(new ListViewItem([ShowEditSession.CommandSummary(c), c.Playlist is not null ? "Local" : c.Device ?? "*"]));
        if (_command >= 0 && _command < _commands.Items.Count) _commands.Items[_command].Selected = true;
        _loading = loading;
    }
    private void SelectCommand(int index)
    {
        if (index == _command || index < 0 || index >= _commandValues.Count) return;
        if (!Attempt(CommitCommand)) { RenderCommands(); return; }
        _commandEditor?.Dispose(); _command = index;
        _commandEditor = new(_commandValues[index], _show, _options, _peers, _media, Changed, _serialDevices);
        _commandPanel!.Controls.Clear(); _commandPanel.Controls.Add(_commandEditor); RenderCommands();
    }
    private void EditCommands(string action) => Attempt(() =>
    {
        CommitCommand(); var index = _command;
        if (action == "add") { _commandValues.Add(ShowEditSession.DefaultCommand()); index = _commandValues.Count - 1; }
        else if (index < 0) return;
        else if (action == "copy") { _commandValues.Insert(index + 1, _commandValues[index]); index++; }
        else if (action == "delete") { _commandValues.RemoveAt(index); index = Math.Min(index, _commandValues.Count - 1); }
        else { var to = index + (action == "up" ? -1 : 1); if (to < 0 || to >= _commandValues.Count) return; (_commandValues[index], _commandValues[to]) = (_commandValues[to], _commandValues[index]); index = to; }
        _commandEditor?.Dispose(); _commandEditor = null; _commandPanel!.Controls.Clear(); _command = -1; RenderCommands();
        if (index >= 0) SelectCommand(index); Changed();
    });
    private void MoveItem(int from, int to) => Attempt(() =>
    {
        CommitItem(); var list = _value.Items.ToList(); var item = list[from]; list.RemoveAt(from); list.Insert(to, item); _value = _value with { Items = list.ToArray() };
        _item = to; RenderItems(); RenderInspector(); Changed();
    });
    private void EditItems(string action) => Attempt(() =>
    {
        CommitItem(); var list = _value.Items.ToList(); var index = _item;
        if (action == "add") { list.Add(ShowEditSession.NewItem(ShowEditSession.Unique("Item", list.Select(i => i.Name)), _value.Type == "ambient")); index = list.Count - 1; }
        else if (index < 0) return;
        else if (action == "copy") { list.Insert(index + 1, list[index] with { Name = ShowEditSession.Unique(list[index].Name + " copy", list.Select(i => i.Name)) }); index++; }
        else if (action == "delete") { list.RemoveAt(index); index = Math.Min(index, list.Count - 1); }
        else { var to = index + (action == "up" ? -1 : 1); if (to < 0 || to >= list.Count) return; (list[index], list[to]) = (list[to], list[index]); index = to; }
        _value = _value with { Items = list.ToArray() }; _item = index; RenderItems(); RenderInspector(); Changed();
    });
}
