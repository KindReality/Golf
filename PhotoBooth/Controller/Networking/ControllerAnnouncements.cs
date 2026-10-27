using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;
using PhotoBooth.Media;
namespace Controller.Networking;

internal sealed class ControllerAnnouncements(Func<ControllerOptions> options, ActivityLog log, Func<byte[], BroadcastOptions, CancellationToken, Task>? send = null,
    Func<ControllerSharedDefinitions[]>? peers = null, Func<string>? mediaDirectory = null, Func<IEnumerable<string>>? assignedRoles = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SnapshotHttpServer _server = new("/definitions");
    private bool _started;
    private Task _announcing = Task.CompletedTask;
    private volatile Published _published = new([], "");
    private volatile Published _media = new(MediaInventory.Empty.Serialize(), MediaInventory.EmptyRevision);
    private bool _checkedStartupMedia;
    private sealed record Published(byte[] Bytes, string Revision);
    private ControllerSharedDefinitions OwnDefinitions()
    {
        var current = options();
        return new() { ControllerId=current.ControllerId,ControllerName=current.EffectiveDeviceName,Hostname=Environment.MachineName,Perspectives=current.Perspectives,
            Roles=RoleCatalog.Names(PlaylistRoles.Names(current.Playlists).Concat(assignedRoles?.Invoke() ?? [])) };
    }
    public int Port => _server.Port == 0 ? options().DefinitionsPort : _server.Port;
    public void Start() { _started=true; Publish(); _server.Publish(_media.Bytes, _media.Revision, "/media"); _signal.Wait(0); _announcing=Task.Run(AnnounceAsync); }
    internal void RefreshMedia()
    {
        var root = mediaDirectory?.Invoke() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
        var inventory = MediaInventory.Read(root);
        var bytes = inventory.Serialize();
        var revision = MediaInventory.Revision(bytes);
        _server.Publish(bytes, revision, "/media");
        var previous = _media;
        _media = new(bytes, revision);
        if (!_checkedStartupMedia)
        {
            _checkedStartupMedia = true;
            PlaylistMediaValidation.WarnMissing(options(), inventory, log);
        }
        if (previous.Revision != revision)
            log.Info("ControllerMediaInventoryUpdated", $"Media library: {inventory.Files.Length} files", new { Revision = revision, Files = inventory.Files.Length, Root = root });
    }
    public void Publish()
    {
        RefreshDefinitions();
        PublishCatalog();
        try
        {
            var changed=_server.Port!=options().DefinitionsPort;
            _server.Bind(options().DefinitionsPort);
            if(changed)log.Info("DefinitionsHttpListening",$"Shared definitions available on TCP {Port}",new{Port});
        }
        catch(System.Net.Sockets.SocketException ex) { log.Error("DefinitionsHttpUnavailable",ex,new { Port=options().DefinitionsPort,Hint="Check for a conflicting process/port; run Install-Controller.cmd to enable remote firewall access." }); }
        if(_started && _signal.CurrentCount==0)try{_signal.Release();}catch(SemaphoreFullException){}
    }
    private void RefreshDefinitions()
    {
        var bytes = OwnDefinitions().Serialize(); var revision = ControllerSharedDefinitions.Revision(bytes);
        if (_published.Revision == revision) return;
        _server.Publish(bytes, revision); _published = new(bytes, revision);
        PublishCatalog();
    }
    public void PublishCatalog()
    {
        try
        {
            var own = OwnDefinitions();
            var catalog = new ControllerCatalog { LocalControllerId = own.ControllerId, Controllers = new[] { own }.Concat(peers?.Invoke() ?? []).Where(c => c.ControllerId == own.ControllerId || !c.Hostname.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)).DistinctBy(c => c.ControllerId).ToArray() };
            var bytes = catalog.Serialize(); _server.Publish(bytes, ControllerSharedDefinitions.Revision(bytes), "/catalog");
        }
        catch (ArgumentException ex) { log.Error("ControllerCatalogPublishFailed", ex, new { RetainedPrevious = true }); }
    }
    public Task SendAsync(CancellationToken token=default)
    {
        RefreshDefinitions();
        var bytes=ControllerDiscovery.Response(options(),controllerId:options().ControllerId,revision:_published.Revision,definitionsPort:Port,mediaRevision:_media.Revision);
        return send is null ? UdpBroadcastSender.SendAsync(bytes,options(),token) : send(bytes,options(),token);
    }
    private async Task AnnounceAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                try { RefreshMedia(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { log.Warning("ControllerMediaInventoryFailed", "Media inventory unavailable; retaining the previous revision", new { Reason = ex.Message }); }
                await SendAsync(_stop.Token);
                log.Info("ControllerAnnounced","Broadcast controller identity, definitions and media revisions",new { options().ControllerId,_published.Revision,MediaRevision=_media.Revision,Port });
                await _signal.WaitAsync(TimeSpan.FromSeconds(10),_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(Exception ex){log.Error("ControllerAnnouncementFailed",ex);try{await Task.Delay(1000,_stop.Token);}catch(OperationCanceledException){break;}}
        }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _announcing;await _server.DisposeAsync();_signal.Dispose();_stop.Dispose();}
}
