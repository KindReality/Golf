using PhotoBooth.Networking;

namespace Controller.Windows;

internal sealed class LedPerspectiveEditor : Form
{
    private readonly CancellationTokenSource _closed = new();
    private readonly ComboBox _devices = new DarkComboBox { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _segments = new DarkComboBox { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _kind = new DarkComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _address = new() { Width = 290 };
    private readonly TextBox _name = new() { Width = 300 };
    private readonly NumericUpDown _fps = new() { Minimum = 1, Maximum = 120, Value = 30, Width = 90 };
    private readonly NumericUpDown _thickness = new() { Minimum = .01m, Maximum = 100, DecimalPlaces = 2, Value = 10, Width = 90 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(500, 0) };
    private readonly Button _save = new() { Text = "Save perspective", AutoSize = true, Enabled = false };
    private readonly PerspectiveDefinition? _original;
    private bool _busy;
    public PerspectiveDefinition? Result { get; private set; }
    public LedPerspectiveEditor(PerspectiveDefinition? original = null)
    {
        _original = original;
        Text = "Controller • LED perspective"; Font = new("Segoe UI", 10);
        BackColor = DarkTheme.Background; ForeColor = DarkTheme.Text;
        ClientSize = new(555, 515); StartPosition = FormStartPosition.CenterParent;
        foreach(var field in new Control[]{_address,_name,_fps,_thickness}) { field.BackColor=DarkTheme.Surface;field.ForeColor=DarkTheme.Text; }
        foreach(var choice in new[]{_devices,_segments,_kind}) DarkTheme.Choices(choice);
        HandleCreated+=(_,_)=>PhotoBooth.Appearance.DarkWindowChrome.Apply(Handle);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(18), AutoScroll = true };
        void Row(string label, params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 3, 0, 3) };
            row.Controls.Add(new Label { Text = label, Width = 90, Padding = new(0, 5, 0, 0) });
            row.Controls.AddRange(controls); content.Controls.Add(row);
        }
        Button ActionButton(string text, Func<Task> action)
        { var b = new Button { Text = text, AutoSize = true }; DarkTheme.Button(b); b.Click += async (_, _) => await WorkAsync(action); return b; }
        Row("Name", _name); Row("Devices", _devices);
        Row("", ActionButton("Discover / refresh", DiscoverAsync));
        Row("Address", _address, ActionButton("Read", ReadAddressAsync)); Row("Segment", _segments);
        _kind.Items.AddRange(["Pixel array", "Pixel matrix"]); _kind.SelectedIndex = 0;
        Row("Type", _kind); Row("FPS", _fps); Row("Thickness %", _thickness);
        var bottom = new FlowLayoutPanel { AutoSize = true };
        DarkTheme.Button(_save); var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel }; DarkTheme.Button(cancel);
        bottom.Controls.AddRange([_save, cancel]); content.Controls.Add(bottom); content.Controls.Add(_status); Controls.Add(content);
        cancel.Click += (_, _) => Close();
        _save.Click += async (_, _) => await WorkAsync(SaveAsync); CancelButton = cancel; AcceptButton = _save;
        _devices.SelectedIndexChanged += (_, _) => SelectDevice();
        _segments.SelectedIndexChanged += (_, _) => { if (_original is null && _segments.SelectedItem is WledSegment segment) _kind.SelectedIndex=segment.Matrix ? 1 : 0; UpdateStatus(); };
        _kind.SelectedIndexChanged += (_, _) => UpdateStatus();
        _name.Text = original?.Name ?? "";
        if (original?.Led is { } led) { _address.Text = led.Address; _fps.Value = led.FramesPerSecond; _thickness.Value = (decimal)led.SamplingThicknessPercent; _kind.SelectedIndex = original.Type == "pixelMatrix" ? 1 : 0; }
        _kind.Enabled=original is null; // Placement geometry has a fixed type once a perspective is referenced.
        Load += async (_, _) => await WorkAsync(async () => { if (_address.Text.Length > 0) await ReadAddressAsync(); else await DiscoverAsync(); });
        FormClosed += (_, _) => _closed.Cancel();
    }
    private async Task WorkAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; _save.Enabled = false; _status.Text = "Reading WLED devices…";
        try { await action(); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { _busy = false; if (!IsDisposed) _save.Enabled = _segments.SelectedItem is WledSegment; }
    }
    private async Task DiscoverAsync()
    {
        var devices = await WledDiscovery.FindAsync(_closed.Token);
        if (IsDisposed) return;
        _devices.Items.Clear(); _devices.Items.AddRange(devices);
        if (devices.Length > 0) _devices.SelectedIndex = 0;
        else _status.Text = "No WLED devices discovered. You can enter an address manually.";
    }
    private async Task ReadAddressAsync()
    {
        using var client = new WledClient(); var device = await client.ReadAsync(_address.Text.Trim(), _closed.Token);
        if (IsDisposed) return;
        if (_original?.Led is { } led && !device.Mac.Equals(led.DeviceMac, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("This address belongs to a different WLED device.");
        _devices.Items.Clear(); _devices.Items.Add(device); _devices.SelectedIndex = 0;
    }
    private void SelectDevice()
    {
        _segments.Items.Clear();
        if (_devices.SelectedItem is not WledDevice device) return;
        _address.Text = device.Address; _segments.Items.AddRange(device.Segments);
        var at = Array.FindIndex(device.Segments, s => s.Id == _original?.Led?.SegmentId);
        if (device.Segments.Length > 0) _segments.SelectedIndex = Math.Max(0, at);
    }
    private void UpdateStatus()
    {
        if (_segments.SelectedItem is not WledSegment segment) { _save.Enabled = false; return; }
        _thickness.Enabled = _kind.SelectedIndex == 0;
        _save.Enabled = !_busy;
        _status.Text = $"{segment} • RGB • DDP / UDP 4048\nWLED Sync: main segment only OFF, respect LED maps ON, finite realtime timeout.";
        if (_name.Text.Length == 0 && _devices.SelectedItem is WledDevice device) _name.Text = $"{device.Name} {segment.Name} {segment.Id}";
    }
    private async Task SaveAsync()
    {
        try
        {
            if (_devices.SelectedItem is not WledDevice device || _segments.SelectedItem is not WledSegment segment) return;
            if (_kind.SelectedIndex == 1 && !segment.Matrix) throw new ArgumentException("Select a two-dimensional WLED segment to create a pixel matrix.");
            using var client=new WledClient();await client.ValidateStreamingAsync(device.Address,_closed.Token);
            if(IsDisposed || _closed.IsCancellationRequested)return;
            Result = (_original ?? new PerspectiveDefinition()) with
            {
                Name = _name.Text.Trim(), Type = _kind.SelectedIndex == 1 ? "pixelMatrix" : "pixelArray",
                Led = device.Perspective(segment, _kind.SelectedIndex == 1, (int)_fps.Value, (double)_thickness.Value)
            };
            Result.Validate(); DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
