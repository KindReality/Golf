using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// A short, non-activating status notice remains visible even after calibration is turned off.
internal sealed class ConfigurationModeNotice : Forms.Form
{
    private readonly Forms.Label _label = new() { Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.8) };
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
    }
    public void Display(string message, bool error = false)
    {
        _hide.Stop();
        _label.Text = message;
        _label.ForeColor = error ? Color.LightCoral : Color.Azure;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        Location = new(screen.Left + (screen.Width - Width) / 2, screen.Top + 40);
        Show();
        BringToFront();
        _hide.Start();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hide.Stop();
        base.Dispose(disposing);
    }
}
