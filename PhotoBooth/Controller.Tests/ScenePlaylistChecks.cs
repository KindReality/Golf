using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Windows;
using Controller.Scheduling;
using PhotoBooth.Diagnostics;

internal static class ScenePlaylistChecks
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
                var folder = Path.Combine(Path.GetTempPath(), "Controller-scene-links-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
                    var options = ShowEditorChecks.Fixture() with
                    {
                        ArduinoDiscovery = new() { Enabled = false }, UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), DefinitionsPort = FreeTcp(), UdpInterfaceAddress = "127.0.0.1", Triggers = [],
                        Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient" }, new() { Name = "active", AmbientPlaylist = "active-ambient" }],
                        Playlists = [new() { Name = "idle-ambient", Type = "ambient" }, new() { Name = "active-ambient", Type = "ambient" },
                            new() { Name = "idle-entry" }, new() { Name = "active-entry" }],
                        Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") })
                    };
                    var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    await using var host = new ControllerHost(path);
                    using var form = new Form { ClientSize = new(1280, 860), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                    using var page = new ShowEditorPage(host); form.Controls.Add(page); form.Show();
                    ComboBox Field(string name) => Descendants(page).OfType<ComboBox>().Single(c => c.AccessibleName == name);
                    Button SaveButton() => Descendants(page).OfType<Button>().Single(b => b.Text == "Save");
                    Label SaveStatus() => Descendants(page).OfType<Label>().Single(c => c.AccessibleName == "Editor save status");
                    void Select(string field, string value) { var box = Field(field); box.SelectedItem = box.Items.Cast<object>().Single(v => v.ToString() == value); }
                    async Task Save()
                    {
                        Check(SaveButton().Enabled, "Scene playlist edits enable the Save button");
                        SaveButton().PerformClick();
                        await Until(() => !page.HasUnsavedChanges);
                    }
                    Check(SaveButton().Enabled, "Save remains available to read current controls before any draft notification");
                    SaveButton().PerformClick();
                    Check(SaveStatus().Text == "No changes to save.", "Clicking Save without changes gives visible feedback");
                    Select("Activation storyboard", "idle-entry"); Select("Ambient playlist", "active-ambient");
                    Check(page.HasUnsavedChanges, "Scene storyboard selections mark the draft dirty");
                    await Save(); await Until(() => host.Options.Scenes[0].ActivatingPlaylist == "idle-entry"); page.RefreshState();
                    Check(SaveStatus().Text.Contains("Activation: idle-entry") && SaveStatus().Text.Contains("Ambient: active-ambient"),
                        "Save confirms the actual scene playlist assignments");
                    page.Navigate("Scenes", 1);
                    Select("Activation storyboard", "active-entry");
                    await Save(); await Until(() => host.Options.Scenes[1].ActivatingPlaylist == "active-entry"); page.RefreshState();
                    page.Navigate("Scenes", 0);
                    Check(Field("Activation storyboard").Text == "idle-entry" && Field("Ambient playlist").Text == "active-ambient", "Idle retains both changed playlist references after clicking Save and navigating away");
                    page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "active-entry" && Field("Ambient playlist").Text == "active-ambient", "Active retains its two playlist references after saving and navigating away");
                    using var reopened = new ShowEditorPage(host); reopened.Navigate("Scenes", 1);
                    var scene = Descendants(reopened).OfType<SceneEditorView>().Single().Read();
                    var saved = ControllerOptions.Parse(File.ReadAllText(path), options.Build);
                    Check(scene == saved.Scenes[1], "Reopening the editor reads the same saved scene assignments from configuration");
                    Select("Activation storyboard", "(None)"); Select("Ambient playlist", "idle-ambient");
                    await Save(); await Until(() => host.Options.Scenes[1].ActivatingPlaylist is null); page.RefreshState();
                    page.Navigate("Scenes", 0); page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "(None)" && Field("Ambient playlist").Text == "idle-ambient",
                        "Clearing activation and replacing ambient survives Save and reopening");
                    // Match creating a playlist, returning to a scene and assigning it.
                    page.Navigate("Playlists", 0);
                    Descendants(page).OfType<Button>().Single(b => b.Text == "+ Storyboard").PerformClick();
                    Descendants(page).OfType<TextBox>().Single(c => c.AccessibleName == "Storyboard playlist").Text = "new activation";
                    page.Navigate("Scenes", 1);
                    Select("Activation storyboard", "new activation");
                    page.Navigate("Playlists", 0);
                    Descendants(page).OfType<Button>().Single(b => b.Text == "+ Ambient").PerformClick();
                    Descendants(page).OfType<TextBox>().Single(c => c.AccessibleName == "Ambient playlist").Text = "new ambient";
                    page.Navigate("Scenes", 1); Select("Ambient playlist", "new ambient");
                    await Save(); await Until(() => host.Options.Scenes[1].AmbientPlaylist == "new ambient"); page.RefreshState();
                    page.Navigate("Scenes", 0); page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "new activation" && Field("Ambient playlist").Text == "new ambient",
                        "Newly created playlists remain assigned after Save and returning to the scene");
                    foreach (var phase in new[] { "activation", "ambient" })
                    {
                        Descendants(page).OfType<Button>().Single(b => b.Text == "New " + phase + " playlist").PerformClick();
                        var expected = "active " + phase;
                        Check(page.Session.Current.Playlists.Last().Name == expected, "Scene creates a correctly typed " + phase + " playlist directly");
                        await Save(); await Until(() => host.Options.Playlists.Any(p => p.Name == expected)); page.RefreshState();
                        Descendants(page).OfType<Button>().Single(b => b.Text == "← Back").PerformClick();
                        var label = phase == "ambient" ? "Ambient playlist" : "Activation storyboard";
                        Check(Field(label).Text == expected, "Save and Back preserve the automatically assigned " + phase + " playlist");
                    }
                    // A native dropdown can change its selection before draft
                    // notifications are processed. Save must still read it.
                    Select("Activation storyboard", "idle-entry");
                    typeof(ShowEditorPage).GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(page, false);
                    page.RefreshState();
                    Check(!page.HasUnsavedChanges && SaveButton().Enabled, "A missing draft notification cannot disable Save");
                    await Save(); await Until(() => host.Options.Scenes[1].ActivatingPlaylist == "idle-entry"); page.RefreshState();
                    page.Navigate("Scenes", 0); page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "idle-entry", "Save reads and retains selections even without a draft notification");
                    Select("After activation", "Go to scene"); Select("Next scene", "idle");
                    Descendants(page).OfType<Button>().Single(b => b.Text == "Edit activation playlist").PerformClick();
                    Descendants(page).OfType<TextBox>().Single(c => c.AccessibleName == "Playlist minimum duration seconds").Text = "7.125";
                    await Save(); await Until(() => host.Options.Playlist("idle-entry").MinimumDurationMilliseconds == 7125); page.RefreshState();
                    Descendants(page).OfType<Button>().Single(b => b.Text == "← Back").PerformClick();
                    await Save(); await Until(() => host.Options.Scenes[1].Transition is not null); page.RefreshState();
                    page.Navigate("Scenes", 0); page.Navigate("Scenes", 1);
                    Check(Field("After activation").Text == "Go to scene" && Field("Next scene").Text == "idle" &&
                        host.Options.Scenes[1].AmbientPlaylist is null && host.Options.Scenes[1].Transition is { Device: "*" } && host.Options.Playlist("idle-entry").MinimumDurationMilliseconds == 7125,
                        "Save/reopen retains next scene, broadcast destination and millisecond-precision minimum while omitting ambient");
                    await Until(() => Descendants(page).OfType<Label>().Single(c => c.AccessibleName == "Scene transition estimate").Text.Contains("7.125 s"));
                    Check(!Field("Ambient playlist").Visible && Field("Next scene").Visible, "Only the selected after-activation behavior is shown");
                    using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/controller-scene-transition.png")));
                    }
                    var timingShow = ShowConfiguration.From(host.Options);
                    timingShow = timingShow with { Playlists = timingShow.Playlists.Select(p => p.Name == "idle-entry" ? p with
                        { Items = [new() { Name = "media", DelayMilliseconds = 1000, Commands = [new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "clip.mp4" }) }] }] } : p).ToArray() };
                    Task<SceneTransitionEstimate> Estimate(SceneOptions scene, ShowConfiguration graph, CancellationToken token) =>
                        SceneTransitionTiming.EstimateAsync(scene, new() { Playlists = graph.Playlists }, _ => 9000, token);
                    using (var preview = new SceneEditorView(host.Options.Scenes[1], timingShow, false, () => { }, _ => { }, estimateTransition: Estimate))
                    {
                        await Until(() => Descendants(preview).OfType<Label>().Single(c => c.AccessibleName == "Scene transition estimate").Text.Contains("10 s"));
                        Check(timingShow.Playlists.Single(p => p.Name == "idle-entry").MinimumDurationMilliseconds == 7125, "An automatic media estimate does not overwrite the configured scene minimum");
                    }
                    timingShow = timingShow with { Playlists = timingShow.Playlists.Select(p => p.Name == "idle-entry" ? p with { Items = [p.Items[0] with { DelayMilliseconds = 3000 }] } : p).ToArray() };
                    using (var preview = new SceneEditorView(host.Options.Scenes[1], timingShow, false, () => { }, _ => { }, estimateTransition: Estimate))
                    {
                        await Until(() => Descendants(preview).OfType<Label>().Single(c => c.AccessibleName == "Scene transition estimate").Text.Contains("12 s"));
                        Check(true, "Returning to the scene after editing its activation playlist refreshes the displayed estimate");
                    }
                    Select("After activation", "Run ambient playlist"); Select("Ambient playlist", "idle-ambient");
                    await Save(); await Until(() => host.Options.Scenes[1].Transition is null); page.RefreshState();
                    Check(host.Options.Scenes[1].AmbientPlaylist == "idle-ambient", "Switching back to ambient removes the next-scene behavior");
                    Select("Ambient playlist", "active-ambient");
                    var prior = File.ReadAllText(path);
                    File.WriteAllText(path, prior.Replace("active ambient", "changed remotely", StringComparison.Ordinal));
                    SaveButton().PerformClick();
                    await Until(() => SaveStatus().Text.Contains("changed elsewhere"));
                    Check(page.HasUnsavedChanges && Field("Ambient playlist").Text == "active-ambient" &&
                        ControllerOptions.Parse(File.ReadAllText(path), options.Build).Scenes[1].AmbientPlaylist != "active-ambient",
                        "A failed save reports the reason and preserves the selected scene assignments in the draft");
                    Check(host.Engine.Snapshot().Length == 0, "Editing and saving scene links does not activate their storyboards");
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Telemetry.Stop(); Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                done.TrySetResult();
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 3000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Scene playlist assignments did not persist."); await Task.Delay(10); } }
    private static int FreeUdp() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
}
