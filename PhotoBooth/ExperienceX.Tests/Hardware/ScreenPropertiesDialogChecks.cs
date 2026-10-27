using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Appearance;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class ScreenPropertiesDialogChecks
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
                    var known = ScreenPropertiesDialog.KnownRoles(options);
                    Check(known.Length == 3 && known.SequenceEqual(known.Order(StringComparer.OrdinalIgnoreCase)) && known.Count(r => r.Equals("front", StringComparison.OrdinalIgnoreCase)) == 1, "Creation role choices combine screen/audio roles, sort and deduplicate ignoring case");
                    var perspective = new PerspectiveSelection { ControllerId = Guid.NewGuid().ToString("N"), PerspectiveId = Guid.NewGuid().ToString("N"), ControllerName = "Test Controller", Name = "Bottom third", Rectangle = new() { Y = 66.67, Height = 33.33 } };
                    using (var dialog = new ScreenPropertiesDialog(options.ScreensFor("test-display"), [perspective], known, "Projector") { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) })
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
                        Check(created.Name == "Lobby" && created.DefaultName == "Lobby" && created.Perspective?.PerspectiveId == perspective.PerspectiveId && created.Points.TopLeft == new ScreenPoint(1d / 3, 1d / 3) && created.Points.BottomRight == new ScreenPoint(2d / 3, 2d / 3) && persisted.ScreensFor("test-display").Single(s => s.Id == created.Id) == created, "Name, selected perspective, roles and centered second-screen points persist together");
                        var before = await File.ReadAllTextAsync(path);
                        try { await store.AddScreenAsync("test-display", name: "LOBBY", roles: "Entry"); throw new Exception("Duplicate name accepted by store"); } catch (ArgumentException) { }
                        Check(await File.ReadAllTextAsync(path) == before, "Atomic creation rejects a remotely conflicting name without changing configuration");
                        using var cancelled = new ScreenPropertiesDialog(persisted.ScreensFor("test-display"), [perspective], known, "Projector") { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) };
                        using var cancelTimer = new Forms.Timer { Interval = 100 };
                        cancelTimer.Tick += (_, _) => { cancelTimer.Stop(); Descendants(cancelled).OfType<Forms.Button>().Single(b => b.Text == "Cancel").PerformClick(); };
                        cancelTimer.Start(); cancelled.ShowDialog();
                        Check(cancelled.DialogResult == Forms.DialogResult.Cancel && !ConfigurationDialog.Active && await File.ReadAllTextAsync(path) == before, "Cancel closes without creating a screen or changing configuration");
                    }
                    await CheckEditing(folder, perspective);
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
    private static async Task CheckEditing(string folder, PerspectiveSelection perspective)
    {
        var path = Path.Combine(folder, "appsettings.json");
        var options = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
        var original = options.ScreensFor("test-display").Single(s => s.Name == "Lobby") with
            { MediaFile = "Ghosts/Eye.mp4", Brightness = 65, Framing = new() { Zoom = 2 } };
        var mat = new ScreenOptions { Id = "mat", Name = "mat1", DefaultName = "mat1", Type = "mat" };
        options = options with { DisplayScreens = new() { ["test-display"] = [options.ScreensFor("test-display")[0], original, mat] } };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
        var store = new PointConfigurationStore(path);
        using (var dialog = new ScreenPropertiesDialog(options.ScreensFor("test-display"), [perspective], [], "Projector", original)
            { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) })
        {
            dialog.Show(); await Task.Delay(50);
            var name = Descendants(dialog).OfType<Forms.TextBox>().Single(c => c.AccessibleName == "Screen name");
            var roles = Descendants(dialog).OfType<Forms.CheckedListBox>().Single();
            var save = Descendants(dialog).OfType<Forms.Button>().Single(b => b.Text == "Save changes");
            Check(name.Text == original.Name && dialog.Selection == perspective && RoleList.Parse(dialog.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(RoleList.Parse(original.Roles)) && save.Enabled,
                "Editing preloads the current name, perspective and assigned roles even when no other role choices are available.");
            using (var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                var evidence = Path.Combine(Path.GetTempPath(), "ExperienceX-ScreenProperties.png"); bitmap.Save(evidence); Console.WriteLine("Properties screenshot: " + evidence);
            }
            name.Text = "screen1"; Check(!save.Enabled, "Editing rejects another screen's name.");
            name.Text = "Stage";
            for (var i = 0; i < roles.Items.Count; i++) roles.SetItemChecked(i, false);
            Descendants(dialog).OfType<Forms.TextBox>().Single(c => c.AccessibleName == "New role").Text = "Entry,entry";
            Descendants(dialog).OfType<Forms.Button>().Single(b => b.Text == "Add").PerformClick();
            Descendants(dialog).OfType<Forms.ListBox>().Single(c => c is not Forms.CheckedListBox).SelectedIndex = 0;
            await Task.Delay(20); save.PerformClick();
            Check(dialog.DialogResult == Forms.DialogResult.OK, "Save accepts the screen's edited properties.");
            await store.SaveScreenPropertiesAsync("test-display", original, dialog.ScreenName, dialog.Roles, dialog.Selection, original.MediaFile);
        }
        var saved = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
        var screen = saved.ScreensFor("test-display").Single(s => s.Id == original.Id);
        Check(screen == original with { Name = "Stage", Roles = "Entry", Perspective = null } && saved.ScreensFor("test-display").Select(s => s.Id).SequenceEqual(options.ScreensFor("test-display").Select(s => s.Id)),
            "Name, roles and perspective save together without changing points, brightness, media binding, framing, ID or layer order.");
        var before = await File.ReadAllTextAsync(path);
        using (var cancel = new ScreenPropertiesDialog(saved.ScreensFor("test-display"), [perspective], [], "Projector", screen)
            { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) })
        {
            cancel.Show(); await Task.Delay(20);
            Descendants(cancel).OfType<Forms.TextBox>().Single(c => c.AccessibleName == "Screen name").Text = "Discard";
            Descendants(cancel).OfType<Forms.Button>().Single(b => b.Text == "Cancel").PerformClick();
            Check(cancel.DialogResult == Forms.DialogResult.Cancel && await File.ReadAllTextAsync(path) == before, "Cancel discards edits to an existing screen.");
        }
        try { await store.SaveScreenPropertiesAsync("test-display", original, "Stale", "", null, original.MediaFile); throw new Exception("Stale edit accepted."); }
        catch (PointEditConflictException) { }
        Check(await File.ReadAllTextAsync(path) == before, "A stale dialog cannot overwrite subsequent remote changes.");
        using (var dialog = new ScreenPropertiesDialog(saved.ScreensFor("test-display"), [perspective], [], "Projector", mat)
            { StartPosition = Forms.FormStartPosition.Manual, Location = new(-32000, -32000) })
        {
            dialog.Show(); await Task.Delay(20);
            Check(!Descendants(dialog).OfType<Forms.ListBox>().Single(c => c is not Forms.CheckedListBox).Visible && dialog.ScreenName == "mat1", "Mats share the properties dialog without a video perspective field.");
            dialog.Close();
        }
        await store.SaveScreenPropertiesAsync("test-display", mat, "Mask", "Stage", null, null);
        await store.ToggleScreenLockAsync("test-display", screen.Id);
        var locked = ExperienceOptions.Parse(await File.ReadAllTextAsync(path)).ScreensFor("test-display").Single(s => s.Id == screen.Id);
        try { await store.SaveScreenPropertiesAsync("test-display", locked, "Locked edit", "Other", null, locked.MediaFile); throw new Exception("Locked edit accepted."); }
        catch (InvalidOperationException) { }
        Check(ExperienceOptions.Parse(await File.ReadAllTextAsync(path)).ScreensFor("test-display").Single(s => s.Id == mat.Id).Name == "Mask", "Mat properties persist; locked screens reject property changes.");
    }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
