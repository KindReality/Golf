using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class ScreenEditingWorkflowChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-screen-workflow-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var displays = Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray();
                    var primary = Forms.Screen.PrimaryScreen!.DeviceName;
                    var other = displays.FirstOrDefault(d => d != primary) ?? primary;
                    using var commandSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var eventSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var commandsPort = ((IPEndPoint)commandSocket.Client.LocalEndPoint!).Port;
                    var eventsPort = ((IPEndPoint)eventSocket.Client.LocalEndPoint!).Port;
                    commandSocket.Dispose(); eventSocket.Dispose();
                    var definition = new PerspectiveDefinition { Name = "bottom-third", Rectangle = new() { Y = 66.67, Height = 33.33 } };
                    var definitions = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "test-controller", Hostname = Environment.MachineName, Perspectives = [definition] };
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, ConfigurationMode = true, CalibrationGridEnabled = false,
                        LocalController = new() { Enabled = false },
                        DefaultDisplayName = primary, UdpCommandsPort = commandsPort, UdpEventsPort = eventsPort,
                        ControllerDefinitions = new() { [definitions.ControllerId] = definitions },
                        AudioDevices = new() { ["workflow-roles"] = new() { Name = "Workflow roles", Roles = "Front,Scary" } },
                        DisplayScreens = displays.ToDictionary(d => d, d => d == other && d != primary ? new List<ScreenOptions> { new() } : []),
                        DisplayBackgrounds = displays.ToDictionary(d => d, _ => new DisplayBackgroundOptions { IsTransparent = true })
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    await using var host = new ExperienceHost(dispatcher, path);
                    host.Start();
                    ExperienceOptions Read()
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        return ExperienceOptions.Parse(reader.ReadToEnd());
                    }
                    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
                    async Task Until(Func<bool> ready)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        while (true)
                        {
                            try { if (ready()) return; }
                            catch (IOException) { /* Atomic replacement can temporarily contend with a test reader. */ }
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Screen workflow timed out.");
                            await Task.Delay(20);
                        }
                    }
                    void AddVideo()
                    {
                        using var accept = new Forms.Timer { Interval = 20 };
                        accept.Tick += (_, _) =>
                        {
                            var picker = Forms.Application.OpenForms.OfType<ScreenPropertiesDialog>().FirstOrDefault();
                            if (picker is null) return;
                            if (Read().ScreensFor(primary).Count == 0)
                                foreach (var list in Descendants(picker).OfType<Forms.CheckedListBox>())
                                    for (var index = 0; index < list.Items.Count; index++)
                                        if (list.Items[index] is string role && role is "Front" or "Scary") list.SetItemChecked(index, true);
                            picker.DialogResult = Forms.DialogResult.OK;
                            picker.Close();
                        };
                        accept.Start();
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.VideoId, (nint)(0x56 << 16));
                    }
                    void Click(MainWindow window, int x, int y)
                    {
                        SendMessage(window.Handle, 0x201, 1, (nint)((y << 16) | x));
                        SendMessage(window.Handle, 0x202, 0, (nint)((y << 16) | x));
                    }
                    var editingWindow = host.DisplayWindows.Single(w => w.DisplayName == primary);
                    editingWindow.Activate();
                    editingWindow.Focus();
                    await GForceFullscreenChecks.FocusForTest(editingWindow.Handle);
                    await Until(() => PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused);
                    await Task.Delay(150); // Focus-scoped shortcut registration refreshes every 100 ms.
                    AddVideo();
                    await Until(() => Read().ScreensFor(primary).Count == 1 && host.SelectedScreen?.DisplayName == primary);
                    var first = Read().ScreensFor(primary).Single();
                    Check(first.Name == "screen1" && first.Points == new PointOptions(), "A/V did not create a full-size first screen on the default display.");
                    Check(RoleList.Parse(first.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Front", "Scary"]), "Unified creation did not persist both selected roles.");
                    void PickPerspective(int index, Forms.DialogResult result)
                    {
                        using var accept = new Forms.Timer { Interval = 20 };
                        accept.Tick += (_, _) =>
                        {
                            var picker = Forms.Application.OpenForms.OfType<PerspectivePicker>().FirstOrDefault();
                            if (picker is null) return;
                            picker.Controls.OfType<Forms.ListBox>().Single().SelectedIndex = index;
                            picker.DialogResult = result;
                            picker.Close();
                        };
                        accept.Start();
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.PerspectiveId, (nint)(0x50 << 16));
                    }
                    PickPerspective(1, Forms.DialogResult.Cancel);
                    await Task.Delay(100);
                    Check(Read().ScreensFor(primary).Single().Perspective is null, "Cancel changed the screen perspective.");
                    PickPerspective(1, Forms.DialogResult.OK);
                    await Until(() => Read().ScreensFor(primary).Single().Perspective?.PerspectiveId == definition.Id);
                    Check(Read().ScreensFor(primary).Single().Points == first.Points, "Selecting a source perspective changed destination points.");
                    PickPerspective(0, Forms.DialogResult.OK);
                    await Until(() => Read().ScreensFor(primary).Single().Perspective is null);
                    var edgeStart = Read().ScreensFor(primary).Single().Points;
                    var middleX = editingWindow.ClientSize.Width / 2;
                    SendMessage(editingWindow.Handle, 0x201, 1, (nint)middleX);
                    SendMessage(editingWindow.Handle, 0x200, 1, (nint)((40 << 16) | middleX));
                    SendMessage(editingWindow.Handle, 0x202, 0, (nint)((40 << 16) | middleX));
                    await Until(() => Read().ScreensFor(primary).Single().Points.TopLeft.Y > .02);
                    var edgeAfter = Read().ScreensFor(primary).Single().Points;
                    Check(Math.Abs(edgeAfter.TopLeft.Y - edgeAfter.TopRight.Y) < 1e-9 && edgeAfter.BottomLeft == edgeStart.BottomLeft && edgeAfter.BottomRight == edgeStart.BottomRight, "Dragging a video edge did not translate both endpoints while preserving its angle.");
                    await Until(() => editingWindow.ScreenDefinition(first.Id)!.Points == edgeAfter && !host.CalibrationBusy);
                    SendMessage(editingWindow.Handle, 0x100, 0x5A, 0);
                    await Until(() => Read().ScreensFor(primary).Single().Points == edgeStart);
                    Console.WriteLine("Passed native edge selection, paired-point movement, persistence and undo.");
                    Console.WriteLine("Passed actual perspective picker selection, cancellation and restoration to Full without changing destination points.");
                    await GForceFullscreenChecks.FocusForTest(editingWindow.Handle);
                    await Task.Delay(150); // Focus-scoped letters register after the modal picker closes.
                    var target = host.DisplayWindows.Single(w => w.DisplayName == other);
                    // Select a screen away from its editable center label.
                    Click(target, (int)(target.ClientSize.Width * .65), (int)(target.ClientSize.Height * .65));
                    AddVideo();
                    await Until(() => Read().ScreensFor(other).Count == 2 && host.SelectedScreen?.DisplayName == other && host.SelectedScreen?.ScreenName == "screen2");
                    var created = Read().ScreensFor(other)[1];
                    Check(created.Name == "screen2" && created.Points == new PointOptions(), "A/V did not create a full-display screen on the selected screen's display.");
                    if (other != primary) Check(Read().ScreensFor(primary).Count == 1, "Creating on another display also added a screen to the default display.");
                    await Until(() => target.ScreenDefinition(created.Id) is not null);
                    async Task Edit(string name, int key)
                    {
                        var center = ScreenNameLabel.Center(created.Points, target.ClientSize.Width, target.ClientSize.Height);
                        Click(target, (int)center.X, (int)center.Y);
                        await Until(() => target.Controls.OfType<ScreenNameEditor>().Any());
                        var text = target.Controls.OfType<ScreenNameEditor>().Single();
                        text.Text = name;
                        SendMessage(text.Handle, 0x100, key, 0);
                        await Until(() => target.Controls.OfType<ScreenNameEditor>().Any() == false);
                    }
                    await Edit("Lobby", 13);
                    await Until(() => Read().ScreensFor(other)[1].Name == "Lobby" && target.ScreenDefinition(created.Id)!.Name == "Lobby");
                    await Edit("Stage", 9);
                    await Until(() => Read().ScreensFor(other)[1].Name == "Stage" && target.ScreenDefinition(created.Id)!.Name == "Stage");
                    await Edit("Cancelled", 27);
                    await Task.Delay(150);
                    Check(Read().ScreensFor(other)[1].Name == "Stage", "Escape changed the saved screen name.");
                    await Edit("SCREEN1", 13);
                    await Task.Delay(200);
                    Check(Read().ScreensFor(other)[1].Name == "Stage", "A duplicate name replaced the valid saved name.");
                    // An additional A/V must keep the selected screen's display and choose the lowest free default name.
                    AddVideo();
                    await Until(() => Read().ScreensFor(other).Count == 3);
                    Check(Read().ScreensFor(other)[2].Name == "screen3", "New screen default naming did not preserve the renamed screen's reserved default.");
                    Check(Read().ScreensFor(other)[1].Id == created.Id && File.Exists(path + ".bak"), "Renaming lost screen identity or atomic backup.");
                    var dragged = Read().ScreensFor(other)[2];
                    await Until(() => target.ScreenDefinition(dragged.Id) is not null);
                    Check(dragged.Points == new PointOptions(), "Third screen did not fill the display.");
                    // Resize before testing body movement: a full-display screen has no room to move.
                    var resized = new PointOptions { Enabled = true, TopLeft = new(.25, .25), TopRight = new(.75, .25), BottomLeft = new(.25, .75), BottomRight = new(.75, .75) };
                    await new PointConfigurationStore(path).SaveAsync(other, dragged.Points, resized, dragged.Id);
                    dragged = dragged with { Points = resized };
                    await Until(() => target.ScreenDefinition(dragged.Id)!.Points == resized);
                    var x = (int)(target.ClientSize.Width * .65);
                    var y = (int)(target.ClientSize.Height * .65);
                    // Press cycles overlapping screen bodies; release only until the intended screen is selected.
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        SendMessage(target.Handle, 0x201, 1, (nint)((y << 16) | x));
                        if (host.SelectedScreen?.ScreenId == dragged.Id) break;
                        SendMessage(target.Handle, 0x202, 0, (nint)((y << 16) | x));
                    }
                    Check(host.SelectedScreen?.ScreenId == dragged.Id && target.Capture, "Screen body did not acquire a drag on the round-robin selection.");
                    var endX = x + 60;
                    var endY = y + 30;
                    var expected = PointEditing.Translate(dragged.Points, 60.0 / (target.ClientSize.Width - 1), 30.0 / (target.ClientSize.Height - 1));
                    bool AtExpected(PointOptions value) => value.Enabled == expected.Enabled && PointEditing.Points(value).Zip(PointEditing.Points(expected)).All(pair =>
                        Math.Abs(pair.First.X - pair.Second.X) < 1e-9 && Math.Abs(pair.First.Y - pair.Second.Y) < 1e-9);
                    SendMessage(target.Handle, 0x200, 1, (nint)((endY << 16) | endX));
                    Check(Read().PointsFor(other, dragged.Id) == dragged.Points, "Body drag saved before mouse release.");
                    SendMessage(target.Handle, 0x202, 0, (nint)((endY << 16) | endX));
                    await Until(() => AtExpected(Read().PointsFor(other, dragged.Id)));
                    await Until(() => AtExpected(target.ScreenDefinition(dragged.Id)!.Points));
                    await Task.Delay(150); // Allow renderer acknowledgement/save confirmation to finish.
                    var beforeLabelDrag = Read().PointsFor(other, dragged.Id);
                    var dragLabel = ScreenNameLabel.Center(beforeLabelDrag, target.ClientSize.Width, target.ClientSize.Height);
                    var labelX = (int)dragLabel.X;
                    var labelY = (int)dragLabel.Y;
                    SendMessage(target.Handle, 0x201, 1, (nint)((labelY << 16) | labelX));
                    Check(!target.Controls.OfType<ScreenNameEditor>().Any(), "Holding the screen name opened the editor before distinguishing click from drag.");
                    SendMessage(target.Handle, 0x200, 1, (nint)(((labelY + 15) << 16) | (labelX + 15)));
                    SendMessage(target.Handle, 0x202, 0, (nint)(((labelY + 15) << 16) | (labelX + 15)));
                    await Until(() => Read().PointsFor(other, dragged.Id).TopLeft.X > beforeLabelDrag.TopLeft.X);
                    Check(!target.Controls.OfType<ScreenNameEditor>().Any(), "Dragging from the screen name entered text editing.");
                    await Until(() => target.ScreenDefinition(dragged.Id)!.Points == Read().PointsFor(other, dragged.Id));
                    await Task.Delay(150);
                    SendMessage(target.Handle, 0x100, 0x2E, 0);
                    await Until(() => Read().ScreensFor(other).Count == 2 && host.SelectedScreen is null);
                    Check(Read().ScreensFor(other).All(s => s.Id != dragged.Id), "Delete removed the wrong screen.");
                    SendMessage(target.Handle, 0x100, 0x2E, 0);
                    await Task.Delay(100);
                    Check(Read().ScreensFor(other).Count == 2, "Delete without selection removed another screen.");
                    Click(target, (int)(target.ClientSize.Width * .4), (int)(target.ClientSize.Height * .4));
                    var toDelete = host.SelectedScreen!;
                    var label = ScreenNameLabel.Center(Read().PointsFor(other, toDelete.ScreenId), target.ClientSize.Width, target.ClientSize.Height);
                    Click(target, (int)label.X, (int)label.Y);
                    await Until(() => target.Controls.OfType<ScreenNameEditor>().Any());
                    var nameEditor = target.Controls.OfType<ScreenNameEditor>().Single();
                    nameEditor.Text = "Typing";
                    nameEditor.SelectAll();
                    SendMessage(nameEditor.Handle, 0x100, 0x2E, 0);
                    Check(Read().ScreensFor(other).Count == 2, "Delete while editing text removed a screen.");
                    SendMessage(nameEditor.Handle, 0x100, 27, 0);
                    SendMessage(target.Handle, 0x100, 0x2E, 0);
                    await Until(() => Read().ScreensFor(other).Count == 1 && host.SelectedScreen is null && !host.CalibrationBusy);
                    Click(target, (int)(target.ClientSize.Width * .4), (int)(target.ClientSize.Height * .4));
                    SendMessage(target.Handle, 0x100, 0x2E, 0);
                    await Until(() => Read().ScreensFor(other).Count == 0 && host.SelectedScreen is null);
                    await Task.Delay(1200);
                    Check(Read().ScreensFor(other).Count == 0, "Inventory restored the final deleted screen.");
                    var beforeMat = Read().ScreensFor(primary).Count;
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.MatId, (nint)(0x4D << 16));
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.MatId, (nint)(0x4D << 16));
                    await Until(() => Read().ScreensFor(primary).Count == beforeMat + 1 && Read().ScreensFor(primary).Last().IsNegative && host.SelectedScreen is not null);
                    var mask = Read().ScreensFor(primary).Last();
                    Check(mask.IsMat && host.SelectedScreen?.ScreenId == mask.Id, "Rapid A/M/M did not create/select a negative mat on the default display.");
                    var matWindow = host.DisplayWindows.Single(w => w.DisplayName == primary);
                    await Until(() => matWindow.ScreenDefinition(mask.Id)?.IsNegative == true);
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.MatId, (nint)(0x4D << 16));
                    await Until(() => matWindow.ScreenDefinition(mask.Id)?.IsNegative == false && !Read().ScreensFor(primary).Last().IsNegative);
                    Check(Read().PointsFor(primary, mask.Id) == mask.Points && Read().ScreensFor(primary).Count == beforeMat + 1, "M changed the mat geometry or created another screen instead of toggling.");
                    Console.WriteLine("Passed rapid A/M/M creation and selected-mat normal/negative toggle.");
                    await Task.Delay(150);
                    var mx = (int)((mask.Points.TopLeft.X + mask.Points.TopRight.X) / 2 * (matWindow.ClientSize.Width - 1));
                    var my = (int)(mask.Points.TopLeft.Y * (matWindow.ClientSize.Height - 1));
                    // The native double-click must insert precisely one point and save immediately.
                    Click(matWindow, mx, my);
                    SendMessage(matWindow.Handle, 0x203, 1, (nint)((my << 16) | mx));
                    SendMessage(matWindow.Handle, 0x202, 0, (nint)((my << 16) | mx));
                    await Until(() => Read().PointsFor(primary, mask.Id).Vertices.Count == 5);
                    await Until(() => matWindow.ScreenDefinition(mask.Id)!.Points.Vertices.Count == 5);
                    await Task.Delay(150);
                    var inserted = Read().PointsFor(primary, mask.Id).Vertices[1];
                    Check(Math.Abs(inserted.X - .5) < .003 && inserted.Y == mask.Points.TopLeft.Y, "Double-click point was not inserted on the chosen edge.");
                    // Added points must be draggable and leave a concave mask, rather than a quad.
                    SendMessage(matWindow.Handle, 0x201, 1, (nint)((my << 16) | mx));
                    SendMessage(matWindow.Handle, 0x200, 1, (nint)(((my + 45) << 16) | mx));
                    SendMessage(matWindow.Handle, 0x202, 0, (nint)(((my + 45) << 16) | mx));
                    await Until(() => Read().PointsFor(primary, mask.Id).Vertices[1].Y > inserted.Y + .04);
                    await Until(() => matWindow.ScreenDefinition(mask.Id)!.Points == Read().PointsFor(primary, mask.Id));
                    await Task.Delay(150);
                    SendMessage(matWindow.Handle, 0x100, 0x5A, 0);
                    await Until(() => Read().PointsFor(primary, mask.Id).Vertices[1] == inserted);
                    await Task.Delay(150);
                    SendMessage(matWindow.Handle, 0x100, 0x5A, 0);
                    await Until(() => Read().PointsFor(primary, mask.Id).Vertices.Count == 4);
                    await Task.Delay(150);
                    SendMessage(matWindow.Handle, 0x100, 0x2E, 0);
                    await Until(() => Read().ScreensFor(primary).Count == beforeMat && host.SelectedScreen is null);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    Directory.Delete(folder, true);
                    if (!completion.Task.IsCompleted) completion.TrySetResult();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Console.WriteLine("Passed full host A/V creation, renaming, round-robin body drag/release persistence, Delete selection/text guards, and deletion of the final screen using temporary configuration.");
        Console.WriteLine("Passed A/M mat creation, native edge double-click insertion, dragging the added point into a concavity, persisted undo and mat deletion.");
    }
    private static IEnumerable<Forms.Control> Descendants(Forms.Control parent) => parent.Controls.Cast<Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
