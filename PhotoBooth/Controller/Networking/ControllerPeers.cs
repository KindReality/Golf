using System.Net;
using System.Net.Http;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Media;
using PhotoBooth.Networking;

namespace Controller.Networking;

// Only original definitions are fetched from peers; combined catalogs are never relayed recursively.
internal sealed class ControllerPeers(Func<ControllerOptions> options, ActivityLog log, Action changed,
    HttpMessageHandler? transport = null, Func<string, string>? sharePath = null, string? mediaDirectory = null,
    Func<string, CancellationToken, Task>? prepareShare = null,
    Func<byte[], ControllerOptions, CancellationToken, Task>? send = null) : IAsyncDisposable
{
    private sealed class Peer
    {
        public required string Id;
        public string Host = "", Revision = "", Address = "";
        public int Port;
        public long Seen, LastSync;
        public ControllerSharedDefinitions? Definitions;
        public string LoadedRevision = "";
        public string MediaRevision = "", LoadedMediaRevision = "", InventoryRevision = "";
        public MediaInventory? MediaInventory;
        public Task Fetch = Task.CompletedTask, Copy = Task.CompletedTask;
        public string? MediaFailure;
        public bool CredentialsReady;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new(transport ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly SemaphoreSlim _downloads = new(4), _copies = new(1);
    private readonly MediaCache _media = new();
    private Task _loop = Task.CompletedTask;
    private string _catalogSignature = "";
    public ControllerSharedDefinitions[] Definitions
    {
        get { lock (_gate) return !options().PeerDiscovery.Enabled ? [] : _peers.Values.Where(p => p.Definitions is not null && Environment.TickCount64 - p.Seen < 30000)
            .OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Definitions!).ToArray(); }
    }
    public void Start() => _loop = Task.Run(RunAsync);
    public void Receive(BroadcastEnvelope message)
    {
        if (!options().PeerDiscovery.Enabled || message.Event != ControllerDiscovery.ResponseEvent) return;
        try
        {
            var announcement = ControllerDiscovery.ReadAnnouncement(message);
            if (announcement is null || announcement.ControllerId == options().ControllerId || announcement.Hostname.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                if (!_peers.TryGetValue(announcement.ControllerId, out var peer))
                {
                    if (_peers.Count >= 127) throw new ArgumentException("Too many peer Controllers.");
                    _peers.Add(announcement.ControllerId, peer = new() { Id = announcement.ControllerId });
                }
                var address = message.SenderAddress ?? announcement.Hostname;
                if (peer.Host != announcement.Hostname || peer.Address != address || peer.Port != announcement.DefinitionsPort)
                { peer.LoadedMediaRevision = ""; peer.InventoryRevision = ""; peer.MediaInventory = null; peer.CredentialsReady = false; }
                peer.Host = announcement.Hostname; peer.Revision = announcement.Revision; peer.Port = announcement.DefinitionsPort;
                peer.Address = address; peer.Seen = Environment.TickCount64; peer.MediaRevision = announcement.MediaRevision;
                if (peer.LoadedRevision != peer.Revision && peer.Fetch.IsCompleted)
                    peer.Fetch = FetchAsync(peer, announcement, peer.Address);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
        { log.Error("ControllerPeerAnnouncementInvalid", ex, new { message.Source }); }
    }
    private async Task FetchAsync(Peer peer, ControllerDiscovery.Announcement announcement, string address)
    {
        await Task.Yield();
        try
        {
            await _downloads.WaitAsync(_stop.Token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await _http.GetAsync(new UriBuilder("http", address, announcement.DefinitionsPort, "/definitions").Uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var bytes = await SnapshotDownload.ReadAsync(response, ControllerSharedDefinitions.MaximumBytes, timeout.Token);
                if (ControllerSharedDefinitions.Revision(bytes) != announcement.Revision) throw new IOException("Peer definitions changed during download; waiting for its next announcement.");
                var definitions = JsonSerializer.Deserialize<ControllerSharedDefinitions>(bytes, ControllerSharedDefinitions.Json) ?? throw new IOException("Empty peer definitions."); definitions.Validate();
                if (definitions.ControllerId != announcement.ControllerId || !definitions.Hostname.Equals(announcement.Hostname, StringComparison.OrdinalIgnoreCase)) throw new IOException("Peer controller identity mismatch.");
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested || peer.Revision != announcement.Revision || peer.Host != announcement.Hostname || peer.Address != address) return;
                    peer.Definitions = definitions; peer.LoadedRevision = announcement.Revision;
                }
                changed();
                log.Info("ControllerPeerDefinitionsUpdated", $"{definitions.ControllerName}: {definitions.Perspectives.Length} shared perspectives", new { definitions.Hostname, definitions.ControllerId, announcement.Revision });
            }
            finally { _downloads.Release(); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Error("ControllerPeerDefinitionsFailed", ex, new { announcement.Hostname, RetainedPrevious = true }); }
    }
    private async Task RunAsync()
    {
        var lastDiscovery = 0L;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var settings = options();
                if (settings.PeerDiscovery.Enabled && (lastDiscovery == 0 || Environment.TickCount64 - lastDiscovery >= 10000))
                {
                    lastDiscovery = Environment.TickCount64;
                    try
                    {
                        var request = ControllerDiscovery.Request(settings, Guid.NewGuid().ToString("N"));
                        if (send is null) await UdpBroadcastSender.SendAsync(request, settings, _stop.Token);
                        else await send(request, settings, _stop.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.Error("ControllerPeerDiscoveryFailed", ex); }
                }
                lock (_gate)
                {
                    foreach (var peer in _peers.Values.Where(p => settings.PeerDiscovery.Enabled && p.Definitions is not null && Environment.TickCount64 - p.Seen < 30000 &&
                        p.LoadedMediaRevision != p.MediaRevision &&
                        p.Copy.IsCompleted && (p.LastSync == 0 || Environment.TickCount64 - p.LastSync >= settings.PeerDiscovery.MediaSyncIntervalSeconds * 1000)))
                    { peer.LastSync = Environment.TickCount64; var host = peer.Host; var address = peer.Address; var port = peer.Port; var revision = peer.MediaRevision; peer.Copy = Task.Run(() => CopyAsync(peer, host, address, port, revision)); }
                }
                string signature;
                lock (_gate) signature = !settings.PeerDiscovery.Enabled ? "" : string.Join('|', _peers.Values.Where(p => p.Definitions is not null && Environment.TickCount64 - p.Seen < 30000).OrderBy(p => p.Id).Select(p => p.Id + p.LoadedRevision));
                if (signature != _catalogSignature) { _catalogSignature = signature; changed(); }
                await Task.Delay(1000, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task CopyAsync(Peer peer, string hostname, string address, int port, string revision)
    {
        try
        {
            await _copies.WaitAsync(_stop.Token);
            try
            {
                if (!options().PeerDiscovery.Enabled) return;
                bool Current() => peer.Host == hostname && peer.Address == address && peer.Port == port && peer.MediaRevision == revision;
                if (Environment.TickCount64 - peer.Seen >= 30000 || !Current()) return;
                var inventory = peer.MediaInventory;
                if (inventory is null || peer.InventoryRevision != revision)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var response = await _http.GetAsync(new UriBuilder("http", address, port, "/media").Uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    var bytes = await SnapshotDownload.ReadAsync(response, MediaInventory.MaximumBytes, timeout.Token);
                    if (MediaInventory.Revision(bytes) != revision) throw new IOException("Peer media inventory changed; waiting for its next announcement.");
                    inventory = MediaInventory.Parse(bytes);
                    lock (_gate)
                    {
                        if (!Current()) return;
                        peer.MediaInventory = inventory; peer.InventoryRevision = revision;
                    }
                }
                if (!peer.CredentialsReady)
                {
                    await (prepareShare?.Invoke(hostname, _stop.Token) ?? MediaShareAccess.PrepareAsync(hostname, _stop.Token));
                    lock (_gate)
                    {
                        if (!Current()) return;
                        peer.CredentialsReady = true;
                    }
                }
                var destination = mediaDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
                var complete = await _media.SynchronizeAsync(hostname, sharePath?.Invoke(hostname) ?? ControllerDiscovery.MediaShare(hostname), destination, _stop.Token, inventory);
                if (!complete) throw new IOException("Some peer media files could not be copied; they will be retried.");
                lock (_gate)
                {
                    if (!Current()) return;
                    peer.LoadedMediaRevision = revision; peer.MediaFailure = null;
                }
                log.Info("ControllerPeerMediaSynchronized", $"Media checked from {hostname}", new { Hostname = hostname, Destination = destination, Revision = revision });
            }
            finally { _copies.Release(); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (peer.MediaFailure != ex.Message) log.Error("ControllerPeerMediaSyncFailed", ex, new { Hostname = hostname, RetainedPrevious = true });
            peer.MediaFailure = ex.Message;
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _loop;
        Task[] work; lock (_gate) work = _peers.Values.SelectMany(p => new[] { p.Fetch, p.Copy }).ToArray();
        try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { log.Warning("ControllerPeerShutdownTimedOut", "Peer media synchronization is still waiting for Windows file I/O"); }
        _http.Dispose();
    }
}
