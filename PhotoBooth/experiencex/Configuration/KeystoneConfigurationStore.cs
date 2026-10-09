using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExperienceX;

internal sealed class KeystoneEditConflictException : Exception
{
    public KeystoneEditConflictException() : base("Configuration mode or this monitor's keystone was changed remotely; the local edit was cancelled.") { }
}

internal sealed class KeystoneConfigurationStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Json = ConfigurationSchema.Json;
    private static JsonObject Parse(string text) => JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text,
        new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject ?? throw new JsonException("Configuration must be an object.");
    private static void ValidateDocument(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new JsonException("Configuration is empty.");
        ExperienceOptions.Parse(text);
    }
    // External editors using read/merge/write should take the same .lock file with FileShare.None.
    private FileStream LockFile() => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private void Replace(string contents, bool backup)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(contents + Environment.NewLine);
                file.Write(bytes);
                file.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, backup ? path + ".bak" : null, true);
            else
                File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public bool RecoverAtStartup()
    {
        using var fileLock = LockFile();
        try
        {
            ValidateDocument(File.ReadAllText(path));
            return false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            if (!File.Exists(path + ".bak"))
                return false;
            var backup = File.ReadAllText(path + ".bak");
            ValidateDocument(backup);
            if (File.Exists(path))
                File.Copy(path, path + ".rejected-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
            Replace(backup, false);
            return true;
        }
    }
    private string Update(Func<JsonObject, bool> change)
    {
        _gate.Wait();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var fileLock = LockFile();
                    var existed = File.Exists(path);
                    // Normal in-place writers are excluded; replacements are detected by the second read.
                    using var original = existed ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
                    using var reader = original is null ? null : new StreamReader(original, Encoding.UTF8, true, 1024, true);
                    var text = reader?.ReadToEnd() ?? "{}";
                    if (existed)
                        ValidateDocument(text);
                    var root = Parse(text);
                    ExperienceOptions.Parse(root.ToJsonString());
                    if (!change(root))
                        return root.ToJsonString();
                    var updated = root.ToJsonString(Json);
                    ExperienceOptions.Parse(updated);
                    if (File.Exists(path) != existed || (existed && File.ReadAllText(path) != text))
                        throw new IOException("Configuration was replaced during the merge; retrying.");
                    Replace(updated, existed);
                    return updated;
                }
                catch (IOException) when (attempt < 9) { Thread.Sleep(35); }
            }
        }
        finally { _gate.Release(); }
    }
    public string EnsureDisplays(IReadOnlyList<string> monitors) => Update(root =>
    {
        var changed = ConfigurationSchema.Upgrade(root);
        var defaults = JsonSerializer.SerializeToNode(new ExperienceOptions())!.AsObject();
        foreach (var name in new[] { "configurationMode", "KeystoneHitDiameterPercent", "CalibrationGridEnabled", "RequestDeduplicationSeconds", "MonitorBackgrounds",
            "DeviceName", "UdpBroadcastAddress", "UdpInterfaceAddress", "UdpSendAttempts", "UdpRetryDelayMilliseconds", "RequestLifetimeSeconds" })
            if (!root.ContainsKey(name))
            {
                root[name] = defaults[name]?.DeepClone();
                changed = true;
            }
        if (root["DisplayKeystones"] is not JsonObject displays)
        {
            displays = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            root["DisplayKeystones"] = displays;
            changed = true;
        }
        var options = ExperienceOptions.Parse(root.ToJsonString());
        var backgrounds = root["MonitorBackgrounds"]!.AsObject();
        foreach (var monitor in monitors)
        {
            var backgroundKey = MonitorBackground.KeyFor(monitor, options) ?? monitor;
            if (backgrounds[backgroundKey] is not JsonObject background)
            {
                background = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                backgrounds[backgroundKey] = background;
                changed = true;
            }
            foreach (var field in JsonSerializer.SerializeToNode(new MonitorBackgroundOptions())!.AsObject())
                if (!background.ContainsKey(field.Key))
                {
                    background[field.Key] = field.Value?.DeepClone();
                    changed = true;
                }
            if (displays[monitor] is not JsonObject entry)
            {
                entry = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                displays[monitor] = entry;
                changed = true;
            }
            var seed = JsonSerializer.SerializeToNode(options.KeystoneFor(monitor))!.AsObject();
            foreach (var field in seed)
            {
                if (!entry.ContainsKey(field.Key))
                {
                    entry[field.Key] = field.Value?.DeepClone();
                    changed = true;
                }
                else if (field.Value is JsonObject point && entry[field.Key] is JsonObject existing)
                    foreach (var axis in point)
                        if (!existing.ContainsKey(axis.Key))
                        {
                            existing[axis.Key] = axis.Value?.DeepClone();
                            changed = true;
                        }
            }
        }
        return changed;
    });
    public Task<string> ToggleConfigurationModeAsync(CancellationToken cancellation = default) => Task.Run(() => Update(root =>
    {
        cancellation.ThrowIfCancellationRequested();
        var current = ExperienceOptions.Parse(root.ToJsonString());
        root["configurationMode"] = !current.ConfigurationMode;
        return true;
    }), cancellation);

    public Task<string> ToggleBackgroundTransparencyAsync(string monitor, CancellationToken cancellation = default) => Task.Run(() => Update(root =>
    {
        cancellation.ThrowIfCancellationRequested();
        ConfigurationSchema.Upgrade(root);
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var key = MonitorBackground.KeyFor(monitor, current) ?? monitor;
        if (root["MonitorBackgrounds"] is not JsonObject backgrounds)
        {
            backgrounds = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            root["MonitorBackgrounds"] = backgrounds;
        }
        if (backgrounds[key] is not JsonObject background)
        {
            background = JsonSerializer.SerializeToNode(new MonitorBackgroundOptions())!.AsObject();
            backgrounds[key] = background;
        }
        var settings = current.MonitorBackgrounds.GetValueOrDefault(key) ?? new MonitorBackgroundOptions();
        if (!background.ContainsKey("BackgroundColor"))
            background["BackgroundColor"] = settings.BackgroundColor;
        background["IsTransparent"] = !settings.IsTransparent;
        return true;
    }), cancellation);

    public Task SaveAsync(string monitor, KeystoneOptions expected, KeystoneOptions edited) => Task.Run(() => Update(root =>
    {
        edited.Validate();
        var current = ExperienceOptions.Parse(root.ToJsonString());
        if (!current.ConfigurationMode || current.KeystoneFor(monitor) != expected)
            throw new KeystoneEditConflictException();
        if (root["DisplayKeystones"] is not JsonObject displays)
        {
            displays = new JsonObject();
            root["DisplayKeystones"] = displays;
        }
        if (displays[monitor] is not JsonObject entry)
        {
            entry = new JsonObject();
            displays[monitor] = entry;
        }
        entry["Enabled"] = edited.Enabled;
        var points = KeystoneEditing.Points(edited);
        string[] names = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
        for (int i = 0; i < 4; i++)
        {
            if (entry[names[i]] is not JsonObject p)
            {
                p = new JsonObject();
                entry[names[i]] = p;
            }
            p["X"] = points[i].X;
            p["Y"] = points[i].Y;
        }
        return true;
    }));
}
