namespace ExperienceX;

public partial class App : System.Windows.Application
{
    private SingleInstanceLease? _instance;
    private ExperienceHost? _host;
    private Task? _shutdown;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _instance = SingleInstanceLease.TryAcquire("PhotoBooth.ExperienceX.SingleInstance");
        if (_instance is null)
        {
            PhotoBooth.Diagnostics.Telemetry.Start("ExperienceX");
            PhotoBooth.Diagnostics.Telemetry.Warning("SecondInstanceRejected");
            System.Windows.MessageBox.Show("An existing instance of ExperienceX is already running.",
                "ExperienceX already running", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }
        string configurationPath;
        try
        {
            configurationPath = LocalConfiguration.Resolve();
            var source = LocalConfiguration.Initialize(configurationPath, AppContext.BaseDirectory, LocalConfiguration.PreviousConfigurationPath());
            PhotoBooth.Diagnostics.Telemetry.Start("ExperienceX", configurationPath);
            PhotoBooth.Diagnostics.Telemetry.Info("SingleInstanceAcquired", new
            {
                _instance.RecoveredAbandonedOwner
            });
            if (source is not null)
                PhotoBooth.Diagnostics.Telemetry.Info("LocalConfigurationCreated", new
                {
                    Path = configurationPath,
                    Source = source
                });
        }
        catch (Exception ex)
        {
            PhotoBooth.Diagnostics.Telemetry.Start("ExperienceX");
            PhotoBooth.Diagnostics.Telemetry.Error("LocalConfigurationUnavailable", ex);
            System.Windows.MessageBox.Show("ExperienceX could not open its local configuration.\n\n" + ex.Message, "ExperienceX configuration", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            PhotoBooth.Diagnostics.Telemetry.Error("DispatcherUnhandledException", args.Exception);
            PhotoBooth.Diagnostics.Telemetry.Stop();
        };
        base.OnStartup(e);
        _host = new ExperienceHost(Dispatcher, configurationPath);
        _host.CloseRequested += () => _shutdown ??= ShutdownAsync();
        try
        {
            _host.Start();
        }
        catch (Exception ex)
        {
            PhotoBooth.Diagnostics.Telemetry.Error("ApplicationStartupFailed", ex);
            System.Windows.MessageBox.Show("ExperienceX could not start.\n\n" + ex.Message, "ExperienceX startup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            _shutdown ??= ShutdownAsync();
        }
    }
    private async Task ShutdownAsync()
    {
        try
        {
            if (_host is not null)
                await _host.DisposeAsync();
        }
        catch (Exception ex) { PhotoBooth.Diagnostics.Telemetry.Error("ApplicationShutdownFailed", ex); }
        finally { Shutdown(); }
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        PhotoBooth.Diagnostics.Telemetry.Stop();
        _instance?.Dispose();
        _instance = null;
        base.OnExit(e);
    }
}
