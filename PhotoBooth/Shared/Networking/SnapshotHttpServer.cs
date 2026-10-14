using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhotoBooth.Networking;

// A bounded, read-only snapshot endpoint. TCP sockets require no HTTP.sys URL reservation.
internal sealed class SnapshotHttpServer(string path = "/inventory") : IAsyncDisposable
{
    private sealed record Snapshot(byte[] Bytes, string Revision);
    private volatile Snapshot _snapshot = new([], "");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(8);
    private readonly object _gate = new();
    private readonly List<Task> _work = [];
    private TcpListener? _listener;
    private bool _stopping;
    public int Port { get; private set; }
    public void Publish(byte[] bytes, string revision) => _snapshot = new(bytes, revision);
    public void Bind(int port)
    {
        if (_listener is not null && Port == port) return;
        var listener = new TcpListener(IPAddress.Any, port); listener.Start(16);
        _listener?.Stop(); _listener = listener; Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Track(AcceptAsync(listener));
    }
    private void Track(Task task) { lock (_gate) { _work.RemoveAll(t => t.IsCompleted); _work.Add(task); } }
    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested && ReferenceEquals(listener, _listener))
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            if (_stop.IsCancellationRequested || !_connections.Wait(0)) { client.Dispose(); continue; }
            Track(ServeAsync(client));
        }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream(); var header = new byte[8192]; int used = 0, end = -1;
                while (used < header.Length && end < 0)
                {
                    var count = await stream.ReadAsync(header.AsMemory(used), timeout.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    used += count;
                    for (int i = Math.Max(0, used - count - 3); i + 3 < used; i++)
                        if (header[i] == 13 && header[i+1] == 10 && header[i+2] == 13 && header[i+3] == 10) { end = i; break; }
                }
                if (end < 0) return;
                var lines = Encoding.ASCII.GetString(header, 0, end).Split("\r\n");
                var fields = lines[0].Split(' '); var snapshot = _snapshot;
                int status = fields.Length == 3 && fields[0] == "GET" && fields[1] == path ? 200 : 404;
                if (status == 200 && lines.Skip(1).Any(line => line.Equals("If-None-Match: \"" + snapshot.Revision + "\"", StringComparison.OrdinalIgnoreCase))) status = 304;
                var body = status == 200 ? snapshot.Bytes : [];
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 304 ? "Not Modified" : "Not Found")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nETag: \"{snapshot.Revision}\"\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                if (body.Length > 0) await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { _connections.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_stopping) return; _stopping = true; _stop.Cancel(); _listener?.Stop();
        // The accept loop may register its last connection while shutdown starts.
        while (true) { Task[] work; lock (_gate) work = _work.Where(t => !t.IsCompleted).ToArray(); if (work.Length == 0) break; await Task.WhenAll(work).ConfigureAwait(false); }
        _connections.Dispose(); _stop.Dispose();
    }
}
