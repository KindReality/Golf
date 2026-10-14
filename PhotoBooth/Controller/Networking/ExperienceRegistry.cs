using System.Net.Http;
using System.Net;
using System.Text.Json;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Networking;

internal sealed record ExperiencePeer(ExperienceInventory Inventory, bool Online, bool Ambiguous)
{
    public bool Usable => Online && !Ambiguous;
}
internal sealed class ExperienceRegistry(ActivityLog log) : IAsyncDisposable
{
    private sealed class Entry
    {
        public ExperienceInventory? Inventory;
        public string Instance = "", Revision = "", LoadedRevision = "", Address = "";
        public int Port;
        public long Seen;
        public Task Work = Task.CompletedTask;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _stop = new();
    public ExperiencePeer[] Snapshot()
    {
        lock (_gate)
        {
            var peers = _entries.Values.Where(e => e.Inventory is not null).Select(e => new ExperiencePeer(e.Inventory!, Environment.TickCount64 - e.Seen < 30000 && e.Inventory!.InstanceId == e.Instance && e.LoadedRevision == e.Revision, false)).ToArray();
            return peers.Select(p => p with { Ambiguous = peers.Count(other => other.Online && other.Inventory.DeviceName.Equals(p.Inventory.DeviceName, StringComparison.OrdinalIgnoreCase)) > 1 }).OrderBy(p => p.Inventory.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
    public void Receive(BroadcastEnvelope message)
    {
        try
        {
            var announcement = ExperienceDiscovery.Read(message);
            if (announcement is not { } a || !IPAddress.TryParse(message.SenderAddress, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                if (!_entries.TryGetValue(a.Hostname, out var e))
                {
                    if (_entries.Count >= 128) throw new ArgumentException("Too many Experience devices.");
                    _entries.Add(a.Hostname, e = new());
                }
                e.Seen = Environment.TickCount64; e.Instance = a.InstanceId; e.Revision = a.Revision; e.Address = address.ToString(); e.Port = a.Port;
                if (e.Inventory is not null && e.Inventory.InstanceId == a.InstanceId && e.LoadedRevision == a.Revision || !e.Work.IsCompleted) return;
                e.Work = FetchAsync(e, a.Hostname, message.Source, a.InstanceId, a.Revision, e.Address, e.Port);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { log.Error("ExperienceAnnouncementInvalid", ex); }
    }
    private async Task FetchAsync(Entry entry, string hostname, string device, string instance, string revision, string address, int port)
    {
        // Leave Receive's lock before starting network work.
        await Task.Yield();
        try
        {
            using var response = await _http.GetAsync($"http://{address}:{port}/inventory", HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > ExperienceInventory.MaximumBytes) throw new ArgumentException("Experience inventory is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            { if (buffer.Length + count > ExperienceInventory.MaximumBytes) throw new ArgumentException("Experience inventory is too large."); buffer.Write(chunk, 0, count); }
            var bytes = buffer.ToArray();
            if (ExperienceInventory.Revision(bytes) != revision) throw new ArgumentException("Experience inventory changed during discovery; retrying on next announcement.");
            var inventory = JsonSerializer.Deserialize<ExperienceInventory>(bytes, ExperienceInventory.Json) ?? throw new ArgumentException("Empty inventory."); inventory.Validate();
            if (inventory.InstanceId != instance || !inventory.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) || !inventory.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Experience inventory identity mismatch.");
            lock (_gate)
            { if (_stop.IsCancellationRequested || entry.Instance != instance || entry.Revision != revision || entry.Address != address || entry.Port != port) return; entry.Inventory = inventory; entry.LoadedRevision = revision; }
            log.Info("ExperienceInventoryUpdated", $"{device}: {inventory.Screens.Length} screens, {inventory.Speakers.Length} speakers", new { hostname, revision });
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Error("ExperienceInventoryFailed", ex, new { hostname, address, port, Hint = "Run Install-Experience.cmd to enable inventory TCP access." }); }
    }
    public async ValueTask DisposeAsync()
    { _stop.Cancel(); Task[] work; lock (_gate) work = _entries.Values.Select(e => e.Work).ToArray(); await Task.WhenAll(work); _http.Dispose(); _stop.Dispose(); }
}
