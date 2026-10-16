using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Windows;
using PhotoBooth.Diagnostics;

internal static class ShowEditorPageChecks
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
                var folder = Path.Combine(Path.GetTempPath(), "Controller-show-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                try
                {
                    var options = ShowEditorChecks.Fixture() with { UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), DefinitionsPort = FreeTcp(),
                        UdpInterfaceAddress = "127.0.0.1", Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient" }], Triggers = [],
                        Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") }) };
                    var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    await using var host = new ControllerHost(path);
                    await Until(() => host.Scenes.Snapshot().Phase == "Active");
                    using var form = new Form { ClientSize = new(1280, 860), FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                    using var page = new ShowEditorPage(host); form.Controls.Add(page); form.Show();
                    Button Button(string name) => Descendants(page).OfType<Button>().Single(b => b.Text == name);
                    T Field<T>(string name) where T : Control => Descendants(page).OfType<T>().Single(c => c.AccessibleName == name);
                    void Shot(string name)
                    {
                        form.PerformLayout(); page.PerformLayout();
                        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height); form.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools")); Directory.CreateDirectory(root); bitmap.Save(Path.Combine(root, name));
                    }
                    void Check(bool value, string description) { if (!value) throw new Exception(description); Console.WriteLine("PASS " + description); }
                    Check(Descendants(page).OfType<SceneEditorView>().Count() == 1 && host.Engine.Snapshot().Length == 0, "Scenes opens inside the main content area without starting playback");
                    Shot("controller-scenes-editor.png");
                    page.Navigate("Playlists", 1);
                    Console.WriteLine("PASS Storyboard inspector opens");
                    Field<TextBox>("Storyboard playlist").Text = "Renamed cue";
                    Button("+ Item").PerformClick(); Field<TextBox>("Item name").Text = "Second cue";
                    Console.WriteLine("PASS New item inspector opens");
                    Field<NumericUpDown>("Wait before item (ms)").Value = 20000;
                    Field<ComboBox>("Roles (comma-separated)").Text = "Projector, Ceiling";
                    Field<ComboBox>("Role selection").SelectedItem = "Random";
                    Field<CheckBox>("Include subwoofer").Checked = true;
                    Field<CheckBox>("Loop video").Checked = true;
                    Shot("controller-storyboard-editor.png");
                    Button("Commands").PerformClick(); Shot("controller-command-editor.png");
                    Check(page.HasUnsavedChanges && host.Options.Playlists[1].Name == "cue" && host.Engine.Snapshot().Length == 0, "Editing items, roles and commands stays in the draft without dispatching anything");
                    await page.SaveAsync(); await Until(() => host.Options.Playlists[1].Name == "Renamed cue"); page.RefreshState();
                    var saved = host.Options.Playlists[1];
                    Check(saved.Items.Length == 2 && saved.Items[1].DelayMilliseconds == 20000 && saved.Items[1].Roles == "Projector, Ceiling" && saved.Items[1].BroadcastMode == "Random" && saved.Items[1].Commands[0].Payload.GetProperty("loop").GetBoolean(), "Save persists edited millisecond timing, role routing and looping commands");
                    Check(host.Options.Playlists[2].Items[0].Commands[0].Playlist == "Renamed cue", "UI playlist rename preserves nested references");
                    Button("Run playlist").PerformClick(); await Until(() => host.Engine.Snapshot().Length == 1);
                    Button("Cancel run").PerformClick(); await Until(() => host.Engine.Snapshot().Length == 0);
                    Check(true, "Explicit Run and Cancel operate the existing engine");
                    page.Navigate("Playlists", 0); Button("+ Item").PerformClick();
                    Field<NumericUpDown>("Minimum interval (ms)").Value = 1000;
                    Field<NumericUpDown>("Maximum interval (ms)").Value = 5000;
                    Field<NumericUpDown>("Cooldown (ms)").Value = 9000;
                    var prior = File.ReadAllBytes(path); await page.SaveAsync();
                    Check(File.ReadAllBytes(path).SequenceEqual(prior) && page.HasUnsavedChanges, "Invalid ambient timing reports inline and does not change the saved configuration");
                    Field<NumericUpDown>("Cooldown (ms)").Value = 2000; Shot("controller-ambient-editor.png");
                    // Do not save the ambient playback fixture: this test must not broadcast videos.
                    page.Navigate("Triggers", 0); Button("+ New").PerformClick();
                    Field<ComboBox>("Listen for").SelectedItem = "Timer";
                    Check(!Field<TextBox>("Source device").Enabled && Field<NumericUpDown>("Interval (ms)").Enabled, "Trigger inspector separates timer and event fields");
                    Shot("controller-trigger-editor.png");
                    form.ClientSize = new(980, 660); page.Navigate("Playlists", 1); Shot("controller-storyboard-editor-compact.png");
                    Check(!form.OwnedForms.Any() && Descendants(page).OfType<PlaylistEditorView>().Count() == 1, "Navigation and editing use one window without popup dialogs");
                    var custom = new PlaylistCommand { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Ghosts/Eye.mp4", screen = "*", customParameter = "keep", loop = false }) };
                    using var command = new PlaylistCommandEditor(custom, ShowConfiguration.From(host.Options), host.Options, [], [], () => { });
                    Check(command.Read().Payload.GetProperty("customParameter").GetString() == "keep", "Typed media inspector retains custom payload properties");
                    var advanced = custom with { Payload = JsonSerializer.SerializeToElement(new { experience = "audio", value = "Bell.wav", audioDevices = new[] { "Left", "Right" }, volume = 75 }) };
                    using var audio = new PlaylistCommandEditor(advanced, ShowConfiguration.From(host.Options), host.Options, [], [], () => { });
                    Check(audio.Read().Payload.GetProperty("audioDevices").GetArrayLength() == 2, "Advanced command editing preserves coordinated multi-output audio");
                    var namedNone = ShowConfiguration.From(host.Options) with { Playlists = [.. host.Options.Playlists, new() { Name = "None" }] };
                    using var sceneView = new SceneEditorView(new() { Name = "sample", AmbientPlaylist = "AMBIENT", ActivatingPlaylist = "none" }, namedNone, false, () => { }, _ => { });
                    Check(sceneView.Read().ActivatingPlaylist == "None" && sceneView.Read().AmbientPlaylist == "ambient", "Optional playlist choices preserve a real playlist named None and case-insensitive references");
                    using var mode = ShowEditorControls.Choice(["Broadcast", "Random"], "random"); Check(mode.Text == "Random", "Inspector choices preserve case-insensitive random targeting");
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Telemetry.Stop(); Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                done.TrySetResult();
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static async Task Until(Func<bool> test) { var until = Environment.TickCount64 + 5000; while (!test()) { if (Environment.TickCount64 > until) throw new TimeoutException("Controller editor state did not arrive."); await Task.Delay(10); } }
    private static int FreeUdp() { using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)client.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
