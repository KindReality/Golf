using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Windows;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

internal static class ManualActionChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "Controller-manual-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                var previous = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
                Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.2");
                try
                {
                    var options = new ControllerOptions
                    {
                        DeviceName = "operator", ArduinoDiscovery = new() { Enabled = false }, ExperienceSupervisionEnabled = false,
                        UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), DefinitionsPort = FreeTcp(), UdpSendAttempts = 1,
                        Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient" }, new() { Name = "active", AmbientPlaylist = "ambient" }],
                        Playlists = [new() { Name = "ambient", Type = "ambient" }, new() { Name = "cue" }],
                        Triggers = [new() { Name = "beam", SourceDevice = "test-mega", Input = "beam1", Event = "beam.broken", Playlist = "cue", CooldownMilliseconds = 0 }],
                        Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") })
                    };
                    var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    using var commands = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), options.UdpCommandsPort));
                    using var events = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), options.UdpEventsPort));
                    await using var host = new ControllerHost(path);
                    await Until(() => host.Scenes.Snapshot().Phase == "Active");
                    PlaylistCommand Video(bool loop = false) => new() { Device = "test-output", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Tests/Eye.mp4", screen = "Projector/screen1", volume = 42, mute = true, loop }) };
                    var command = Video();
                    await host.SendManualAsync(command);
                    var sent = await Packet(commands, m => ControllerOptions.Text(m.Body, "experience") == "video");
                    Check(sent.Device == "test-output" && sent.Body.GetProperty("volume").GetInt32() == 42 && sent.Context?.Playlist == "Manual", "Manual video uses the command port and preserves target, media settings and context");
                    await host.SendManualAsync(command);
                    Check((await Packet(commands, m => ControllerOptions.Text(m.Body, "experience") == "video")).RequestId != sent.RequestId, "Each manual execution has a fresh request ID");
                    var eventCommand = new PlaylistCommand { Type = "event", Device = "*", Payload = JsonSerializer.SerializeToElement(new { input = "beam1", @event = "beam.broken" }) };
                    await host.SendManualAsync(eventCommand, "test-mega");
                    var emitted = await Packet(events, m => m.Event == "beam.broken");
                    await Until(() => host.Log.Snapshot().Count(e => e.Message == "Playlist started: cue") == 1);
                    await Task.Delay(100);
                    Check(emitted.Source == "test-mega" && host.Log.Snapshot().Count(e => e.Message == "Playlist started: cue") == 1, "A simulated sensor event uses the event port and triggers locally exactly once despite UDP echo");
                    var sceneCommand = new PlaylistCommand { Device = "operator", Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = "active" }) };
                    await host.SendManualAsync(sceneCommand); await Until(() => host.Scenes.Snapshot() is { Active: "active", Phase: "Active" });
                    Check(true, "Manual scene commands take effect on this Controller");
                    await host.SendManualAsync(Video(loop: true));
                    var loop = await Packet(commands, m => m.Body.TryGetProperty("loop", out var value) && value.ValueKind == JsonValueKind.True);
                    await host.SendManualAsync(sceneCommand with { Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = "idle" }) });
                    await Until(() => host.Scenes.Snapshot() is { Active: "idle", Phase: "Active" });
                    using (var noStop = new CancellationTokenSource(100))
                    {
                        try
                        {
                            while (true)
                            {
                                var message = BroadcastEnvelope.Parse((await commands.ReceiveAsync(noStop.Token)).Buffer);
                                if (ControllerOptions.Text(message.Body, "command") == "stop") throw new Exception("Scene change sent a playback stop.");
                            }
                        }
                        catch (OperationCanceledException) when (noStop.IsCancellationRequested) { }
                    }
                    Check(loop.RequestId.Length > 0, "Scene changes leave manual looping video playing without sending a stop");
                    await host.SendManualAsync(new() { Playlist = "cue" });
                    Check(host.Log.Snapshot().Any(e => e.Message == "Manual playlist: cue"), "Local playlist actions use the existing playlist engine");

                    using var form = new Form { ClientSize = new(1920, 952), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                    using var page = new LivePage(host); form.Controls.Add(page); form.Show(); page.RefreshState();
                    T Field<T>(string label) where T : Control => Descendants(page.Manual).OfType<T>().Single(c => c.AccessibleName == label && c.Visible);
                    Button Button(string accessible) => Descendants(page).OfType<Button>().Single(b => b.AccessibleName == accessible);
                    Field<ComboBox>("Destination device").Text = "test-output"; Field<ComboBox>("Media file").Text = "Tests/Eye.mp4";
                    Field<NumericUpDown>("Volume (%)").Value = 42;
                    Field<TextBox>("Pin name").Text = "Projector video";
                    var before = host.Log.Snapshot().Count(e => e.Kind == "Command");
                    await page.Manual.SavePinAsync(); await Until(() => host.Options.PinnedActions.Length == 1); page.RefreshState();
                    var pin = host.Options.PinnedActions[0];
                    Check(host.Log.Snapshot().Count(e => e.Kind == "Command") == before, "Pinning saves an action without executing it");
                    await using (var reopened = new ConfigurationService(path, options.Build, new(), _ => Task.CompletedTask))
                        Check(PinnedActionOptions.Signature(reopened.Initial.PinnedActions.Single()) == PinnedActionOptions.Signature(pin), "Pinned command settings survive configuration reload/restart");
                    Button("Run pin Projector video").PerformClick();
                    var clicked = await Packet(commands, m => m.Context?.Item == "Projector video");
                    Check(clicked.Body.GetProperty("volume").GetInt32() == 42 && ControllerOptions.Text(clicked.Body, "screen") == "*", "One pin click immediately sends its saved settings");
                    before = host.Log.Snapshot().Count(e => e.Kind == "Command"); Button("Edit pin Projector video").PerformClick();
                    Check(Field<ComboBox>("Media file").Text == "Tests/Eye.mp4" && host.Log.Snapshot().Count(e => e.Kind == "Command") == before, "Editing loads the saved action without sending");
                    Field<NumericUpDown>("Volume (%)").Value = 57; Field<TextBox>("Pin name").Text = "Projector / video";
                    await page.Manual.SavePinAsync(); await Until(() => host.Options.PinnedActions[0].Name == "Projector / video");
                    Check(host.Options.PinnedActions[0].Id == pin.Id && host.Options.PinnedActions[0].Command.Payload.GetProperty("volume").GetInt32() == 57, "Editing a pin updates it in place and preserves its identity");
                    Descendants(page.Manual).OfType<Button>().Single(b => b.Text == "New pin").PerformClick();
                    Button("Compose event").PerformClick(); Field<TextBox>("Event source device").Text = "test-mega";
                    Field<TextBox>("Input").Text = "beam1"; Field<TextBox>("Event name").Text = "beam.broken"; Field<TextBox>("Pin name").Text = "Beam event";
                    await page.Manual.SavePinAsync(); await Until(() => host.Options.PinnedActions.Length == 2); page.RefreshState();
                    var eventPin = host.Options.PinnedActions.Single(p => p.Name == "Beam event");
                    Button("Run pin Beam event").PerformClick();
                    Check((await Packet(events, m => m.Event == "beam.broken")).Source == "test-mega", "Pinned events retain the simulated source and input");
                    page.Manual.LoadPin(eventPin);
                    await host.SavePinnedActionAsync(eventPin, eventPin with { Name = "Remote edit" }); await Until(() => host.Options.PinnedActions.Any(p => p.Name == "Remote edit"));
                    Field<TextBox>("Pin name").Text = "Local edit"; await page.Manual.SavePinAsync();
                    Check(host.Options.PinnedActions.Any(p => p.Name == "Remote edit") && Descendants(page.Manual).OfType<Label>().Single(l => l.AccessibleName == "Manual action status").Text.Contains("changed elsewhere"), "Concurrent edits retain the draft and do not overwrite a remote pin change");
                    page.Manual.LoadPin(host.Options.PinnedActions.Single(p => p.Id == eventPin.Id));
                    Descendants(page.Manual).OfType<Button>().Single(b => b.Text == "Remove pin").PerformClick(); await Until(() => host.Options.PinnedActions.Length == 1);
                    Check(true, "Removing a pin persists without removing unrelated pins");
                    var pinsBeforeShowSave = JsonSerializer.Serialize(host.Options.PinnedActions, ControllerOptions.Json);
                    await host.SetConfigurationModeAsync(true); await Until(() => host.Options.ConfigurationMode);
                    var graph = ShowConfiguration.From(host.Options); await host.SaveShowAsync(graph, graph);
                    Check(JsonSerializer.Serialize(ControllerOptions.Parse(File.ReadAllText(path), options.Build).PinnedActions, ControllerOptions.Json) == pinsBeforeShowSave,
                        "Saving scenes and playlists preserves pinned actions");
                    page.RefreshState();
                    host.Log.Info("ManualCommandSent", "Video → Tests/Eye.mp4 · Projector / screen1"); host.Log.Info("ManualEventSent", "test-mega: beam.broken · beam1");
                    page.RefreshState();
                    var feed = Descendants(page).OfType<ListBox>().Single(b => b.AccessibleName == "Live activity feed");
                    var conflict = PlaybackPriorityEvent.Create(options with { DeviceName = "experience-test" }, "operator", "blocked-request", "Eyes/Eye.mp4",
                        "display", "Projector", "screen1", CommandPriority.Low, CommandPriority.Medium, true);
                    using (var sender = new UdpClient())
                        await sender.SendAsync(conflict, new IPEndPoint(IPAddress.Parse("127.0.0.2"), options.UdpEventsPort));
                    await Until(() => host.Log.Snapshot().Any(e => e.Message == "Deferred Eye.mp4 on experience-test/Projector/screen1 (low < medium)"));
                    page.RefreshState();
                    Descendants(page).OfType<Button>().Single(b => b.Text == "Events").PerformClick();
                    Check(feed.Items.Count > 0 && feed.Items.Cast<ActivityEntry>().All(e => e.Kind == "Event"), "Activity event filter shows only events");
                    Check(feed.Items.Cast<ActivityEntry>().Count(e => e.Message.Contains("Eye.mp4 on experience-test/Projector/screen1")) == 1 &&
                        !host.Log.Snapshot().Any(e => e.Message == "experience-test: playback.priorityRejected · playback"),
                        "A received priority-conflict event appears once with media, output and priorities in the Events feed");
                    Descendants(page).OfType<Button>().Single(b => b.Text == "Commands").PerformClick();
                    Check(feed.Items.Count > 0 && feed.Items.Cast<ActivityEntry>().All(e => e.Kind == "Command"), "Activity command filter shows only commands");
                    Button("Clear live activity").PerformClick();
                    Check(host.Log.Snapshot().Length == 0 && host.Options.PinnedActions.Length == 1, "Clear empties the shared activity feed without deleting pins or configuration");
                    await host.SavePinnedActionAsync(null, eventPin); await Until(() => host.Options.PinnedActions.Length == 2);
                    page.Manual.LoadPin(host.Options.PinnedActions.Single(p => p.Id == pin.Id)); page.RefreshState();
                    Descendants(page).OfType<Button>().Single(b => b.Text == "All").PerformClick();
                    host.Log.Info("NetworkEventReceived", "test-mega: beam.broken · beam1");
                    host.Log.Info("ManualCommandSent", "Video → Tests/Eye.mp4 · Projector / screen1"); page.RefreshState();
                    var preview = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/controller-live-consolidated.png"));
                    using (var bitmap = new Bitmap(page.Width, page.Height)) { page.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(preview); }
                    Console.WriteLine("Live page preview: " + preview);
                    form.ClientSize = new(1180, 700); page.PerformLayout(); page.RefreshState();
                    Check(!Descendants(page.Manual).OfType<Panel>().Any(p => p.AutoScroll && p.VerticalScroll.Visible), "Composer fits with saved pins on a compact display without scrolling");
                    using (var bitmap = new Bitmap(page.Width, page.Height)) { page.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.ChangeExtension(preview, ".compact.png")); }
                    var pinFile = File.ReadAllText(path);
                    try { await host.SavePinnedActionAsync(null, eventPin with { Id = Guid.NewGuid().ToString("N"), Name = "Projector / video" }); throw new Exception("Duplicate pin name was accepted."); }
                    catch (ArgumentException) { Check(File.ReadAllText(path) == pinFile, "Invalid pins never overwrite configuration"); }
                    Check(!form.OwnedForms.Any(), "Manual sending, pin editing and activity stay in one window");
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Telemetry.Stop(); Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                done.TrySetResult();
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static async Task Until(Func<bool> ready) { var end = Environment.TickCount64 + 4000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Manual action state did not arrive."); await Task.Delay(10); } }
    private static async Task<BroadcastEnvelope> Packet(UdpClient socket, Func<BroadcastEnvelope, bool> matches)
    { using var timeout = new CancellationTokenSource(4000); while (true) { var message = BroadcastEnvelope.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer); if (matches(message)) return message; } }
    private static int FreeUdp() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
