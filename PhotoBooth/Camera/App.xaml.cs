using System.Windows;

namespace SaftApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            PhotoBooth.Diagnostics.Telemetry.Start("Camera");
            DispatcherUnhandledException += (_, args) =>
            {
                PhotoBooth.Diagnostics.Telemetry.Error("DispatcherUnhandledException", args.Exception);
                PhotoBooth.Diagnostics.Telemetry.Stop();
            };
            base.OnStartup(e);
        }
        protected override void OnExit(ExitEventArgs e)
        {
            PhotoBooth.Diagnostics.Telemetry.Stop();
            base.OnExit(e);
        }
    }
}
