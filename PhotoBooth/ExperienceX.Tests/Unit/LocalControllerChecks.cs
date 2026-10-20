using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;

internal static class LocalControllerChecks
{
    private sealed class Transport : HttpMessageHandler
    {
        public byte[] Bytes = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly List<Uri> Requests = [];
        public int Unchanged;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            var revision = ControllerSharedDefinitions.Revision(Bytes);
            if (Status == HttpStatusCode.OK && request.Headers.IfNoneMatch.Any(e => e.Tag == "\"" + revision + "\""))
            { Unchanged++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Bytes) });
        }
    }
    public static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "LocalControllerChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    var source = Path.Combine(root, "controller"); Directory.CreateDirectory(Path.Combine(source, "Ghosts"));
                    await File.WriteAllTextAsync(Path.Combine(source, "Ghosts", "Eye.mp4"), "first");
                    var local = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "local-alias", Hostname = Environment.MachineName, Perspectives = [new() { Name = "local" }] };
                    var remote = local with { ControllerId = Guid.NewGuid().ToString("N"), Hostname = "peer-one", ControllerName = "remote", Perspectives = [new() { Name = "remote" }] };
                    var catalog = new ControllerCatalog { LocalControllerId = local.ControllerId, Controllers = [local, remote] };
                    var transport = new Transport { Bytes = catalog.Serialize() };
                    var options = new ExperienceOptions { VideoDirectory = Path.Combine(root, "videos"), AudioDirectory = Path.Combine(root, "audio"), LocalController = new() { Port = 55444, MediaSyncIntervalSeconds = 1 } };
                    var client = new ControllerDefinitionsCache(_ => Task.CompletedTask);
                    await using (var connection = new LocalControllerConnection(dispatcher, client.ApplyAsync, transport, () => source))
                    {
                        await connection.PollAsync(options, default);
                        var video = Path.Combine(options.VideoDirectory, "Ghosts", "Eye.mp4"); var audio = Path.Combine(options.AudioDirectory, "Ghosts", "Eye.mp4");
                        await Until(() => File.Exists(video) && File.Exists(audio));
                        Check(transport.Requests.All(u => IPAddress.IsLoopback(IPAddress.Parse(u.Host)) && u.Port == 55444 && u.AbsolutePath == "/catalog"), "Experience fetches only loopback at its configured local Controller port");
                        Check(client.Perspectives.Count == 2 && client.LocalController?.ControllerId == local.ControllerId, "Remote perspectives arrive only through the local Controller's combined catalog");
                        Check(File.ReadAllText(video) == "first", "Experience copies its local Controller library into local playback folders");
                        await connection.PollAsync(options, default);
                        Check(transport.Unchanged == 1, "Unchanged local catalogs use conditional HTTP without retransferring definitions");
                        await File.WriteAllTextAsync(Path.Combine(source, "Ghosts", "Eye.mp4"), "updated");
                        await Task.Delay(1100); await connection.PollAsync(options, default);
                        await Until(() => File.ReadAllText(video) == "updated");
                        Check(true, "New local Controller media is synchronized without restarting Experience");
                        transport.Bytes = (catalog with { Controllers = [local with { Hostname = "foreign-computer" }, remote] }).Serialize();
                        try { await connection.PollAsync(options, default); throw new Exception("Foreign local identity accepted."); } catch (IOException) { }
                        Check(client.LocalController?.Hostname == Environment.MachineName, "A catalog identifying a remote computer as local is rejected without losing cached state");
                        transport.Status = HttpStatusCode.ServiceUnavailable;
                        try { await connection.PollAsync(options, default); throw new Exception("Failed Controller accepted."); } catch (HttpRequestException) { }
                        Check(File.ReadAllText(video) == "updated" && client.Perspectives.Count == 2, "Controller outages retain locally cached media and perspectives");
                        transport.Bytes = catalog.Serialize(); transport.Status = HttpStatusCode.OK;
                        connection.Configure(options); await Until(() => transport.Requests.Count >= 7);
                        connection.Configure(options with { LocalController = new() { Enabled = false } });
                        var count = transport.Requests.Count; await Task.Delay(1200);
                        Check(transport.Requests.Count == count, "Disabling the local connection stops retries immediately");
                    }
                    await PerspectiveChecks.Run();
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Directory.Delete(root, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 5000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Local Controller state did not arrive."); await Task.Delay(10); } }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
