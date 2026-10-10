using Controller.Application;
using Controller.Windows;
using PhotoBooth.Diagnostics;

namespace Controller;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var lease = SingleInstanceLease.TryAcquire();
        if (lease is null)
        {
            MessageBox.Show("An existing instance of Controller is already running.", "Controller", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Home Technologies", "Controller", "Configuration", "appsettings.json");
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--configuration")
            {
                MessageBox.Show("Usage: Controller.exe [--configuration path]", "Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            path = Path.GetFullPath(args[1]);
        }
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            Telemetry.Error("ControllerWindowError", e.Exception);
            MessageBox.Show(e.Exception.Message, "Controller error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        try { System.Windows.Forms.Application.Run(new ControlWindow(path)); }
        finally { Telemetry.Stop(); }
    }
}
