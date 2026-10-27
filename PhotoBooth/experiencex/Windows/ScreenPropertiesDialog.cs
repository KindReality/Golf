using PhotoBooth.Appearance;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class ScreenPropertiesDialog : ConfigurationDialog
{
    private readonly Forms.TextBox _name = new() { Dock = Forms.DockStyle.Fill, AccessibleName = "Screen name" };
    private readonly Forms.ListBox _perspectives = new() { Dock = Forms.DockStyle.Fill, BorderStyle = Forms.BorderStyle.None, AccessibleName = "Perspective" };
    private readonly Forms.CheckedListBox _roles = new() { Dock = Forms.DockStyle.Fill, BorderStyle = Forms.BorderStyle.None, CheckOnClick = true, IntegralHeight = false, AccessibleName = "Roles" };
    private readonly Forms.TextBox _newRole = new() { Dock = Forms.DockStyle.Fill, PlaceholderText = "New role", AccessibleName = "New role" };
    private readonly Forms.Label _status = new() { Dock = Forms.DockStyle.Fill, AutoEllipsis = true };
    private readonly Forms.Button _accept = new() { Text = "Create screen", AutoSize = true };
    private readonly ScreenOptions? _editing;
    private readonly HashSet<string> _reservedNames;
    private sealed record AcceptedChoice(string Name, PerspectiveSelection? Perspective, string Roles);
    private AcceptedChoice? _accepted;
    public string ScreenName => _accepted?.Name ?? _name.Text.Trim();
    public PerspectiveSelection? Selection => _accepted is { } choice ? choice.Perspective : _editing is { IsVideo: false } ? _editing.Perspective : PerspectiveChoices.Selected(_perspectives);
    public string Roles => _accepted?.Roles ?? string.Join(",", RoleList.Parse(string.Join(",", _roles.CheckedItems.Cast<string>())));

    internal static string[] KnownRoles(ExperienceOptions options, IEnumerable<string>? shared = null) => RoleCatalog.Names(
        options.DisplayScreens.Values.SelectMany(s => s).Select(s => s.Roles)
            .Concat(options.AudioDevices.Values.Select(d => d.Roles)).Concat(shared ?? []));

    public ScreenPropertiesDialog(IReadOnlyList<ScreenOptions> existing, IReadOnlyList<PerspectiveSelection> perspectives,
        IEnumerable<string> roles, string displayLabel, ScreenOptions? editing = null)
    {
        _editing = editing;
        Text = editing is null ? "New video screen" : "Screen properties"; ClientSize = new(790, 490); MinimumSize = new(660, 430);
        _accept.Text = editing is null ? "Create screen" : "Save changes";
        _reservedNames = existing.Where(s => s.Id != editing?.Id).SelectMany(s => new[] { s.Name, s.DefaultName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _name.Text = editing?.Name ?? ScreenOptions.AvailableDefaultName(existing);
        PerspectiveChoices.Populate(_perspectives, perspectives, editing?.Perspective, editing?.IsLed == true);
        var selectedRoles = RoleList.Parse(editing?.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles.SelectMany(RoleList.Parse).Concat(selectedRoles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            _roles.Items.Add(role, selectedRoles.Contains(role));

        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new(22, 16, 22, 16), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 38, 27, 34, 36 }) layout.RowStyles.Add(new(Forms.SizeType.Absolute, height));
        layout.RowStyles.Add(new(Forms.SizeType.Percent, 100)); layout.RowStyles.Add(new(Forms.SizeType.Absolute, 44)); layout.RowStyles.Add(new(Forms.SizeType.Absolute, 46));
        layout.Controls.Add(new Forms.Label { Text = editing is null ? "NEW VIDEO SCREEN" : "SCREEN PROPERTIES", Dock = Forms.DockStyle.Fill, Font = new("Segoe UI", 16, System.Drawing.FontStyle.Bold), ForeColor = DarkFormsTheme.Accent }, 0, 0);
        layout.Controls.Add(new Forms.Label { Text = "Screen name", Dock = Forms.DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 1);
        layout.Controls.Add(_name, 0, 2);
        layout.Controls.Add(new Forms.Label { Text = displayLabel + (editing is null ? " • Adjust its points after creation." : " • Edit this screen's settings."), Dock = Forms.DockStyle.Fill, AutoEllipsis = true, ForeColor = DarkFormsTheme.Muted, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 3);
        var choices = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Forms.Padding.Empty };
        choices.ColumnStyles.Add(new(Forms.SizeType.Percent, 56)); choices.ColumnStyles.Add(new(Forms.SizeType.Percent, 44));
        var sources = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new(0, 0, 14, 0) };
        sources.RowStyles.Add(new(Forms.SizeType.Absolute, 28)); sources.RowStyles.Add(new(Forms.SizeType.Percent, 100));
        sources.Controls.Add(new Forms.Label { Text = "PERSPECTIVE", Dock = Forms.DockStyle.Fill, ForeColor = DarkFormsTheme.Accent }, 0, 0); sources.Controls.Add(_perspectives, 0, 1);
        var groups = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Forms.Padding.Empty };
        groups.RowStyles.Add(new(Forms.SizeType.Absolute, 28)); groups.RowStyles.Add(new(Forms.SizeType.Percent, 100)); groups.RowStyles.Add(new(Forms.SizeType.Absolute, 42));
        groups.Controls.Add(new Forms.Label { Text = "ROLES • SELECT ANY", Dock = Forms.DockStyle.Fill, ForeColor = DarkFormsTheme.Accent }, 0, 0); groups.Controls.Add(_roles, 0, 1);
        var add = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0, 5, 0, 0) };
        add.ColumnStyles.Add(new(Forms.SizeType.Percent, 100)); add.ColumnStyles.Add(new(Forms.SizeType.AutoSize));
        var addRole = new Forms.Button { Text = "Add", AutoSize = true, AccessibleName = "Add role" }; addRole.Click += (_, _) => AddRoles();
        add.Controls.Add(_newRole, 0, 0); add.Controls.Add(addRole, 1, 0); groups.Controls.Add(add, 0, 2);
        choices.Controls.Add(sources, 0, 0); choices.Controls.Add(groups, 1, 0); layout.Controls.Add(choices, 0, 4);
        if (editing is { IsVideo: false }) { sources.Visible = false; choices.ColumnStyles[0].Width = 0; choices.ColumnStyles[1].Width = 100; }
        layout.Controls.Add(_status, 0, 5);
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.RightToLeft, WrapContents = false, Margin = Forms.Padding.Empty };
        var cancel = new Forms.Button { Text = "Cancel", AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(_accept); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout); AcceptButton = _accept; CancelButton = cancel;
        _accept.Click += (_, _) =>
        {
            if (!ValidateSelection()) return;
            _accepted = new(ScreenName, Selection, Roles);
            DialogResult = Forms.DialogResult.OK; Close();
        };
        _name.TextChanged += (_, _) => ValidateSelection();
        _newRole.KeyDown += (_, e) => { if (e.KeyCode != Forms.Keys.Enter) return; e.SuppressKeyPress = true; AddRoles(); };
        _roles.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke(new Action(() => ValidateSelection())); };
        Shown += (_, _) => { ValidateSelection(); _name.Focus(); _name.SelectAll(); };
        ValidateSelection();
    }
    private void AddRoles()
    {
        try
        {
            var names = RoleList.Parse(_newRole.Text);
            if (names.Length == 0) return;
            RoleList.Parse(string.Join(",", _roles.CheckedItems.Cast<string>().Concat(names)));
            foreach (var name in names)
            {
                var index = Enumerable.Range(0, _roles.Items.Count).FirstOrDefault(i => string.Equals((string)_roles.Items[i], name, StringComparison.OrdinalIgnoreCase), -1);
                if (index < 0) index = _roles.Items.Add(name);
                _roles.SetItemChecked(index, true);
            }
            _newRole.Clear(); ValidateSelection();
        }
        catch (ArgumentException ex) { _status.Text = ex.Message; _status.ForeColor = DarkFormsTheme.Error; }
    }
    private bool ValidateSelection()
    {
        try
        {
            ScreenOptions.ValidateName(ScreenName);
            if (_reservedNames.Contains(ScreenName)) throw new ArgumentException("This display already uses that screen name. Choose another name.");
            _ = Roles;
            _accept.Enabled = true; _status.ForeColor = DarkFormsTheme.Muted;
            _status.Text = _roles.Items.Count == 0 ? "No roles configured yet. Add one above, or continue without roles." : "Roles are optional. Checked roles are saved with this screen.";
            return true;
        }
        catch (ArgumentException ex) { _accept.Enabled = false; _status.Text = ex.Message; _status.ForeColor = DarkFormsTheme.Error; return false; }
    }
}
