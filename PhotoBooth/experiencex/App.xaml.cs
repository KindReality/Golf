namespace ExperienceX;

public partial class App : System.Windows.Application
{
    private SingleInstanceLease? _instance;
    private ExperienceHost? _host;
    private Task? _shutdown;
    private PhotoBooth.Updates.BuildUpdateMonitor? _updates;
    private RendererStallRecovery? _stallRecovery;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == RendererDiagnosticCapture.HelperArgument)
        {
            Shutdown(RendererDiagnosticCapture.RunHelper(e.Args));
            return;
        }
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
            configurationPath = MachineConfiguration.Resolve();
            var source = MachineConfiguration.Initialize(configurationPath, System.IO.Path.GetDirectoryName(configurationPath)!);
            PhotoBooth.Diagnostics.Telemetry.Start("ExperienceX", configurationPath);
            PhotoBooth.Diagnostics.Telemetry.Info("SingleInstanceAcquired", new
            {
                _instance.RecoveredAbandonedOwner
            });
            if (source is not null)
                PhotoBooth.Diagnostics.Telemetry.Info("MachineConfigurationCreated", new
                {
                    Path = configurationPath,
                    Source = source,
                    Hostname = Environment.MachineName
                });
        }
        catch (Exception ex)
        {
            PhotoBooth.Diagnostics.Telemetry.Start("ExperienceX");
            PhotoBooth.Diagnostics.Telemetry.Error("MachineConfigurationUnavailable", ex);
            System.Windows.MessageBox.Show("ExperienceX could not open its computer configuration in OneDrive.\n\n" + ex.Message, "ExperienceX configuration", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
        _host.ShutdownRequested += () => _shutdown ??= ShutdownAsync(cancelPendingRestart: true);
        _host.RestartRequested += () =>
        {
            if (_updates is not null) _updates.RequestRestart();
            else _host.ReportRestartFailure("The update service is not ready.");
        };
        try
        {
            _host.Start();
            _updates = new PhotoBooth.Updates.BuildUpdateMonitor("ExperienceX", System.IO.Path.GetDirectoryName(configurationPath)!);
            _stallRecovery = new(() => _host.RendererHealth, () => _host.RecoveryOptions, () => _updates.RequestRestart());
            _updates.ManualRestartFailed += _ => _stallRecovery?.RestartFailed();
            _updates.ManualRestartFailed += reason => Dispatcher.BeginInvoke(new Action(() => _host?.ReportRestartFailure(reason)));
            _host.FocusExperience();
        }
        catch (Exception ex)
        {
            PhotoBooth.Diagnostics.Telemetry.Error("ApplicationStartupFailed", ex);
            System.Windows.MessageBox.Show("ExperienceX could not start.\n\n" + ex.Message, "ExperienceX startup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            _shutdown ??= ShutdownAsync();
        }
    }
    private async Task ShutdownAsync(bool cancelPendingRestart = false)
    {
        _stallRecovery?.Dispose();
        try
        {
            if (_updates is not null) await _updates.StopAsync(cancelPendingRestart);
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
