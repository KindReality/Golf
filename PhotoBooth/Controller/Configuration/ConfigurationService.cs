using Controller.Diagnostics;

namespace Controller.Configuration;

internal sealed class ConfigurationService : IAsyncDisposable
{
    private readonly BuildEnvironment _environment;
    private readonly ActivityLog _log;
    private readonly Func<ControllerOptions, Task> _apply;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly FileSystemWatcher _watcher;
    private Task _loop = Task.CompletedTask;
    private string _accepted;
    private string? _failure;
    public string Path { get; }
    public ControllerOptions Initial { get; }

    public ConfigurationService(string path, BuildEnvironment environment, ActivityLog log, Func<ControllerOptions, Task> apply,
        string? applicationDirectory = null)
    {
        Path = path;
        _environment = environment;
        _log = log;
        _apply = apply;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var source = PhotoBooth.Configuration.MachineConfigurationFiles.Initialize(path,
                applicationDirectory ?? AppContext.BaseDirectory, json => ControllerOptions.Parse(json, environment));
            if (source is not null)
                _log.Info("MachineConfigurationCreated", "Created computer configuration from shipped defaults", new { Path, Source = source, Hostname = Environment.MachineName });
            else if (!File.Exists(path) && File.Exists(path + ".bak"))
            {
                var backup = File.ReadAllText(path + ".bak");
                ControllerOptions.Parse(backup, environment);
                ConfigurationDefaults.Write(path, backup);
                _log.Info("ConfigurationRecoveredFromBackup", "Recovered computer configuration from backup", new { Path });
            }
        }
        _accepted = File.ReadAllText(path);
        var populated = ConfigurationDefaults.Populate(_accepted, environment);
        if (populated != _accepted) { ConfigurationDefaults.Write(path, populated); _accepted = populated; }
        Initial = ControllerOptions.Parse(_accepted, environment);
        _watcher = new(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };
        _watcher.Changed += (_, _) => Signal();
        _watcher.Created += (_, _) => Signal();
        _watcher.Deleted += (_, _) => Signal();
        _watcher.Renamed += (_, _) => Signal();
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        _loop = WatchAsync();
    }

    private void Signal()
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WatchAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _changed.WaitAsync(1000, token).ConfigureAwait(false);
                await Task.Delay(75, token).ConfigureAwait(false);
                var text = await File.ReadAllTextAsync(Path, token).ConfigureAwait(false);
                if (text == _accepted) { _failure = null; continue; }
                var populated = ConfigurationDefaults.Populate(text, _environment);
                var next = ControllerOptions.Parse(populated, _environment);
                if (populated != text)
                {
                    // Avoid replacing a newer remote edit while filling required defaults.
                    if (await File.ReadAllTextAsync(Path, token).ConfigureAwait(false) != text) { Signal(); continue; }
                    ConfigurationDefaults.Write(Path, populated);
                }
                await _apply(next).ConfigureAwait(false);
                _accepted = populated;
                _failure = null;
                _log.Info("ConfigurationReloaded", "Configuration updated; running playlists retain their settings", new { Path, _environment.Environment });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_failure != ex.Message) _log.Error("ConfigurationRejected", ex, new { Path });
                _failure = ex.Message;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        _cancellation.Cancel();
        await _loop.ConfigureAwait(false);
        _cancellation.Dispose();
        _changed.Dispose();
    }
}
