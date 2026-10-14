using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;
namespace Controller.Networking;

internal sealed class ControllerAnnouncements(Func<ControllerOptions> options, ActivityLog log, Func<byte[], BroadcastOptions, CancellationToken, Task>? send = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SnapshotHttpServer _server = new("/definitions");
    private bool _started;
    private Task _announcing = Task.CompletedTask;
    private volatile Published _published = new([], "");
    private sealed record Published(byte[] Bytes, string Revision);
    public int Port => _server.Port == 0 ? options().DefinitionsPort : _server.Port;
    public void Start() { _started=true; Publish(); _signal.Wait(0); _announcing=AnnounceAsync(); }
    public void Publish()
    {
        var bytes=new ControllerSharedDefinitions { ControllerId=options().ControllerId,ControllerName=options().EffectiveDeviceName,Hostname=Environment.MachineName,Perspectives=options().Perspectives }.Serialize();
        var revision=ControllerSharedDefinitions.Revision(bytes);
        _server.Publish(bytes,revision); _published=new(bytes,revision);
        try
        {
            var changed=_server.Port!=options().DefinitionsPort;
            _server.Bind(options().DefinitionsPort);
            if(changed)log.Info("DefinitionsHttpListening",$"Shared definitions available on TCP {Port}",new{Port});
        }
        catch(System.Net.Sockets.SocketException ex) { log.Error("DefinitionsHttpUnavailable",ex,new { Port=options().DefinitionsPort,Hint="Check for a conflicting process/port; run Install-Controller.cmd to enable remote firewall access." }); }
        if(_started && _signal.CurrentCount==0)try{_signal.Release();}catch(SemaphoreFullException){}
    }
    public Task SendAsync(CancellationToken token=default)
    {
        var bytes=ControllerDiscovery.Response(options(),controllerId:options().ControllerId,revision:_published.Revision,definitionsPort:Port);
        return send is null ? UdpBroadcastSender.SendAsync(bytes,options(),token) : send(bytes,options(),token);
    }
    private async Task AnnounceAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await SendAsync(_stop.Token);
                log.Info("ControllerAnnounced","Broadcast controller identity and shared definitions revision",new { options().ControllerId,_published.Revision,Port });
                await _signal.WaitAsync(TimeSpan.FromSeconds(10),_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(Exception ex){log.Error("ControllerAnnouncementFailed",ex);try{await Task.Delay(1000,_stop.Token);}catch(OperationCanceledException){break;}}
        }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _announcing;await _server.DisposeAsync();_signal.Dispose();_stop.Dispose();}
}
