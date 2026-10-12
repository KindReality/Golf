using System.IO;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Scan metadata off the dispatcher once a second, including media arriving after discovery.
internal sealed class ScreenMediaMonitor(Dispatcher dispatcher, Action<ScreenMediaCatalog> changed) : IAsyncDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AsyncTaskScope _tasks = new("Screen media inventory");
    private string? _directory, _failure;
    private bool _scanning, _disposed;
    public ScreenMediaCatalog Current { get; private set; } = ScreenMediaCatalog.Empty;

    public void Start(string directory)
    {
        Configure(directory);
        _timer.Tick += (_, _) => _tasks.Run(ScanAsync, "Read local screen media");
        _timer.Start();
    }
    public void Configure(string directory)
    {
        dispatcher.VerifyAccess();
        if (string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase)) return;
        _directory = directory;
        try { Publish(ScreenMediaCatalog.Read(directory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Publish(ScreenMediaCatalog.Empty);
            ReportFailure(ex);
        }
    }
    private async Task ScanAsync(CancellationToken token)
    {
        if (_disposed || _scanning || _directory is null) return;
        _scanning = true;
        var directory = _directory;
        try
        {
            var catalog = await Task.Run(() => ScreenMediaCatalog.Read(directory), token);
            token.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && _directory == directory) Publish(catalog);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ReportFailure(ex); }
        finally { _scanning = false; }
    }
    private void Publish(ScreenMediaCatalog catalog)
    {
        Current = catalog;
        _failure = null;
        changed(catalog);
    }
    private void ReportFailure(Exception ex)
    {
        if (_failure != ex.Message)
            Telemetry.Warning("ScreenMediaInventoryFailed", new { Directory = _directory, Reason = ex.Message, RetrySeconds = 1 });
        _failure = ex.Message;
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _timer.Stop();
        await _tasks.DisposeAsync();
    }
}
