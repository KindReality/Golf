using System.Net;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Networking;

internal sealed class ControllerAnnouncements(Func<ControllerOptions> options, ActivityLog log, Func<byte[], BroadcastOptions, CancellationToken, Task>? send = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private HttpListener? _http;
    private readonly List<Task> _servers = [];
    private bool _started;
    private Task _announcing = Task.CompletedTask;
    private volatile Published _published = new([], "");
    private sealed record Published(byte[] Bytes, string Revision);
    public int Port { get; private set; }
    public void Start()
    {
        _started = true;
        Publish();
        _signal.Wait(0);
        _announcing = AnnounceAsync();
    }
    private void Bind()
    {
        var port = options().DefinitionsPort;
        if (Port == port && _http?.IsListening == true) return;
        var next = new HttpListener(); next.Prefixes.Add($"http://+:{port}/");
        try
        {
            next.Start(); _http?.Close(); _http = next; Port = port;
            _servers.RemoveAll(t => t.IsCompleted); _servers.Add(ServeAsync(next));
            log.Info("DefinitionsHttpListening", $"Shared definitions available on TCP {Port}", new { Port });
        }
        catch (HttpListenerException ex)
        {
            next.Close(); Port = port;
            log.Error("DefinitionsHttpUnavailable", ex, new { Port, Hint = "Run Install-Controller.cmd to reserve the HTTP endpoint and enable its firewall rule." });
        }
    }
    public void Publish()
    {
        var current = options();
        if (_started) Bind();
        var bytes = new ControllerSharedDefinitions
        {
            ControllerId = current.ControllerId,
            ControllerName = current.EffectiveDeviceName,
            Hostname = Environment.MachineName,
            Perspectives = current.Perspectives
        }.Serialize();
        var revision = ControllerSharedDefinitions.Revision(bytes);
        if (_published.Revision == revision) return;
        _published = new(bytes, revision);
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }
    public Task SendAsync(CancellationToken token)
    {
        var bytes = ControllerDiscovery.Response(options(), controllerId: options().ControllerId, revision: _published.Revision, definitionsPort: Port);
        return send is null ? UdpBroadcastSender.SendAsync(bytes, options(), token) : send(bytes, options(), token);
    }
    private async Task AnnounceAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await SendAsync(token);
                log.Info("ControllerAnnounced", "Broadcast controller identity and shared definitions revision", new { options().ControllerId, _published.Revision, Port });
                await _signal.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log.Error("ControllerAnnouncementFailed", ex); try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; } }
        }
    }
    private async Task ServeAsync(HttpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when ((_stop.IsCancellationRequested || !listener.IsListening) && ex is HttpListenerException or ObjectDisposedException) { break; }
            try
            {
                var snapshot = _published;
                if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/definitions") { context.Response.StatusCode = 404; continue; }
                context.Response.Headers["ETag"] = '"' + snapshot.Revision + '"';
                context.Response.Headers["Cache-Control"] = "no-cache";
                if (context.Request.Headers["If-None-Match"] == '"' + snapshot.Revision + '"') { context.Response.StatusCode = 304; continue; }
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = snapshot.Bytes.Length;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await context.Response.OutputStream.WriteAsync(snapshot.Bytes, timeout.Token);
                log.Info("SharedDefinitionsDownloaded", "Sent complete shared definitions", new { snapshot.Revision, Bytes = snapshot.Bytes.Length });
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException) { if (!_stop.IsCancellationRequested) log.Error("DefinitionsTransferFailed", ex); }
            finally { try { context.Response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { } }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _http?.Close();
        await Task.WhenAll(_servers.Append(_announcing));
        _signal.Dispose(); _stop.Dispose();
    }
}
