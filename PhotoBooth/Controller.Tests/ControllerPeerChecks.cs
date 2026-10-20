using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Networking;
using PhotoBooth.Networking;

internal static class ControllerPeerChecks
{
    private sealed class Transport : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, byte[]> Responses = new();
        public readonly ConcurrentQueue<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Responses[request.RequestUri!.Host]) });
        }
    }
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ControllerPeers-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable); Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.2");
        try
        {
            var localMedia = Path.Combine(root, "local"); var remoteMedia = Path.Combine(root, "remote"); Directory.CreateDirectory(localMedia); Directory.CreateDirectory(Path.Combine(remoteMedia, "Ghosts"));
            await File.WriteAllTextAsync(Path.Combine(localMedia, "local-only.mp4"), "retain");
            await File.WriteAllTextAsync(Path.Combine(localMedia, "conflict.mp4"), "local-original");
            await File.WriteAllTextAsync(Path.Combine(remoteMedia, "conflict.mp4"), "remote-original");
            await File.WriteAllTextAsync(Path.Combine(remoteMedia, "Ghosts", "Eye.mp4"), "remote-video");
            var settings = new ControllerOptions { DeviceName = "local-alias", ControllerId = Guid.NewGuid().ToString("N"), DefinitionsPort = FreeTcp(), PeerDiscovery = new() { MediaSyncIntervalSeconds = 1 } };
            var definition = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "peer-alias", Hostname = "peer-one", Perspectives = [new() { Name = "bottom-third", Rectangle = new() { Y = 66, Height = 34 } }] };
            var transport = new Transport(); transport.Responses["127.0.0.3"] = definition.Serialize();
            var sent = new ConcurrentQueue<BroadcastEnvelope>(); var prepared = new ConcurrentQueue<string>();
            ControllerAnnouncements? announcements = null;
            await using var peers = new ControllerPeers(() => settings, new ActivityLog(), () => announcements!.PublishCatalog(), transport, _ => remoteMedia, localMedia,
                (host, _) => { prepared.Enqueue(host); return Task.CompletedTask; }, (bytes, _, _) => { sent.Enqueue(BroadcastEnvelope.Parse(bytes)); return Task.CompletedTask; });
            await using var server = announcements = new(() => settings, new ActivityLog(), (_, _, _) => Task.CompletedTask, () => peers.Definitions);
            server.Start(); peers.Start();
            await Until(() => sent.Count > 0);
            Check(ControllerDiscovery.IsRequest(sent.First()) && sent.First().Device == "*", "Controllers broadcast discovery requests to other Controllers");
            BroadcastEnvelope Announce(ControllerSharedDefinitions d, string? revision = null) => BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new() { DeviceName = d.ControllerName }, "event", "*", new
                { @event = ControllerDiscovery.ResponseEvent, input = "controller", hostname = d.Hostname, controllerId = d.ControllerId, revision = revision ?? ControllerSharedDefinitions.Revision(d.Serialize()), definitionsPort = 54244 })) with { SenderAddress = "127.0.0.3" };
            peers.Receive(Announce(definition with { Hostname = Environment.MachineName })); await Task.Delay(30);
            Check(transport.Requests.Count == 0, "Controllers ignore their own host instead of synchronizing media with themselves");
            peers.Receive(Announce(definition)); await Until(() => peers.Definitions.Length == 1);
            await Until(() => File.Exists(Path.Combine(localMedia, "Ghosts", "Eye.mp4")));
            Check(prepared.SequenceEqual(["peer-one"]) && File.ReadAllText(Path.Combine(localMedia, "Ghosts", "Eye.mp4")) == "remote-video", "Discovered peers prepare their media credentials and populate the local Controller library");
            Check(File.ReadAllText(Path.Combine(localMedia, "conflict.mp4")) == "local-original", "Peer synchronization preserves conflicting local files");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var ownBytes = await http.GetByteArrayAsync($"http://127.0.0.2:{server.Port}/definitions");
            var own = JsonSerializer.Deserialize<ControllerSharedDefinitions>(ownBytes, ControllerSharedDefinitions.Json)!;
            var catalogBytes = await http.GetByteArrayAsync($"http://127.0.0.2:{server.Port}/catalog");
            var catalog = JsonSerializer.Deserialize<ControllerCatalog>(catalogBytes, ControllerSharedDefinitions.Json)!; catalog.Validate();
            Check(own.Perspectives.Length == 0 && catalog.Controllers.Length == 2 && catalog.LocalControllerId == settings.ControllerId, "Peer endpoint exposes only original definitions; local catalog combines local and remote Controllers without relay loops");
            peers.Receive(Announce(definition)); await Task.Delay(30);
            Check(transport.Requests.Count == 1, "Unchanged announcements do not redownload peer definitions");
            definition = definition with { Perspectives = [definition.Perspectives[0] with { Name = "updated" }] }; transport.Responses["127.0.0.3"] = definition.Serialize();
            peers.Receive(Announce(definition)); await Until(() => peers.Definitions.Single().Perspectives[0].Name == "updated");
            Check(transport.Requests.All(u => u.AbsolutePath == "/definitions"), "Controller peers fetch bounded original definitions rather than other peers' combined catalogs");
            await File.WriteAllTextAsync(Path.Combine(remoteMedia, "Ghosts", "Eye.mp4"), "updated-remote-video");
            await Until(() => File.ReadAllText(Path.Combine(localMedia, "Ghosts", "Eye.mp4")) == "updated-remote-video");
            Check(prepared.Count == 1, "Periodic media synchronization receives updates and reuses credentials");
            transport.Responses["127.0.0.3"] = ownBytes; peers.Receive(Announce(definition, new string('A', 64))); await Task.Delay(100);
            Check(peers.Definitions.Single().Perspectives[0].Name == "updated", "Failed peer revisions retain the last complete definitions");
            settings = settings with { PeerDiscovery = new() { Enabled = false } }; server.PublishCatalog();
            catalog = JsonSerializer.Deserialize<ControllerCatalog>(await http.GetByteArrayAsync($"http://127.0.0.2:{server.Port}/catalog"), ControllerSharedDefinitions.Json)!;
            Check(catalog.Controllers.Length == 1 && File.Exists(Path.Combine(localMedia, "local-only.mp4")), "Disabling peer discovery removes remote catalog entries without deleting local media");
        }
        finally { Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); Directory.Delete(root, true); }
    }
    private static int FreeTcp() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 6000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Controller peer state did not arrive."); await Task.Delay(10); } }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
