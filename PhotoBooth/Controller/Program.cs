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
        string path;
        string? templateDirectory = null;
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--configuration")
            {
                MessageBox.Show("Usage: Controller.exe [--configuration path]", "Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            path = Path.GetFullPath(args[1]);
        }
        else
        {
            try
            {
                path = PhotoBooth.Configuration.MachineConfigurationFiles.Resolve("Controller");
                templateDirectory = Path.GetDirectoryName(path);
            }
            catch (Exception ex)
            {
                Telemetry.Start("Controller");
                Telemetry.Error("MachineConfigurationUnavailable", ex);
                Telemetry.Stop();
                MessageBox.Show(ex.Message, "Controller configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            Telemetry.Error("ControllerWindowError", e.Exception);
            MessageBox.Show(e.Exception.Message, "Controller error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        try { System.Windows.Forms.Application.Run(new ControlWindow(path, templateDirectory)); }
        finally { Telemetry.Stop(); }
    }
}
