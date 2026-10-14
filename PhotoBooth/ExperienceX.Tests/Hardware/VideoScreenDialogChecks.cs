using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Appearance;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class VideoScreenDialogChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-video-dialog-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder); Exception? failure = null;
                try
                {
                    var options = new ExperienceOptions
                    {
                        ConfigurationMode = true,
                        DisplayScreens = new() { ["test-display"] = [new() { Roles = "Right,front" }] },
                        AudioDevices = new() { ["speaker"] = new() { Roles = "Front,subwoofer" } }
                    };
                    var known = VideoScreenDialog.KnownRoles(options);
                    Check(known.Length == 3 && known.SequenceEqual(known.Order(StringComparer.OrdinalIgnoreCase)) && known.Count(r => r.Equals("front", StringComparison.OrdinalIgnoreCase)) == 1, "Creation role choices combine screen/audio roles, sort and deduplicate ignoring case");
                    var perspective = new PerspectiveSelection { ControllerId = Guid.NewGuid().ToString("N"), PerspectiveId = Guid.NewGuid().ToString("N"), ControllerName = "Test Controller", Name = "Bottom third", Rectangle = new() { Y = 66.67, Height = 33.33 } };
                    using (var dialog = new VideoScreenDialog(options.ScreensFor("test-display"), [perspective], known, "Projector") { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) })
                    {
                        dialog.Show();
                        await Task.Delay(100); // WinForms posts Shown; let the WPF dispatcher process it before checking the guard/theme.
                        Forms.TextBox Text(string name) => Descendants(dialog).OfType<Forms.TextBox>().Single(c => c.AccessibleName == name);
                        Forms.Button Button(string name) => Descendants(dialog).OfType<Forms.Button>().Single(c => c.Text == name);
                        var roles = Descendants(dialog).OfType<Forms.CheckedListBox>().Single();
                        var perspectives = Descendants(dialog).OfType<Forms.ListBox>().Single(c => c is not Forms.CheckedListBox);
                        Check(dialog.ScreenName == "screen2" && dialog.Selection is null && dialog.Roles == "" && dialog.TopMost && ConfigurationDialog.Active, "Dialog starts with a unique name, Full perspective, no roles and topmost protection");
                        Check(Button("Create screen").FlatStyle == Forms.FlatStyle.Flat && Button("Create screen").BackColor == DarkFormsTheme.Selection && dialog.BackColor == DarkFormsTheme.Background, "Creation uses the shared Controller/Experience dark Azure treatment");
                        Text("Screen name").Text = "SCREEN1";
                        Check(!Button("Create screen").Enabled, "Duplicate screen names are rejected inline before saving");
                        Text("Screen name").Text = "Lobby";
                        perspectives.SelectedIndex = 1;
                        roles.SetItemChecked(Array.FindIndex(known, r => r.Equals("front", StringComparison.OrdinalIgnoreCase)), true);
                        roles.SetItemChecked(Array.FindIndex(known, r => r.Equals("right", StringComparison.OrdinalIgnoreCase)), true);
                        Text("New role").Text = "Entry,FRONT"; Button("Add").PerformClick();
                        Check(RoleList.Parse(dialog.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["front", "Right", "Entry"]), "Multiple existing roles and a new role can be selected without duplicates");
                        dialog.PerformLayout();
                        using (var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height))
                        {
                            dialog.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                            var evidence = Path.Combine(Path.GetTempPath(), "ExperienceX-NewVideoDialog.png"); bitmap.Save(evidence); Console.WriteLine("Dialog screenshot: " + evidence);
                        }
                        Button("Create screen").PerformClick();
                        Check(dialog.DialogResult == Forms.DialogResult.OK && !ConfigurationDialog.Active, "Create closes the unified dialog and releases topmost protection");
                        var path = Path.Combine(folder, "appsettings.json"); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
                        var store = new PointConfigurationStore(path);
                        var created = await store.AddScreenAsync("test-display", perspective: dialog.Selection, name: dialog.ScreenName, roles: dialog.Roles);
                        var persisted = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
                        Check(created.Name == "Lobby" && created.DefaultName == "Lobby" && created.Perspective?.PerspectiveId == perspective.PerspectiveId && created.Points == new PointOptions() && persisted.ScreensFor("test-display").Single(s => s.Id == created.Id) == created, "Name, selected perspective, roles and full-display points persist together");
                        var before = await File.ReadAllTextAsync(path);
                        try { await store.AddScreenAsync("test-display", name: "LOBBY", roles: "Entry"); throw new Exception("Duplicate name accepted by store"); } catch (ArgumentException) { }
                        Check(await File.ReadAllTextAsync(path) == before, "Atomic creation rejects a remotely conflicting name without changing configuration");
                        using var cancelled = new VideoScreenDialog(persisted.ScreensFor("test-display"), [perspective], known, "Projector") { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) };
                        using var cancelTimer = new Forms.Timer { Interval = 100 };
                        cancelTimer.Tick += (_, _) => { cancelTimer.Stop(); Descendants(cancelled).OfType<Forms.Button>().Single(b => b.Text == "Cancel").PerformClick(); };
                        cancelTimer.Start(); cancelled.ShowDialog();
                        Check(cancelled.DialogResult == Forms.DialogResult.Cancel && !ConfigurationDialog.Active && await File.ReadAllTextAsync(path) == before, "Cancel closes without creating a screen or changing configuration");
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult();
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    private static IEnumerable<Forms.Control> Descendants(Forms.Control parent) => parent.Controls.Cast<Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
