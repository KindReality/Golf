using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Windows;
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
                            new() { Name = "idle-entry" }, new() { Name = "idle-exit" }, new() { Name = "active-entry" }, new() { Name = "active-exit" }],
                        Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") })
                    };
                    var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    await using var host = new ControllerHost(path);
                    using var form = new Form { ClientSize = new(1280, 860), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                    using var page = new ShowEditorPage(host); form.Controls.Add(page); form.Show();
                    ComboBox Field(string name) => Descendants(page).OfType<ComboBox>().Single(c => c.AccessibleName == name);
                    void Select(string field, string value) { var box = Field(field); box.SelectedItem = box.Items.Cast<object>().Single(v => v.ToString() == value); }
                    Select("Activation storyboard", "idle-entry"); Select("Deactivation storyboard", "idle-exit");
                    Check(page.HasUnsavedChanges, "Scene storyboard selections mark the draft dirty");
                    await page.SaveAsync(); await Until(() => host.Options.Scenes[0].ActivatingPlaylist == "idle-entry"); page.RefreshState();
                    page.Navigate("Scenes", 1);
                    Select("Activation storyboard", "active-entry"); Select("Deactivation storyboard", "active-exit");
                    await page.SaveAsync(); await Until(() => host.Options.Scenes[1].DeactivatingPlaylist == "active-exit"); page.RefreshState();
                    page.Navigate("Scenes", 0);
                    Check(Field("Activation storyboard").Text == "idle-entry" && Field("Ambient playlist").Text == "idle-ambient" && Field("Deactivation storyboard").Text == "idle-exit", "Idle retains its three playlist references after saving and navigating away");
                    page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "active-entry" && Field("Ambient playlist").Text == "active-ambient" && Field("Deactivation storyboard").Text == "active-exit", "Active retains its three playlist references after saving and navigating away");
                    using var reopened = new ShowEditorPage(host); reopened.Navigate("Scenes", 1);
                    var scene = Descendants(reopened).OfType<SceneEditorView>().Single().Read();
                    var saved = ControllerOptions.Parse(File.ReadAllText(path), options.Build);
                    Check(scene == saved.Scenes[1], "Reopening the editor reads the same saved scene assignments from configuration");
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
                    await page.SaveAsync(); await Until(() => host.Options.Scenes[1].AmbientPlaylist == "new ambient"); page.RefreshState();
                    page.Navigate("Scenes", 0); page.Navigate("Scenes", 1);
                    Check(Field("Activation storyboard").Text == "new activation" && Field("Ambient playlist").Text == "new ambient" && Field("Deactivation storyboard").Text == "active-exit",
                        "Newly created playlists remain assigned after Save and returning to the scene");
                    foreach (var phase in new[] { "activation", "ambient", "deactivation" })
                    {
                        Descendants(page).OfType<Button>().Single(b => b.Text == "New " + phase + " playlist").PerformClick();
                        var expected = "active " + phase;
                        Check(page.Session.Current.Playlists.Last().Name == expected, "Scene creates a correctly typed " + phase + " playlist directly");
                        await page.SaveAsync(); await Until(() => host.Options.Playlists.Any(p => p.Name == expected)); page.RefreshState();
                        Descendants(page).OfType<Button>().Single(b => b.Text == "← Back").PerformClick();
                        var label = phase == "ambient" ? "Ambient playlist" : phase == "activation" ? "Activation storyboard" : "Deactivation storyboard";
                        Check(Field(label).Text == expected, "Save and Back preserve the automatically assigned " + phase + " playlist");
                    }
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
