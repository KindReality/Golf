using ExperienceX;
using System.Text.Json.Nodes;
using System.Windows.Threading;

internal static class ConfigurationServiceChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-service-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var path = Path.Combine(folder, "appsettings.ALPHA.json");
                    File.WriteAllText(path, "{\"DisplayName\":\"TEST\",\"TransitionInSeconds\":7,\"Custom\":42}");
                    await using var service = new ConfigurationService(dispatcher, path, () => ["TEST"]);
                    service.LoadInitial();
                    var migrated = JsonNode.Parse(File.ReadAllText(path))!;
                    if (migrated["ConfigurationVersion"]!.GetValue<int>() != ConfigurationSchema.CurrentVersion || migrated["Custom"]!.GetValue<int>() != 42 || service.Current.TransitionInSeconds != 7)
                        throw new Exception("Persisted schema migration lost legacy values or unknown fields.");
                    service.StartWatching();
                    migrated["DeviceName"] = "  projection-pc  ";
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Until(() => service.Current.EffectiveDeviceName == "projection-pc");
                    if (!migrated.AsObject().ContainsKey("UdpBroadcastAddress") || !migrated.AsObject().ContainsKey("DeviceName"))
                        throw new Exception("Network settings were not added to existing configuration.");
                    int changes = 0;
                    service.Changed += (_, _) => changes++;
                    if (!await service.ToggleConfigurationModeAsync() || !service.Current.ConfigurationMode)
                        throw new Exception("Shortcut toggle did not immediately enable configuration mode.");
                    var toggled = JsonNode.Parse(File.ReadAllText(path))!;
                    if (toggled["Custom"]!.GetValue<int>() != 42 || !toggled["configurationMode"]!.GetValue<bool>())
                        throw new Exception("Shortcut toggle lost unrelated configuration or was not persisted.");
                    toggled["Custom"] = 99;
                    toggled["configurationMode"] = false;
                    File.WriteAllText(path, toggled.ToJsonString());
                    if (!await service.ToggleConfigurationModeAsync() || JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() != 99)
                        throw new Exception("Shortcut toggle did not merge the latest remote settings.");
                    if (await service.ToggleConfigurationModeAsync() || service.Current.ConfigurationMode)
                        throw new Exception("Shortcut toggle did not disable configuration mode.");
                    migrated["DisplayBackgrounds"] = new JsonObject { [@"\\.\DISPLAY9"] = "#102030" };
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Until(() => service.Current.DisplayBackgrounds.ContainsKey(@"\\.\DISPLAY9"));
                    if (Math.Abs(DisplayBackground.For(@"\\.\DISPLAY9", service.Current).G - 32 / 255f) > 0.0001)
                        throw new Exception("Display background did not reload from a remote edit.");
                    if (!await service.SetBackgroundAsync(@"\\.\DISPLAY9", true) || DisplayBackground.For(@"\\.\DISPLAY9", service.Current).A != 0)
                        throw new Exception("Background shortcut did not apply transparency immediately.");
                    if (await service.SetBackgroundAsync(@"\\.\DISPLAY9", false, "#000000") ||
                        service.Current.DisplayBackgrounds[@"\\.\DISPLAY9"].BackgroundColor != "#000000" ||
                        DisplayBackground.For(@"\\.\DISPLAY9", service.Current).A != 1)
                        throw new Exception("Background shortcut did not switch transparency to black immediately.");
                    if (await service.SetBackgroundAsync(@"\\.\DISPLAY9", false, "#FFFFFF") || DisplayBackground.For(@"\\.\DISPLAY9", service.Current).R != 1)
                        throw new Exception("White background did not apply immediately.");
                    var validBackground = service.Current;
                    migrated["DisplayBackgrounds"]![@"\\.\DISPLAY9"] = "invalid";
                    File.WriteAllText(path, migrated.ToJsonString());
                    await service.ReloadAsync();
                    if (service.Current != validBackground)
                        throw new Exception("Invalid live background replaced working configuration.");
                    migrated["DisplayBackgrounds"]![@"\\.\DISPLAY9"] = "transparent";
                    migrated["configurationMode"] = true;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Until(() => service.Current.ConfigurationMode);
                    if (await service.ToggleScreenTransparencyAsync("TEST", "screen1") || service.Current.ScreensFor("TEST")[0].BlackKey!.Enabled)
                        throw new Exception("Screen chroma key did not apply immediately.");
                    var remoteScreen = JsonNode.Parse(File.ReadAllText(path))!;
                    remoteScreen["DisplayScreens"]!["TEST"]![0]!["BlackKey"]!["Enabled"] = true;
                    File.WriteAllText(path, remoteScreen.ToJsonString());
                    await Until(() => service.Current.ScreensFor("TEST")[0].BlackKey!.Enabled);
                    var accepted = service.Current;
                    File.WriteAllText(path, "{");
                    await service.ReloadAsync();
                    if (service.Current != accepted || File.ReadAllText(path) != "{")
                        throw new Exception("Invalid live edit replaced working settings or was overwritten.");
                    try
                    {
                        await service.ToggleConfigurationModeAsync();
                        throw new Exception("Shortcut toggle accepted invalid configuration.");
                    }
                    catch (System.Text.Json.JsonException) { }
                    if (File.ReadAllText(path) != "{" || service.Current != accepted)
                        throw new Exception("Shortcut toggle overwrote an invalid remote edit.");
                    migrated["UdpCommandsPort"] = 22222;
                    void RejectBinding(ExperienceOptions _) => throw new InvalidOperationException("Injected unavailable port");
                    service.Changing += RejectBinding;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await service.ReloadAsync();
                    if (service.Current != accepted)
                        throw new Exception("Rejected listener binding committed partial options.");
                    service.Changing -= RejectBinding;
                    await service.ReloadAsync();
                    if (service.Current.UdpCommandsPort != 22222)
                        throw new Exception("Corrected config was not accepted.");
                    var decoding = JsonNode.Parse(File.ReadAllText(path))!;
                    decoding["SoftwareVideoDecoding"] = true;
                    decoding["TransitionInSeconds"] = 2;
                    File.WriteAllText(path, decoding.ToJsonString());
                    await service.ReloadAsync();
                    if (service.Current.SoftwareVideoDecoding || service.Current.TransitionInSeconds != 2 ||
                        !JsonNode.Parse(File.ReadAllText(path))!["SoftwareVideoDecoding"]!.GetValue<bool>())
                        throw new Exception("Decode mode changed without restart, lost the requested setting, or blocked unrelated live configuration.");
                    await service.DisposeAsync();
                    var count = changes;
                    migrated["configurationMode"] = false;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Task.Delay(150);
                    if (changes != count)
                        throw new Exception("Stopped watcher delivered configuration changes.");
                    File.WriteAllText(path, decoding.ToJsonString());
                    await using (var restarted = new ConfigurationService(dispatcher,path,()=>["TEST"]))
                    {
                        restarted.LoadInitial();
                        if (!restarted.Current.SoftwareVideoDecoding) throw new Exception("Restart did not apply software decode mode.");
                    }
                    Console.WriteLine("Passed decoding-mode restart requirement while preserving other live settings and the requested configuration.");
                    var future = "{\"ConfigurationVersion\":999}";
                    try
                    {
                        ExperienceOptions.Parse(future);
                        throw new Exception("Future schema version was accepted.");
                    }
                    catch (NotSupportedException) { }
                    File.WriteAllText(path, future);
                    try
                    {
                        new PointConfigurationStore(path).RecoverAtStartup();
                        throw new Exception("Future configuration was silently downgraded.");
                    }
                    catch (NotSupportedException) { }
                    if (File.ReadAllText(path) != future)
                        throw new Exception("Future configuration was overwritten.");
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Passed versioned configuration migration, real file watching, invalid edit retention, apply rejection and watcher shutdown checks.");
    }

    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Configuration watcher did not apply the change.");
            await Task.Delay(20);
        }
    }
}
