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
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, "{\"MonitorName\":\"TEST\",\"maxPlaybackLength\":7,\"Custom\":42}");
                    await using var service = new ConfigurationService(dispatcher, path, () => ["TEST"]);
                    service.LoadInitial();
                    var migrated = JsonNode.Parse(File.ReadAllText(path))!;
                    if (migrated["ConfigurationVersion"]!.GetValue<int>() != ConfigurationSchema.CurrentVersion || migrated["Custom"]!.GetValue<int>() != 42 || service.Current.MaxVideoPlaybackLength != 7)
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
                    migrated["MonitorBackgrounds"] = new JsonObject { [@"\\.\DISPLAY9"] = "#102030" };
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Until(() => service.Current.MonitorBackgrounds.ContainsKey(@"\\.\DISPLAY9"));
                    if (Math.Abs(MonitorBackground.For(@"\\.\DISPLAY9", service.Current).G - 32 / 255f) > 0.0001)
                        throw new Exception("Monitor background did not reload from a remote edit.");
                    if (!await service.ToggleBackgroundTransparencyAsync(@"\\.\DISPLAY9") || MonitorBackground.For(@"\\.\DISPLAY9", service.Current).A != 0)
                        throw new Exception("Background shortcut did not apply transparency immediately.");
                    if (await service.ToggleBackgroundTransparencyAsync(@"\\.\DISPLAY9") ||
                        Math.Abs(MonitorBackground.For(@"\\.\DISPLAY9", service.Current).G - 32 / 255f) > 0.0001)
                        throw new Exception("Background shortcut did not restore the saved color immediately.");
                    var validBackground = service.Current;
                    migrated["MonitorBackgrounds"]![@"\\.\DISPLAY9"] = "invalid";
                    File.WriteAllText(path, migrated.ToJsonString());
                    await service.ReloadAsync();
                    if (service.Current != validBackground)
                        throw new Exception("Invalid live background replaced working configuration.");
                    migrated["MonitorBackgrounds"]![@"\\.\DISPLAY9"] = "transparent";
                    migrated["configurationMode"] = true;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Until(() => service.Current.ConfigurationMode);
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
                    migrated["UdpPort"] = 22222;
                    void RejectBinding(ExperienceOptions _) => throw new InvalidOperationException("Injected unavailable port");
                    service.Changing += RejectBinding;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await service.ReloadAsync();
                    if (service.Current != accepted)
                        throw new Exception("Rejected listener binding committed partial options.");
                    service.Changing -= RejectBinding;
                    await service.ReloadAsync();
                    if (service.Current.UdpPort != 22222)
                        throw new Exception("Corrected config was not accepted.");
                    await service.DisposeAsync();
                    var count = changes;
                    migrated["configurationMode"] = false;
                    File.WriteAllText(path, migrated.ToJsonString());
                    await Task.Delay(150);
                    if (changes != count)
                        throw new Exception("Stopped watcher delivered configuration changes.");
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
                        new KeystoneConfigurationStore(path).RecoverAtStartup();
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
