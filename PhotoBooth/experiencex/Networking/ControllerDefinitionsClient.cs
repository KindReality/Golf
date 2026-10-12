using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace ExperienceX;

internal sealed class ControllerDefinitionsClient(Dispatcher dispatcher, Func<ControllerSharedDefinitions, Task> apply, HttpMessageHandler? transport = null) : IAsyncDisposable
{
    private sealed record Announcement(string Host, int Port, string Revision, string Address);
    private readonly Dictionary<string, Announcement> _latest = new();
    private readonly Dictionary<string, (string Revision, ControllerSharedDefinitions Definitions)> _loaded = new();
    private readonly HashSet<string> _active = [];
    private readonly AsyncTaskScope _tasks = new("Controller definitions");
    private readonly HttpClient _http = new(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
    public IReadOnlyList<PerspectiveSelection> Perspectives => _loaded.Values.SelectMany(c => c.Definitions.Perspectives.Select(c.Definitions.Select))
        .OrderBy(p => p.ControllerName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public void Seed(IEnumerable<ControllerSharedDefinitions> definitions)
    {
        foreach (var d in definitions)
        {
            d.Validate();
            _loaded[d.ControllerId] = (ControllerSharedDefinitions.Revision(d.Serialize()), d);
        }
    }
    public void Receive(BroadcastEnvelope message)
    {
        if (message.Event != ControllerDiscovery.ResponseEvent)
            return;
        try
        {
            var body = message.Body;
            var id = body.GetProperty("controllerId").GetString()!;
            var revision = body.GetProperty("revision").GetString()!;
            var host = body.GetProperty("hostname").GetString()!;
            var port = body.GetProperty("definitionsPort").GetInt32();
            if (!Guid.TryParse(id, out _) || revision is null || revision.Length != 64 || !revision.All(Uri.IsHexDigit) || port is < 1 or > 65535)
                throw new ArgumentException("Invalid controller announcement identity, revision or HTTP port.");
            ControllerDiscovery.MediaShare(host);
            _latest[id] = new(host, port, revision, message.SenderAddress ?? host);
            if (_loaded.TryGetValue(id, out var loaded) && loaded.Revision == revision || !_active.Add(id))
                return;
            var announcement = _latest[id];
            _tasks.Run(token => FetchAsync(id, announcement, token), "Fetch shared definitions");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Telemetry.Warning("ControllerAnnouncementInvalid", new
            {
                message.Source,
                Reason = ex.Message
            });
        }
    }
    private async Task FetchAsync(string id, Announcement announcement, CancellationToken token)
    {
        try
        {
            var uri = new UriBuilder("http", announcement.Address, announcement.Port, "/definitions").Uri;
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ControllerSharedDefinitions.MaximumBytes)
                throw new IOException("Definitions exceed 4 MB.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + read > ControllerSharedDefinitions.MaximumBytes)
                    throw new IOException("Definitions exceed 4 MB.");
                memory.Write(buffer, 0, read);
            }
            var bytes = memory.ToArray();
            if (ControllerSharedDefinitions.Revision(bytes) != announcement.Revision)
                throw new IOException("Definitions changed during download; waiting for the next announcement.");
            var definitions = JsonSerializer.Deserialize<ControllerSharedDefinitions>(bytes, ControllerSharedDefinitions.Json)!;
            definitions.Validate();
            if (definitions.ControllerId != id || !definitions.Hostname.Equals(announcement.Host, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Controller identity does not match its announcement.");
            await dispatcher.InvokeAsync(async () =>
            {
                if (_latest[id] != announcement || token.IsCancellationRequested)
                    return;
                await apply(definitions);
                _loaded[id] = (announcement.Revision, definitions);
                Telemetry.Info("ControllerDefinitionsApplied", new
                {
                    definitions.ControllerId,
                    definitions.ControllerName,
                    announcement.Revision,
                    Count = definitions.Perspectives.Length
                });
            }).Task.Unwrap();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Telemetry.Warning("ControllerDefinitionsDownloadFailed", new { ControllerId = id, announcement.Host, Reason = ex.Message, RetainedPrevious = true }); }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
                await dispatcher.InvokeAsync(() =>
            {
                _active.Remove(id);
                if (!token.IsCancellationRequested && _latest[id] != announcement && _active.Add(id))
                {
                    var latest = _latest[id];
                    _tasks.Run(t => FetchAsync(id, latest, t), "Fetch newer shared definitions");
                }
            });
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _tasks.DisposeAsync();
        _http.Dispose();
    }
}
