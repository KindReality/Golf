using ExperienceX;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;

internal static class UdpListenerChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var received = new List<ExperienceRequest>();
                    var events = new List<PhotoBooth.Networking.BroadcastEnvelope>();
                    await using var listener = new UdpExperienceListener(dispatcher, () => new ExperienceOptions(), received.Add, events.Add);
                    listener.Bind(0);
                    var port = listener.Port;
                    using var first = new UdpClient();
                    using var retry = new UdpClient();
                    async Task Send(UdpClient sender, string id, int targetPort)
                    {
                        var bytes = Encoding.UTF8.GetBytes($"{{\"device\":\"*\",\"source\":\"tests\",\"experience\":\"video\",\"value\":\"ping.mp4\",\"requestId\":\"{id}\"}}");
                        await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, targetPort));
                    }
                    await Send(first, "one", port);
                    await Until(() => received.Count == 1);
                    var announcement = Encoding.UTF8.GetBytes("{\"type\":\"event\",\"device\":\"*\",\"source\":\"sensor\",\"requestId\":\"event-one\",\"event\":\"proximity.enter\",\"experience\":\"video\",\"value\":\"ping.mp4\"}");
                    await first.SendAsync(announcement, new IPEndPoint(IPAddress.Loopback, port));
                    await Until(() => events.Count == 1);
                    if (received.Count != 1 || events[0].Event != "proximity.enter")
                        throw new Exception("An event was incorrectly executed as a playback command.");
                    await Send(retry, "one", port);
                    await Task.Delay(150);
                    if (received.Count != 1)
                        throw new Exception("A retry from another sender port restarted the request.");
                    using (var noReply = new CancellationTokenSource(150))
                    {
                        try
                        {
                            await first.ReceiveAsync(noReply.Token);
                            throw new Exception("UDP listener sent a reply.");
                        }
                        catch (OperationCanceledException) when (noReply.IsCancellationRequested) { }
                    }
                    using var occupied = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    var occupiedPort = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
                    try
                    {
                        listener.Bind(occupiedPort);
                        throw new Exception("Binding to an occupied port succeeded.");
                    }
                    catch (SocketException) { }
                    if (listener.Port != port)
                        throw new Exception("Failed binding released the working listener.");
                    await Send(first, "two", port);
                    await Until(() => received.Count == 2);
                    listener.Bind(0);
                    await Send(first, "three", listener.Port);
                    await Until(() => received.Count == 3);
                    await listener.DisposeAsync();
                    try
                    {
                        listener.Bind(0);
                        throw new Exception("Stopped listener accepted a new binding.");
                    }
                    catch (ObjectDisposedException) { }
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Passed owned UDP reception, sender-port-independent retries, no replies, transactional rebinding and receive-loop shutdown.");
    }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("UDP request was not dispatched.");
            await Task.Delay(20);
        }
    }
}
