using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;
using PhotoBooth.Media;
using PhotoBooth.Networking;

namespace ExperienceX;

// No LAN discovery: Experience connects only to its Controller over IPv4 loopback.
internal sealed class LocalControllerConnection(Dispatcher dispatcher, Func<ControllerCatalog, Task> apply,
    HttpMessageHandler? transport = null, Func<string>? mediaDirectory = null) : IAsyncDisposable
{
    private sealed class Connection
    {
        public ControllerCatalog? Catalog;
        public string? Revision, Failure;
        public long LastCopy;
        public Task Copy = Task.CompletedTask;
    }
    private readonly AsyncTaskScope _tasks = new("Local controller connection");
    private readonly HttpClient _http = new(transport ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly MediaCache _cache = new();
    private readonly SemaphoreSlim _copyGate = new(1);
    private readonly List<CancellationTokenSource> _rounds = [];
    private LocalControllerOptions? _settings;
    private string? _directories;
    private CancellationTokenSource? _round;
    private Connection _connection = new();
    public void Configure(ExperienceOptions current)
    {
        var directories = current.VideoDirectory + "|" + current.AudioDirectory;
        if (_settings == current.LocalController && _directories == directories) return;
        _round?.Cancel(); _settings = current.LocalController; _directories = directories;
        _connection = new();
        if (!_settings.Enabled) return;
        _round = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token); _rounds.Add(_round);
        var token = _round.Token; var connection = _connection;
        _tasks.Run(async _ => { try { await Task.Run(() => RunAsync(current, connection, token), token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { } }, "Connect to local controller");
    }
    private async Task RunAsync(ExperienceOptions current, Connection connection, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await PollAsync(current, connection, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (connection.Failure != ex.Message) Telemetry.Warning("LocalControllerUnavailable", new { Address = "127.0.0.1", current.LocalController.Port, Reason = ex.Message, RetainedPrevious = true });
                connection.Failure = ex.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(current.LocalController.RetryIntervalSeconds), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    internal Task PollAsync(ExperienceOptions current, CancellationToken token) => PollAsync(current, _connection, token);
    private async Task PollAsync(ExperienceOptions current, Connection connection, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var address = LocalTestNetwork.Address is null ? "127.0.0.1" : "127.0.0.2";
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("http", address, current.LocalController.Port, "/catalog").Uri);
        if (connection.Revision is not null) request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + connection.Revision + "\"");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var catalog = connection.Catalog;
        if (response.StatusCode != HttpStatusCode.NotModified)
        {
            var bytes = await SnapshotDownload.ReadAsync(response, ControllerCatalog.MaximumBytes, timeout.Token).ConfigureAwait(false);
            catalog = JsonSerializer.Deserialize<ControllerCatalog>(bytes, ControllerSharedDefinitions.Json) ?? throw new IOException("Empty local controller catalog.");
            catalog.Validate();
            if (!catalog.Controllers.Single(c => c.ControllerId == catalog.LocalControllerId).Hostname.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local controller catalog belongs to a different computer.");
            await dispatcher.InvokeAsync(() => apply(catalog), DispatcherPriority.Background, token).Task.Unwrap();
            connection.Catalog = catalog; connection.Revision = ControllerSharedDefinitions.Revision(bytes);
            Telemetry.Info("LocalControllerCatalogApplied", new { catalog.LocalControllerId, Controllers = catalog.Controllers.Length, Perspectives = catalog.Controllers.Sum(c => c.Perspectives.Length) });
        }
        else if (catalog is not null) await dispatcher.InvokeAsync(() => apply(catalog), DispatcherPriority.Background, token).Task.Unwrap();
        else throw new IOException("Local controller returned no catalog.");
        if (connection.Failure is not null || connection.LastCopy == 0) Telemetry.Info("LocalControllerConnected", new { Address = "127.0.0.1", current.LocalController.Port });
        connection.Failure = null;
        if (current.LocalController.MediaSyncEnabled && connection.Copy.IsCompleted && (connection.LastCopy == 0 || Environment.TickCount64 - connection.LastCopy >= current.LocalController.MediaSyncIntervalSeconds * 1000))
        {
            connection.LastCopy = Environment.TickCount64;
            connection.Copy = CopyAsync(current, token);
            _tasks.Run(async _ => { try { await connection.Copy; } catch (OperationCanceledException) when (token.IsCancellationRequested) { } }, "Copy local controller media");
        }
    }
    private async Task CopyAsync(ExperienceOptions current, CancellationToken token) => await Task.Run(async () =>
    {
        await _copyGate.WaitAsync(token);
        try
        {
            var source = mediaDirectory?.Invoke() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
            LocalMediaPath.Ensure(Path.GetFullPath(source));
            var destinations = new[] { current.VideoDirectory, current.AudioDirectory }.Select(d => MediaPath.ResolveDirectory(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var complete = true;
            foreach (var destination in destinations)
            {
                if (Path.GetFullPath(source).Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                complete &= await _cache.SynchronizeAsync(Environment.MachineName, source, destination, token, acceptManagedOwnershipTransfer: true);
            }
            Telemetry.Info("LocalControllerMediaSyncCompleted", new { Source = source, Destinations = destinations, Complete = complete });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Telemetry.Warning("LocalControllerMediaSyncFailed", new { Reason = ex.Message, RetainedPrevious = true }); }
        finally { _copyGate.Release(); }
    }, token);
    public async ValueTask DisposeAsync()
    {
        _round?.Cancel(); await _tasks.DisposeAsync(); _http.Dispose();
        foreach (var round in _rounds) round.Dispose();
    }
}
