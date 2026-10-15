using Controller.Application;
using Controller.Networking;
using PhotoBooth.Networking;
using System.Drawing.Drawing2D;

namespace Controller.Windows;

internal enum LiveTargetMode { Video, Audio, Roles }

internal sealed class LivePage : UserControl
{
    private readonly ControllerHost _host;
    private readonly HashSet<LiveTarget> _selected = [];
    private readonly HashSet<string> _selectedRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<LiveTargetMode, Button> _modeButtons = [];
    private LiveTargetMode _mode;
    private readonly FlowLayoutPanel _targets = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new(4) };
    private readonly FlowLayoutPanel _media = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Padding = new(4) };
    private readonly ToolTip _tips = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = DarkTheme.Accent, Text = "Discovering Experience devices…", AutoEllipsis = true };
    private readonly NumericUpDown _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 65 };
    private readonly CheckBox _mute = new() { Text = "Mute video", Checked = true, AutoSize = true };
    private readonly ComboBox _filter = new DarkComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _search = new() { Width = 190, PlaceholderText = "Find media" };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private string _signature = "", _filesSignature = "";
    private string[] _files = [];
    private bool _sending;
    private readonly Image _tablet, _speaker;
    public LivePage(ControllerHost host)
    {
        _host = host; Dock = DockStyle.Fill; BackColor = DarkTheme.Background; ForeColor = DarkTheme.Text; Font = new("Segoe UI", 10);
        _tablet = Asset("Tablet.png"); _speaker = Asset("Speaker.png");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new(16) };
        layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Absolute, 48)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40)); layout.RowStyles.Add(new(SizeType.Absolute, 164)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        modes.Controls.Add(new Label { Text = "TARGET", AutoSize = true, ForeColor = DarkTheme.Accent, Padding = new(0, 8, 12, 0) });
        foreach (var mode in Enum.GetValues<LiveTargetMode>())
        {
            var button = new Button { Text = mode.ToString(), AutoSize = true, AccessibleName = "Target " + mode };
            DarkTheme.Button(button, mode == _mode); button.Click += (_, _) => SetTargetMode(mode);
            _modeButtons.Add(mode, button); modes.Controls.Add(button);
        }
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true }; DarkTheme.Button(b); b.Click += (_, _) => action(); toolbar.Controls.Add(b); return b; }
        Button("Select all", SelectAll);
        Button("Clear", () => { if (_mode == LiveTargetMode.Roles) _selectedRoles.Clear(); else _selected.RemoveWhere(t => t.Audio == (_mode == LiveTargetMode.Audio)); _signature = ""; RefreshState(); SelectionStatus(); });
        Button("Discover", () => _ = DiscoverAsync());
        toolbar.Controls.Add(new Label { Text = "Audio volume %", AutoSize = true, Padding = new(12, 8, 0, 0) });
        _volume.BackColor = DarkTheme.Surface; _volume.ForeColor = ForeColor; toolbar.Controls.Add(_volume); toolbar.Controls.Add(_mute);
        layout.Controls.Add(modes, 0, 0); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(_targets, 0, 2);
        var mediaTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        mediaTools.Controls.Add(new Label { Text = "CLICK MEDIA TO PLAY ONCE", AutoSize = true, ForeColor = DarkTheme.Accent, Padding = new(0, 8, 12, 0) });
        _filter.Items.AddRange(["All media", "Videos", "Audio"]); _filter.SelectedIndex = 1; _filter.Enabled = false; DarkTheme.Choices(_filter);
        _search.BackColor = DarkTheme.Surface; _search.ForeColor = ForeColor;
        _filter.SelectedIndexChanged += (_, _) => RenderMedia(); _search.TextChanged += (_, _) => RenderMedia();
        mediaTools.Controls.Add(_filter); mediaTools.Controls.Add(_search);
        layout.Controls.Add(mediaTools, 0, 3); layout.Controls.Add(_media, 0, 4); layout.Controls.Add(_status, 0, 5); Controls.Add(layout);
        _refresh.Tick += (_, _) => RefreshState();
        VisibleChanged += (_, _) => { if (Visible) { _refresh.Start(); RefreshState(); _ = DiscoverAsync(); } else _refresh.Stop(); };
    }
    internal void SetTargetMode(LiveTargetMode mode)
    {
        _mode = mode;
        foreach (var pair in _modeButtons) DarkTheme.Button(pair.Value, pair.Key == mode);
        _filter.Enabled = mode == LiveTargetMode.Roles;
        _filter.SelectedIndex = mode == LiveTargetMode.Video ? 1 : mode == LiveTargetMode.Audio ? 2 : 0;
        _signature = ""; RefreshState(); SelectionStatus();
    }
    private void SelectAll()
    {
        var peers = _host.Experiences.Snapshot();
        if (_mode == LiveTargetMode.Roles)
            foreach (var role in LivePlayback.Roles(peers).Where(r => r.Screens + r.Speakers > 0)) _selectedRoles.Add(role.Name);
        else foreach (var peer in peers.Where(p => p.Usable))
        {
            if (_mode == LiveTargetMode.Video) foreach (var screen in peer.Inventory.Screens.Where(s => s.Playable)) _selected.Add(new(peer.Inventory.Hostname, screen.Id, screen.Display));
            else foreach (var speaker in peer.Inventory.Speakers) _selected.Add(new(peer.Inventory.Hostname, speaker.Id));
        }
        _signature = ""; RefreshState(); SelectionStatus();
    }
    private void SelectionStatus() => _status.Text = _mode == LiveTargetMode.Roles
        ? $"{_selectedRoles.Count} roles selected • click video or audio to play on matching outputs"
        : $"{_selected.Count(t => t.Audio == (_mode == LiveTargetMode.Audio))} " + (_mode == LiveTargetMode.Audio ? "speakers" : "screens") + " selected • click media to play once";
    internal void ActivatePage() { _refresh.Start(); RefreshState(); _ = DiscoverAsync(); }
    private static Image Asset(string name)
    {
        using var stream = typeof(LivePage).Assembly.GetManifestResourceStream("Controller.Assets." + name) ?? throw new InvalidOperationException("Missing Live artwork.");
        using var image = Image.FromStream(stream); return new Bitmap(image);
    }
    private async Task DiscoverAsync()
    { try { await _host.DiscoverExperiencesAsync(); } catch (Exception ex) { if (!IsDisposed) _status.Text = "Discovery failed: " + ex.Message; } }
    private static void Clear(Control parent) { foreach (var child in parent.Controls.Cast<Control>().ToArray()) child.Dispose(); }
    internal void RefreshState()
    {
        if (IsDisposed) return;
        var peers = _host.Experiences.Snapshot();
        var signature = _mode + ":" + _targets.ClientSize.Width + string.Join('|', peers.Select(p => ExperienceInventory.Revision(p.Inventory.Serialize()) + p.Online + p.Ambiguous)) + string.Join('|', _selected.OrderBy(t => t.Hostname).ThenBy(t => t.Display).ThenBy(t => t.Id)) + string.Join('|', _selectedRoles.Order(StringComparer.OrdinalIgnoreCase));
        if (signature != _signature)
        {
            _signature = signature; var scroll = _targets.AutoScrollPosition; _targets.SuspendLayout(); Clear(_targets);
            if (_mode == LiveTargetMode.Roles)
            {
                var roles = LivePlayback.Roles(peers);
                foreach (var role in roles) AddRoleTile(role);
                if (roles.Length == 0) _targets.Controls.Add(new Label { Text = "No roles have been reported.\nAssign Roles to screens or audio outputs in the Experience configuration.", AutoSize = true, Padding = new(16) });
            }
            else foreach (var peer in peers)
            {
                var inventory = peer.Inventory;
                var groupWidth = Math.Max(350, _targets.ClientSize.Width - 28);
                var columns = Math.Max(1, (groupWidth - 28) / 172);
                var rows = (int)Math.Ceiling((_mode == LiveTargetMode.Video ? inventory.Screens.Length : inventory.Speakers.Length) / (double)columns);
                var group = new DarkCard { Dock = DockStyle.None, Width = groupWidth, Height = Math.Clamp(54 + rows * 208, 270, 530), Padding = new(14), Margin = new(6), BackColor = Color.FromArgb(22, 49, 68) };
                var title = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = DarkTheme.Accent, Text = inventory.DeviceName + (peer.Ambiguous ? " • DUPLICATE NAME" : peer.Online ? " • ONLINE" : " • OFFLINE"), Font = new(Font, FontStyle.Bold) };
                var tiles = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
                if (_mode == LiveTargetMode.Video) foreach (var screen in inventory.Screens)
                {
                    var target = new LiveTarget(inventory.Hostname, screen.Id, screen.Display);
                    AddTile(tiles, target, screen.DisplayLabel + " / " + screen.Name, screen.MediaBound ? "Media locked" : screen.Kind != "video" ? screen.Kind : !screen.Available ? "Unavailable" : "Video screen", peer.Usable && screen.Playable, _tablet);
                }
                if (_mode == LiveTargetMode.Audio) foreach (var speaker in inventory.Speakers) AddTile(tiles, new(inventory.Hostname, speaker.Id), speaker.Name, "Audio" + (speaker.Default ? " • default" : ""), peer.Usable, _speaker);
                group.Controls.Add(tiles); group.Controls.Add(title); _targets.Controls.Add(group);
            }
            if (peers.Length == 0 && _mode != LiveTargetMode.Roles) _targets.Controls.Add(new Label { Text = "No Experience devices found yet.\nRun Install-Experience.cmd on each Experience PC to enable Live discovery.", AutoSize = true, MaximumSize = new(700, 0), Padding = new(16) });
            if (_status.Text.StartsWith("Discovering", StringComparison.Ordinal)) SelectionStatus();
            _targets.ResumeLayout(); _targets.AutoScrollPosition = new(-scroll.X, -scroll.Y);
        }
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
            var files = PhotoBooth.Media.MediaLibrary.Read(root).Where(entry => !entry.IsFolder).Select(entry=>entry.RelativePath)
                .Where(f => LivePlayback.Videos.Contains(Path.GetExtension(f)) || LivePlayback.Audio.Contains(Path.GetExtension(f))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var signatureFiles = string.Join('|', files);
            if (_filesSignature != signatureFiles) { _filesSignature = signatureFiles; _files = files; RenderMedia(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Media folder unavailable: " + ex.Message; }
    }
    private void AddTile(Control panel, LiveTarget target, string title, string detail, bool enabled, Image artwork)
    {
        var tile = new LiveTile(artwork, title, detail) { Text = title + " " + detail, AccessibleName = title + " " + detail, Width = 164, Height = 200, Enabled = enabled, Selected = _selected.Contains(target), Tag = target, Margin = new(4) };
        tile.Click += (_, _) => { if (!_selected.Add(target)) _selected.Remove(target); tile.Selected = _selected.Contains(target); tile.Invalidate(); SelectionStatus(); };
        _tips.SetToolTip(tile, title + "\n" + detail); panel.Controls.Add(tile);
    }
    private void AddRoleTile(LiveRole role)
    {
        var detail = $"{role.Screens} screens • {role.Speakers} speakers";
        var tile = new LiveTile(role.Screens > 0 ? _tablet : _speaker, role.Name, detail)
        {
            Text = role.Name + " " + detail, AccessibleName = "Role " + role.Name + " " + detail,
            Width = 200, Height = 200, Enabled = role.Screens + role.Speakers > 0,
            Selected = _selectedRoles.Contains(role.Name), Tag = role.Name, Margin = new(4)
        };
        tile.Click += (_, _) => { if (!_selectedRoles.Add(role.Name)) _selectedRoles.Remove(role.Name); tile.Selected = _selectedRoles.Contains(role.Name); tile.Invalidate(); SelectionStatus(); };
        _tips.SetToolTip(tile, "Role: " + role.Name + "\n" + detail + " available across Experience devices\nVideo plays on matching screens; audio plays on matching speakers.");
        _targets.Controls.Add(tile);
    }
    private void RenderMedia()
    {
        if (IsDisposed) return;
        Clear(_media);
        foreach (var file in _files.Where(f => f.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) && (_filter.SelectedIndex == 0 || LivePlayback.Videos.Contains(Path.GetExtension(f)) == (_filter.SelectedIndex == 1))))
        {
            var video = LivePlayback.Videos.Contains(Path.GetExtension(file));
            var button = new Button { Text = (video ? "▶  VIDEO\n\n" : "♫  AUDIO\n\n") + file, Width = 210, Height = 124, Margin = new(5), AccessibleName = "Play " + file, Tag = file };
            DarkTheme.Button(button); button.Click += async (_, _) => await PlayAsync(file); _media.Controls.Add(button);
        }
    }
    internal async Task PlayAsync(string filename)
    {
        if (_sending || IsDisposed) return; _sending = true;
        try
        {
            var video = LivePlayback.Videos.Contains(Path.GetExtension(filename));
            if (_mode != LiveTargetMode.Roles && video != (_mode == LiveTargetMode.Video)) { _status.Text = "Choose media matching the target mode."; return; }
            var peers = _host.Experiences.Snapshot();
            var plan = _mode == LiveTargetMode.Roles
                ? LivePlayback.BuildRoles(_host.Options, peers, _selectedRoles, filename, (int)_volume.Value, _mute.Checked)
                : LivePlayback.Build(_host.Options, peers, _selected.Where(t => t.Audio == (_mode == LiveTargetMode.Audio)), filename, (int)_volume.Value, _mute.Checked);
            if (plan.Commands.Length == 0) { _status.Text = plan.Skipped.Length == 0 ? "Select at least one " + (_mode == LiveTargetMode.Roles ? "role." : video ? "video screen." : "speaker.") : string.Join("; ", plan.Skipped); return; }
            var results = await Task.WhenAll(plan.Commands.Select(async bytes => { try { await _host.SendLiveAsync(bytes); return (string?)null; } catch (Exception ex) { return ex.Message; } }));
            if (!IsDisposed) _status.Text = $"Sent {filename} • {results.Count(r => r is null)} commands" + (plan.Skipped.Length + results.Count(r => r is not null) > 0 ? " • " + string.Join("; ", plan.Skipped.Concat(results.OfType<string>())) : " • playback once");
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { _sending = false; }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _refresh.Dispose(); _tips.Dispose(); _tablet.Dispose(); _speaker.Dispose(); } base.Dispose(disposing); }
}
internal sealed class LiveTile(Image artwork, string title, string detail) : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(22, 49, 68));
        using var fill = new SolidBrush(Selected ? Color.FromArgb(65, 0, 155, 245) : Color.FromArgb(26, 0, 145, 225));
        using var border = new Pen(Enabled ? Selected ? Color.FromArgb(60, 190, 255) : Color.FromArgb(55, 107, 138) : Color.FromArgb(50, 67, 78), Selected ? 2.5f : 1f);
        var rect = new Rectangle(2, 2, Width - 5, Height - 5); g.FillRectangle(fill, rect); g.DrawRectangle(border, rect);
        var scale = Math.Min((Width - 28f) / artwork.Width, 94f / artwork.Height); var width = artwork.Width * scale; var height = artwork.Height * scale;
        g.DrawImage(artwork, (Width - width) / 2, 14, width, height);
        TextRenderer.DrawText(g, title, Font, new Rectangle(8, 111, Width - 16, 61), Enabled ? Color.FromArgb(216, 239, 255) : Color.FromArgb(116, 142, 158), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, detail, Font, new Rectangle(8, Height - 26, Width - 16, 20), Selected && Enabled ? Color.FromArgb(80, 201, 255) : Color.FromArgb(136, 164, 182), TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        if (Selected) TextRenderer.DrawText(g, "✓", Font, new Point(Width - 24, 6), Color.FromArgb(80, 201, 255));
        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(6, 6, Width - 13, Height - 13));
    }
}
