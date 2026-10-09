namespace Experience;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private ExperienceHost? _host;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        PhotoBooth.Diagnostics.Telemetry.Start("Experience");
        _instanceMutex = new System.Threading.Mutex(true, "PhotoBooth.Experience.SingleInstance", out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            PhotoBooth.Diagnostics.Telemetry.Warning("SecondInstanceRejected");
            System.Windows.MessageBox.Show("An existing instance of Experience is already running.",
                "Experience already running", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            PhotoBooth.Diagnostics.Telemetry.Error("DispatcherUnhandledException", args.Exception);
            PhotoBooth.Diagnostics.Telemetry.Stop();
        };
        base.OnStartup(e);
        _host = new ExperienceHost(Dispatcher);
        _host.Start();
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _host?.Dispose();
        PhotoBooth.Diagnostics.Telemetry.Stop();
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
