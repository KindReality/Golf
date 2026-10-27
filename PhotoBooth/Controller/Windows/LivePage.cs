using Controller.Application;
using Controller.Configuration;
using System.Text.Json;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class LivePage : UserControl
{
    private readonly ControllerHost _host;
    private readonly ManualActionPanel _manual;
    private readonly LiveMediaPanel _media;
    private readonly LiveActivityPanel _activity;
    private readonly FlowLayoutPanel _pins = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Padding = new(0, 3, 0, 0) };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly HashSet<string> _sendingPins = [];
    private readonly RowStyle _pinRow = new(SizeType.Absolute, 52);
    private readonly RowStyle _manualRow = new(SizeType.Absolute, 294);
    private string _pinSignature = "";
    internal ManualActionPanel Manual => _manual;
    internal LivePage(ControllerHost host)
    {
        _host = host; Dock = DockStyle.Fill; BackColor = DarkTheme.Background; ForeColor = DarkTheme.Text; Font = new("Segoe UI", 9);
        _manual = new(host); _media = new(host); _activity = new(host.Log);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new(10, 2, 10, 6) };
        layout.RowStyles.Add(_pinRow); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var pins = Card("PINNED ACTIONS", _pins); layout.Controls.Add(pins, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 69)); columns.ColumnStyles.Add(new(SizeType.Percent, 31));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new(0, 0, 6, 0) };
        left.RowStyles.Add(_manualRow); left.RowStyles.Add(new(SizeType.Percent, 100));
        var manual = new DarkCard { Dock = DockStyle.Fill, Padding = new(6), Margin = new(0, 0, 0, 4) }; manual.Controls.Add(_manual);
        left.Controls.Add(manual, 0, 0); left.Controls.Add(Card("MEDIA CONTROL", _media), 0, 1);
        columns.Controls.Add(left, 0, 0);
        var activity = new DarkCard { Padding = new(6), Margin = Padding.Empty }; activity.Controls.Add(_activity); columns.Controls.Add(activity, 1, 0);
        layout.Controls.Add(columns, 0, 1); Controls.Add(layout);
        _refresh.Tick += (_, _) => RefreshState();
        VisibleChanged += (_, _) => { if (Visible) ActivatePage(); else _refresh.Stop(); };
        RefreshState();
    }
    private static DarkCard Card(string title, Control content)
    {
        var card = new DarkCard { Padding = new(6), Margin = Padding.Empty };
        content.Dock = DockStyle.Fill; card.Controls.Add(content);
        card.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 20, ForeColor = DarkTheme.Accent }); return card;
    }
    internal void ActivatePage() { _refresh.Start(); _media.ActivatePage(); RefreshState(); }
    internal void SetTargetMode(LiveTargetMode mode) => _media.SetTargetMode(mode);
    internal Task PlayAsync(string file) => _media.PlayAsync(file);
    internal void RefreshState()
    {
        if (IsDisposed) return;
        _manualRow.Height = Math.Clamp(_manual.ContentHeight + 24, 294, 350);
        _media.RefreshState(); _activity.RefreshState();
        var pins = _host.Options.PinnedActions;
        var signature = JsonSerializer.Serialize(pins, ControllerOptions.Json);
        if (signature == _pinSignature) return; _pinSignature = signature;
        _pinRow.Height = pins.Length == 0 ? 52 : 98;
        foreach (var child in _pins.Controls.Cast<Control>().ToArray()) child.Dispose();
        if (pins.Length == 0) _pins.Controls.Add(new Label { Text = "Configure an action below, give it a name, then choose Pin action.", AutoSize = true, ForeColor = DarkTheme.Muted, Padding = Padding.Empty });
        foreach (var pin in pins)
        {
            var card = new TableLayoutPanel { Width = 238, Height = 46, ColumnCount = 2, RowCount = 1, Margin = new(0, 0, 6, 0), BackColor = DarkTheme.Raised, Padding = new(4) };
            card.ColumnStyles.Add(new(SizeType.Percent, 100)); card.ColumnStyles.Add(new(SizeType.Absolute, 88));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 88, WrapContents = false, Margin = Padding.Empty };
            var run = new ToolbarIconButton(ToolbarGlyph.Play, "Run pin " + pin.Name) { Size = new(38, 32), Enabled = !_sendingPins.Contains(pin.Id) };
            LiveControls.StyleButton(run, true); run.Click += (_, _) => _ = RunPinAsync(pin);
            var edit = new ToolbarIconButton(ToolbarGlyph.Edit, "Edit pin " + pin.Name) { Size = new(34, 32) };
            LiveControls.StyleButton(edit);
            edit.Click += (_, _) => _manual.LoadPin(pin);
            buttons.Controls.AddRange([run, edit]); card.Controls.Add(buttons, 1, 0);
            card.Controls.Add(new Label { Text = (pin.Command.Type == "event" ? "EVENT" : "COMMAND") + "\n" + pin.Name, Dock = DockStyle.Fill, ForeColor = pin.Command.Type == "event" ? DarkTheme.Warning : DarkTheme.Text, AutoEllipsis = true }, 0, 0);
            _pins.Controls.Add(card);
        }
    }
    internal async Task RunPinAsync(PinnedActionOptions pin)
    {
        if (!_sendingPins.Add(pin.Id)) return;
        _pinSignature = ""; RefreshState();
        try { await _host.SendManualAsync(pin.Command, pin.EventSource, pin.Name); if (!IsDisposed) _manual.Notice("Sent pin ‘" + pin.Name + "’."); }
        catch (Exception ex) { if (!IsDisposed) _manual.Notice(ex.Message, true); }
        finally { _sendingPins.Remove(pin.Id); if (!IsDisposed) { _pinSignature = ""; RefreshState(); } }
    }
    protected override void Dispose(bool disposing) { if (disposing) _refresh.Dispose(); base.Dispose(disposing); }
}
