using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// A short, non-activating status notice remains visible even after calibration is turned off.
internal sealed class ConfigurationModeNotice : Forms.Form
{
    private readonly Forms.Label _label = new() { Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private string? _menuMessage, _menuDisplay;
    internal string Message => _label.Text;
    public void SetMenu(string? message, string? display = null)
    {
        if (_menuMessage == message && _menuDisplay == display) return;
        _menuMessage = message; _menuDisplay = display;
        if (message is null) { _hide.Stop(); Hide(); }
        else DisplayCore(message, false, true, display);
    }
    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080;
            return parameters;
        }
    }
    public ConfigurationModeNotice()
    {
        Text = "ExperienceX configuration status";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = Color.FromArgb(12, 23, 38);
        ForeColor = Color.Azure;
        Opacity = 0.94;
        Font = new Font("Segoe UI", 12);
        ClientSize = new(430, 60);
        Controls.Add(_label);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        // Configuration status must not become LED source content either.
        LedCalibrationWindow.SetWindowDisplayAffinity(Handle,0x11);
    }
    public void Display(string message, bool error = false, bool persistent = false, string? display = null)
    {
        // A pending choice owns the HUD until completed or canceled.
        if (_menuMessage is not null) return;
        DisplayCore(message, error, persistent, display);
    }
    private void DisplayCore(string message, bool error, bool persistent, string? display)
    {
        _hide.Stop();
        _label.Text = message;
        _label.ForeColor = error ? Color.LightCoral : Color.Azure;
        var screen = (Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(display, StringComparison.OrdinalIgnoreCase)) ?? Forms.Screen.FromPoint(Forms.Cursor.Position)).WorkingArea;
        var measured = Forms.TextRenderer.MeasureText(message, Font);
        Width = Math.Min(Math.Max(1, screen.Width - 32), Math.Max(430, measured.Width + 32));
        var wrapped = Forms.TextRenderer.MeasureText(message, Font, new Size(Math.Max(1, Width - 24), int.MaxValue), Forms.TextFormatFlags.WordBreak);
        Height = Math.Max(60, wrapped.Height + 24);
        Location = new(screen.Left + (screen.Width - Width) / 2, screen.Top + 40);
        Show();
        BringToFront();
        if (!persistent) _hide.Start();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hide.Stop();
        base.Dispose(disposing);
    }
}
