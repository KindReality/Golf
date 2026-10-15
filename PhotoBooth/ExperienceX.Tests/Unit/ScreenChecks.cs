using ExperienceX;
using PhotoBooth.Networking;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

internal static class ScreenChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-screens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, """
                {"ConfigurationVersion":2,"configurationMode":true,"DefaultMonitorName":"Projector","MonitorAliases":{"Projector":"DISPLAY1"},
                 "MonitorBackgrounds":{"DISPLAY1":{"BackgroundColor":"#123456","IsTransparent":true}},
                 "DisplayKeystones":{"DISPLAY1":{"Enabled":true,"TopLeft":{"X":0.1,"Y":0.1}}},"Custom":17,"UdpPort":21325}
                """);
            var store = new PointConfigurationStore(path);
            var options = ExperienceOptions.Parse(store.EnsureDisplays(["DISPLAY1", "DISPLAY2"]));
            Check(options.DefaultDisplayName == "Projector" && options.DisplayAliases["Projector"] == "DISPLAY1", "Display rename lost aliases/defaults.");
            Check(options.ScreensFor("DISPLAY1").Single().Name == "screen1" && options.PointsFor("DISPLAY1").TopLeft.X == .1, "Legacy keystone did not become screen1.");
            Check(options.DisplayBackgrounds["DISPLAY1"].IsTransparent && options.DisplayBackgrounds["DISPLAY1"].BackgroundColor == "#123456", "Migration lost transparency/color.");
            Check(options.UdpEventsPort == 54242 && options.UdpCommandsPort == 54243, "Hitchhiker ports were not migrated.");
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            Check(root["MonitorAliases"] is null && root["MonitorBackgrounds"] is null && root["DisplayKeystones"] is null && root["Custom"]!.GetValue<int>() == 17, "Migration left legacy fields or lost custom fields.");
            var second = await store.AddScreenAsync("DISPLAY1");
            Check(second.Name == "screen2" && second.Points == ScreenOptions.CenteredPoints(), "Second video was not centered at one-third size.");
            Check(ScreenOptions.New([new(), second]).Points == ScreenOptions.CenteredPoints(), "Subsequent videos were not centered at one-third size.");
            Check(ScreenOptions.New([],"mat").Points == ScreenOptions.CenteredPoints(), "First mat on an empty display was not one-third size.");
            Check(ScreenOptions.New([new(){Type="mat"}]).Points == new PointOptions(), "A mat prevented the first video from filling the display.");
            await store.RenameScreenAsync("DISPLAY1", second.Id, "screen2", "News");
            options = ExperienceOptions.Parse(File.ReadAllText(path));
            Check(options.ScreensFor("DISPLAY1")[1].Id == second.Id && options.ScreensFor("DISPLAY1")[1].Name == "News", "Rename changed identity or order.");
            try
            {
                await store.RenameScreenAsync("DISPLAY1", second.Id, "News", "SCREEN1");
                throw new Exception("Duplicate name was accepted.");
            }
            catch (ArgumentException) { }
            try
            {
                await store.RenameScreenAsync("DISPLAY1", second.Id, "screen2", "Stale");
                throw new Exception("Stale rename overwrote a remote edit.");
            }
            catch (PointEditConflictException) { }
            root = JsonNode.Parse(File.ReadAllText(path))!;
            root["DisplayScreens"]!["DISPLAY2"] = new JsonArray();
            File.WriteAllText(path, root.ToJsonString());
            store.EnsureDisplays(["DISPLAY1", "DISPLAY2", "DISPLAY3"]);
            options = ExperienceOptions.Parse(File.ReadAllText(path));
            Check(options.ScreensFor("DISPLAY2").Count == 0 && options.ScreensFor("DISPLAY3").Single().Name == "screen1", "Inventory recreated a deliberately empty display or failed new hardware defaults.");
            var first = await store.AddScreenAsync("DISPLAY2");
            Check(first.Name == "screen1" && first.Points == new PointOptions(), "First screen on empty display was not full size.");
            await store.DeleteScreenAsync("DISPLAY2", first);
            store.EnsureDisplays(["DISPLAY1", "DISPLAY2"]);
            Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("DISPLAY2").Count == 0, "Deleted last screen was recreated by hardware inventory.");
            try
            {
                await store.DeleteScreenAsync("DISPLAY1", second);
                throw new Exception("Stale screen deletion overwrote a renamed screen.");
            }
            catch (PointEditConflictException) { }
            Check(JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() == 17, "Screen deletion lost unrelated configuration.");
            var editable = new PointOptions { Enabled = true, TopLeft = new(.25, .25), TopRight = new(.75, .25), BottomLeft = new(.25, .75), BottomRight = new(.75, .75) };
            var moved = PointEditing.Translate(editable, .1, -.1);
            moved.Validate();
            Check(moved.TopLeft == new ScreenPoint(.35, .15) && moved.BottomRight == new ScreenPoint(.85, .65), "Body drag changed screen size or failed to translate all corners.");
            var clamped = PointEditing.Translate(editable, 2, -2);
            clamped.Validate();
            Check(clamped.TopLeft == new ScreenPoint(.5, 0) && clamped.BottomRight == new ScreenPoint(1, .5), "Body drag did not stop at display edges.");
            Check(PointEditing.Translate(first.Points, 1, 1) == first.Points, "Full-size screen moved outside its display.");
            var bounds = editable;
            Check(ScreenSelection.Contains(bounds, .5, .5) && !ScreenSelection.Contains(bounds, .1, .5), "Screen hit geometry failed.");
            Check(ScreenSelection.Target(new ExperienceRequest { Screen = "Projector/News" }) == ("Projector", "News"), "Combined target parsing failed.");
            var routing = new ExperienceOptions
            {
                DefaultDisplayName = "left", DisplayAliases = new() { ["Projector"] = "right" },
                DisplayScreens = new()
                {
                    ["left"] = [new() { Type = "mat", Id = "mask", Name = "mask" }, new(), new() { Id = "second", Name = "screen2" }],
                    ["right"] = [new()], ["empty"] = [], ["offline"] = [new()]
                }
            };
            Check(ScreenSelection.VideoOutputs(new() { Screen = "*" }, routing, ["left", "right", "empty"], "left").Count == 3, "All-screen routing included mats/offline/empty displays or missed a video.");
            Check(ScreenSelection.VideoOutputs(new() { Screen = "Projector/*" }, routing, ["left", "right"], "left").Single().Display == "right", "Display alias wildcard failed.");
            Check(ScreenSelection.VideoOutputs(new(), routing, ["left", "right"], "left").Single().Screen.Name == "screen1", "Default routing no longer selects just the first video screen.");
            Check(ScreenSelection.VideoOutputs(new() { Screen = "*/screen1" }, routing, ["left", "right"], "left").Count == 2, "Named screen across displays failed.");
            Check(ScreenSelection.VideoOutputs(new() { Screen = "*", DisplayName = "left" }, routing, ["left", "right"], "left").Count == 2, "All screens on an explicit display failed.");
            var cycle = new PointHitCycle();
            Check(new[] { cycle.Pick([0, 1]), cycle.Pick([0, 1]), cycle.Pick([0, 1]) }.SequenceEqual([0, 1, 0]), "Screen round robin did not preserve definition order.");
            await NetworkPorts();
            await NativeSelection(path);
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed legacy display/screen migration, naming/conflicts, zero-screen/new hardware defaults, screen creation geometry, combined targets, separate UDP ports, native overlap selection and name editing.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static async Task NetworkPorts()
    {
        var received = new List<string>();
        await using var listener = new UdpBroadcastListener(() => new BroadcastOptions(), (message, _) => { lock (received) received.Add(message.Type); return Task.CompletedTask; });
        using var temporary = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var eventsPort = ((IPEndPoint)temporary.Client.LocalEndPoint!).Port;
        temporary.Dispose();
        listener.Bind(0, eventsPort);
        using var client = new UdpClient();
        var options = new BroadcastOptions();
        var command = BroadcastEnvelope.Create(options, "command", "*", new
        {
            experience = "video",
            value = "ping.mp4"
        }, "port-check");
        var announcement = BroadcastEnvelope.Create(options, "event", "*", new
        {
            @event = "sensor.enter"
        }, "event-check");
        await client.SendAsync(command, new IPEndPoint(IPAddress.Loopback, eventsPort));
        await Task.Delay(100);
        lock (received)
            Check(received.Count == 0, "A command ran on the events port.");
        await client.SendAsync(command, new IPEndPoint(IPAddress.Loopback, listener.Port));
        await client.SendAsync(announcement, new IPEndPoint(IPAddress.Loopback, eventsPort));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            lock (received)
            {
                if (received.Count == 2)
                    break;
            }
            if (DateTime.UtcNow > deadline)
                throw new Exception("Separate ports did not dispatch both message types.");
            await Task.Delay(10);
        }
        Check(options.PortFor("event") == 54242 && options.PortFor("command") == 54243, "Sender uses wrong destination ports.");
    }
    private static async Task NativeSelection(string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var options = ExperienceOptions.Parse(File.ReadAllText(path));
                using var window = new MainWindow("DISPLAY1", options) { ClientSize = new(1920, 1080) };
                var editor = new PointCalibration(() => options, () => window.Surfaces, new PointConfigurationStore(path), () => Task.CompletedTask);
                window.AttachCalibration(editor);
                void Click(int x, int y)
                {
                    SendMessage(window.Handle, 0x201, 1, (nint)((y << 16) | x));
                    SendMessage(window.Handle, 0x202, 0, (nint)((y << 16) | x));
                }
                Click(1152, 648);
                Check(editor.SelectedScreen?.ScreenName == "screen1", "First overlapping screen was not selected.");
                Click(1152, 648);
                Check(editor.SelectedScreen?.ScreenName == "News", "Second overlapping screen was not selected.");
                Click(1152, 648);
                Check(editor.SelectedScreen?.ScreenName == "screen1", "Screen selection did not wrap.");
                var selected = (ScreenSurface)editor.SelectedScreen!;
                var center = ScreenNameLabel.Center(options.PointsFor("DISPLAY1", selected.ScreenId), 1920, 1080);
                string? committed = null;
                int editing = 0;
                window.TextEditingChanged += enabled => editing += enabled ? 1 : -1;
                window.RenameRequested += (_, _, name) => committed = name;
                Click((int)center.X, (int)center.Y);
                var text = window.Controls.OfType<ScreenNameEditor>().Single();
                Check(editing == 1 && text.Text == "screen1", "Clicking name did not enter editing.");
                text.Text = "Cancelled";
                SendMessage(text.Handle, 0x100, 27, 0);
                Check(editing == 0 && committed is null && window.Controls.Count == 0, "Escape did not cancel name editing.");
                foreach (var key in new[] { 13, 9 })
                {
                    Click((int)center.X, (int)center.Y);
                    text = window.Controls.OfType<ScreenNameEditor>().Single();
                    text.Text = "Renamed";
                    SendMessage(text.Handle, 0x100, key, 0);
                    Check(committed == "Renamed" && editing == 0, "Enter/Tab did not commit name editing.");
                    committed = null;
                }
                int adds = 0, videos = 0, closes = 0;
                using var hotkeys = new ApplicationHotkeys(() => { }, () => closes++, () => { }, () => true);
                hotkeys.AddScreenPrefix = () => adds++;
                hotkeys.AddVideoScreen = () => videos++;
                Check(hotkeys.Register(true).Length == 0, "A/V shortcut registration failed.");
                SendMessage(hotkeys.Handle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                SendMessage(hotkeys.Handle, 0x312, ApplicationHotkeys.VideoId, (nint)(0x56 << 16));
                Check(adds == 1 && videos == 1, "A/V callbacks failed.");
                hotkeys.Register(true, true);
                SendMessage(hotkeys.Handle, 0x312, ApplicationHotkeys.RestartId, (nint)(0x58 << 16));
                Check(closes == 0, "Typing X while editing a name closed the app.");
                hotkeys.Register(true, false);
                SendMessage(hotkeys.Handle, 0x312, ApplicationHotkeys.RestartId, (nint)(0x58 << 16));
                Check(closes == 1, "Shortcuts did not resume after name editing.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
