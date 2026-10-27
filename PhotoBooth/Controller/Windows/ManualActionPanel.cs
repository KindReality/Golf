using Controller.Application;
using Controller.Configuration;
using System.Text.Json;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class ManualActionPanel : UserControl
{
    private readonly ControllerHost _host;
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
    private readonly TextBox _name = TextField(), _source = TextField();
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = DarkTheme.Muted, AccessibleName = "Manual action status" };
    private readonly Button _commandMode, _eventMode, _send, _pin, _new, _remove;
    private readonly TableLayoutPanel _sourceRow;
    private PlaylistCommandEditor? _command, _event;
    private PinnedActionOptions? _editing;
    private bool _eventSelected, _busy;
    internal PinnedActionOptions? Editing => _editing;
    internal int ContentHeight => 114 + (_eventSelected ? 28 : 0) + ((_eventSelected ? _event : _command)?.ContentHeight ?? 0);

    internal ManualActionPanel(ControllerHost host)
    {
        _host = host; Dock = DockStyle.Fill; BackColor = DarkTheme.Surface; Font = new("Segoe UI", 9);
        foreach (var height in new[] { 30, 0, -1, 28, 32, 24 }) _layout.RowStyles.Add(height < 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, height));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = "SEND ACTION", AutoSize = true, ForeColor = DarkTheme.Accent, Padding = new(2, 7, 10, 0) });
        _commandMode = Button("Command", () => SelectMode(false), true); _eventMode = Button("Event", () => SelectMode(true));
        _commandMode.AccessibleName = "Compose command"; _eventMode.AccessibleName = "Compose event";
        header.Controls.AddRange([_commandMode, _eventMode]);
        _layout.Controls.Add(header, 0, 0);
        _source.PlaceholderText = host.Options.EffectiveDeviceName; _source.AccessibleName = "Event source device";
        _sourceRow = InputRow("Source device", _source); _layout.Controls.Add(_sourceRow, 0, 1);
        _layout.Controls.Add(_editorHost, 0, 2);
        _name.PlaceholderText = "Name this action to pin it"; _name.AccessibleName = "Pin name";
        _layout.Controls.Add(InputRow("Pin name", _name), 0, 3);
        _send = Button("▶  Send now", () => _ = SendAsync(), true); _send.AccessibleName = "Send manual action";
        _pin = Button("⚑  Pin action", () => _ = SavePinAsync()); _pin.AccessibleName = "Save pinned action";
        _new = Button("New pin", () => { _editing = null; _name.Clear(); State(); Notice("Current action is ready to pin under a new name."); });
        _remove = Button("Remove pin", () => _ = RemovePinAsync());
        var actions = Toolbar(_send, _pin, _new, _remove); actions.WrapContents = false;
        foreach (var button in new[] { _commandMode, _eventMode, _send, _pin, _new, _remove }) LiveControls.StyleButton(button, button == _send);
        _status.AutoEllipsis = true;
        _layout.Controls.Add(actions, 0, 4); _layout.Controls.Add(_status, 0, 5); Controls.Add(_layout);
        SelectMode(false); Notice("Send an action now, or name and pin it for one-click use.");
    }
    private static TableLayoutPanel InputRow(string caption, TextBox field)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Absolute, 76)); row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = DarkTheme.Muted }, 0, 0);
        field.Dock = DockStyle.Fill; row.Controls.Add(field, 1, 0); return row;
    }
    private string[] Media()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
        try { return PhotoBooth.Media.MediaLibrary.Read(root).Where(e => !e.IsFolder).Select(e => e.RelativePath)
            .Concat(_host.Experiences.Snapshot().SelectMany(p => p.Inventory.VideoFiles.Concat(p.Inventory.AudioFiles))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    private PlaylistCommandEditor CreateEditor(PlaylistCommand command)
        => new(command, ShowConfiguration.From(_host.Options), _host.Options, _host.Experiences.Snapshot(), Media(), () => { },
            _host.ArduinoStatus.Where(a => a.Connected).Select(a => a.Name).ToArray(), manual: true);
    internal void SelectMode(bool events)
    {
        _eventSelected = events;
        if (events) _event ??= CreateEditor(new() { Type = "event", Device = "*", Payload = JsonSerializer.SerializeToElement(new { input = "motion", @event = "motion.detected" }) });
        else _command ??= CreateEditor(ShowEditSession.DefaultCommand() with { Payload = JsonSerializer.SerializeToElement(new
            { experience = "video", value = Media().FirstOrDefault(f => LivePlayback.Videos.Contains(Path.GetExtension(f))) ?? "", screen = "*", mute = true, loop = false }) });
        var editor = events ? _event! : _command!;
        if (editor.Parent is null) _editorHost.Controls.Add(editor);
        if (_command is not null) _command.Visible = !events;
        if (_event is not null) _event.Visible = events;
        editor.BringToFront(); _sourceRow.Visible = events; _layout.RowStyles[1].Height = events ? 28 : 0;
        LiveControls.StyleButton(_commandMode, !events); LiveControls.StyleButton(_eventMode, events); State();
    }
    internal void LoadPin(PinnedActionOptions pin)
    {
        _editing = pin; _name.Text = pin.Name; _source.Text = pin.EventSource ?? "";
        var events = pin.Command.Type == "event";
        if (events) { _event?.Dispose(); _event = CreateEditor(pin.Command); }
        else { _command?.Dispose(); _command = CreateEditor(pin.Command); }
        SelectMode(events); Notice($"Editing ‘{pin.Name}’. Send now runs it; Save pin updates the shortcut.");
    }
    private (PlaylistCommand Command, string? Source) Read()
    {
        var command = (_eventSelected ? _event! : _command!).Read();
        var source = _eventSelected && !string.IsNullOrWhiteSpace(_source.Text) ? _source.Text.Trim() : null;
        PinnedActionOptions.ValidateCommand(command, _host.Options, source); return (command, source);
    }
    internal async Task SendAsync() => await Work(async () =>
    {
        var action = Read(); await _host.SendManualAsync(action.Command, action.Source, _name.Text.Trim() is { Length: > 0 } name ? name : null);
        Notice("Sent " + ShowEditSession.CommandSummary(action.Command) + " • " + (action.Command.Device ?? "this Controller"));
    });
    internal async Task SavePinAsync() => await Work(async () =>
    {
        var action = Read();
        var pin = new PinnedActionOptions { Id = _editing?.Id ?? Guid.NewGuid().ToString("N"), Name = _name.Text.Trim(), Command = action.Command, EventSource = action.Source };
        pin.Validate(_host.Options); await _host.SavePinnedActionAsync(_editing, pin); _editing = pin; Notice($"Pinned ‘{pin.Name}’. Its play button sends immediately.");
    });
    private async Task RemovePinAsync() => await Work(async () =>
    { if (_editing is null) return; await _host.SavePinnedActionAsync(_editing, null); _editing = null; _name.Clear(); Notice("Pin removed."); });
    private async Task Work(Func<Task> action)
    {
        if (_busy || IsDisposed) return; _busy = true; State();
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) Notice(ex.Message, true); }
        finally { if (!IsDisposed) { _busy = false; State(); } }
    }
    internal void Notice(string text, bool error = false) { _status.Text = text; _status.ForeColor = error ? DarkTheme.Error : DarkTheme.Muted; }
    private void State()
    {
        _send.Enabled = _pin.Enabled = _commandMode.Enabled = _eventMode.Enabled = _editorHost.Enabled = _source.Enabled = _name.Enabled = !_busy;
        _pin.Text = _editing is null ? "⚑  Pin action" : "Save pin"; _new.Visible = _remove.Visible = _editing is not null;
        _new.Enabled = _remove.Enabled = !_busy;
    }
}
