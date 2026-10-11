using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExperienceX;

internal sealed class PointEditConflictException : Exception
{
    public PointEditConflictException() : base("Configuration mode or this screen's points were changed remotely; the local edit was cancelled.") { }
}

internal sealed class PointConfigurationStore(string path)
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
    public string EnsureDisplays(IReadOnlyList<string> displays) => Update(root =>
    {
        var changed = ConfigurationSchema.Upgrade(root);
        var defaults = JsonSerializer.SerializeToNode(new ExperienceOptions())!.AsObject();
        foreach (var name in new[] { "configurationMode", "PointHitDiameterPercent", "CalibrationGridEnabled", "RequestDeduplicationSeconds", "DisplayBackgrounds",
            "DeviceName", "UdpEventsPort", "UdpCommandsPort", "UdpBroadcastAddress", "UdpInterfaceAddress", "UdpSendAttempts", "UdpRetryDelayMilliseconds", "RequestLifetimeSeconds", "MediaDiscovery", "VideoDirectory", "AudioDirectory" })
            if (!root.ContainsKey(name))
            {
                root[name] = defaults[name]?.DeepClone();
                changed = true;
            }
        if (root["DisplayScreens"] is not JsonObject configured)
        {
            configured = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            root["DisplayScreens"] = configured;
            changed = true;
        }
        var options = ExperienceOptions.Parse(root.ToJsonString());
        var backgrounds = root["DisplayBackgrounds"]!.AsObject();
        foreach (var display in displays)
        {
            var backgroundKey = DisplayBackground.KeyFor(display, options) ?? display;
            if (backgrounds[backgroundKey] is not JsonObject background)
            {
                background = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                backgrounds[backgroundKey] = background;
                changed = true;
            }
            foreach (var field in JsonSerializer.SerializeToNode(new DisplayBackgroundOptions())!.AsObject())
                if (!background.ContainsKey(field.Key))
                {
                    background[field.Key] = field.Value?.DeepClone();
                    changed = true;
                }
            // An existing empty array means zero screens; never recreate a deliberately removed screen.
            if (!configured.ContainsKey(display))
            {
                configured[display] = JsonSerializer.SerializeToNode(new[] { new ScreenOptions { Points = options.PointsFor(display) } });
                changed = true;
            }
            foreach (var screen in configured[display]!.AsArray().OfType<JsonObject>())
            {
                var parsed = JsonSerializer.Deserialize<ScreenOptions>(screen.ToJsonString(), Json)!;
                foreach (var field in JsonSerializer.SerializeToNode(parsed)!.AsObject())
                    if (!screen.ContainsKey(field.Key))
                    {
                        screen[field.Key] = field.Value?.DeepClone();
                        changed = true;
                    }
                var points = screen["Points"]!.AsObject();
                foreach (var field in JsonSerializer.SerializeToNode(parsed.Points)!.AsObject())
                {
                    if (!points.ContainsKey(field.Key))
                    {
                        points[field.Key] = field.Value?.DeepClone();
                        changed = true;
                    }
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

    public Task<string> SetBackgroundAsync(string display, bool transparent, string? color = null, CancellationToken cancellation = default) => Task.Run(() => Update(root =>
    {
        cancellation.ThrowIfCancellationRequested();
        ConfigurationSchema.Upgrade(root);
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var key = DisplayBackground.KeyFor(display, current) ?? display;
        if (root["DisplayBackgrounds"] is not JsonObject backgrounds)
        {
            backgrounds = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            root["DisplayBackgrounds"] = backgrounds;
        }
        if (backgrounds[key] is not JsonObject background)
        {
            background = JsonSerializer.SerializeToNode(new DisplayBackgroundOptions())!.AsObject();
            backgrounds[key] = background;
        }
        var settings = current.DisplayBackgrounds.GetValueOrDefault(key) ?? new DisplayBackgroundOptions();
        if (color is not null) DisplayBackground.Parse(color);
        background["BackgroundColor"] = color ?? settings.BackgroundColor;
        background["IsTransparent"] = transparent;
        return true;
    }), cancellation);

    public Task<string> ToggleScreenTransparencyAsync(string display, string screenId, CancellationToken cancellation = default) => Task.Run(() => Update(root =>
    {
        cancellation.ThrowIfCancellationRequested();
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var screen = current.ScreensFor(display).FirstOrDefault(s => s.Id.Equals(screenId, StringComparison.OrdinalIgnoreCase));
        if (!current.ConfigurationMode || screen is null)
            throw new InvalidOperationException("Select a video screen in configuration mode before toggling chroma key.");
        if (screen.IsMat)
            throw new InvalidOperationException("Mat screens remain opaque black.");
        var entry = ScreenEntry(root, display, screenId);
        var effective = screen.EffectiveBlackKey(current);
        if (entry["BlackKey"] is not JsonObject key)
        {
            key = JsonSerializer.SerializeToNode(effective)!.AsObject();
            entry["BlackKey"] = key;
        }
        key["Enabled"] = !effective.Enabled;
        return true;
    }), cancellation);

    public Task<string> ToggleScreenSizingAsync(string display, string screenId, CancellationToken cancellation = default) => Task.Run(() => Update(root =>
    {
        cancellation.ThrowIfCancellationRequested();
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var screen = current.ScreensFor(display).FirstOrDefault(s => s.Id.Equals(screenId, StringComparison.OrdinalIgnoreCase));
        if (!current.ConfigurationMode || screen is null || screen.IsMat)
            throw new InvalidOperationException("Select a video screen in configuration mode before toggling Fit/Stretch.");
        ScreenEntry(root, display, screenId)["SizingMode"] = screen.SizingMode == "fit" ? "stretch" : "fit";
        return true;
    }), cancellation);

    public Task SaveAsync(string display, PointOptions expected, PointOptions edited, string? screenId = null) => Task.Run(() => Update(root =>
    {
        ConfigurationSchema.Upgrade(root);
        var current = ExperienceOptions.Parse(root.ToJsonString());
        if (current.ScreensFor(display).FirstOrDefault(s => s.Id == screenId)?.IsMat == true) edited.ValidatePolygon();
        else edited.Validate();
        if (!current.ConfigurationMode || current.PointsFor(display, screenId) != expected)
            throw new PointEditConflictException();
        var entry = ScreenEntry(root, display, screenId);
        var existing = entry["Points"] as JsonObject ?? new JsonObject();
        entry["Points"] = existing;
        foreach (var field in JsonSerializer.SerializeToNode(edited)!.AsObject())
            existing[field.Key] = field.Value?.DeepClone();
        return true;
    }));

    private static JsonObject ScreenEntry(JsonObject root, string display, string? id)
    {
        if (root["DisplayScreens"] is not JsonObject displays || displays[display] is not JsonArray screens)
            throw new PointEditConflictException();
        return screens.OfType<JsonObject>().FirstOrDefault(s => id is null ||
            string.Equals(s["Id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)) ?? throw new PointEditConflictException();
    }
    public Task<ScreenOptions> AddScreenAsync(string display, CancellationToken cancellation = default, string type = "video") => Task.Run(() =>
    {
        ScreenOptions? added = null;
        Update(root =>
        {
            cancellation.ThrowIfCancellationRequested();
            ConfigurationSchema.Upgrade(root);
            var current = ExperienceOptions.Parse(root.ToJsonString());
            if (!current.ConfigurationMode || root["DisplayScreens"] is not JsonObject displays || displays[display] is not JsonArray screens)
                throw new PointEditConflictException();
            added = ScreenOptions.New(current.ScreensFor(display), type);
            screens.Add(JsonSerializer.SerializeToNode(added));
            return true;
        });
        return added!;
    }, cancellation);
    public Task RenameScreenAsync(string display, string id, string expected, string name) => Task.Run(() => Update(root =>
    {
        ConfigurationSchema.Upgrade(root);
        ScreenOptions.ValidateName(name);
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var entry = ScreenEntry(root, display, id);
        if (!current.ConfigurationMode || entry["Name"]?.GetValue<string>() != expected)
            throw new PointEditConflictException();
        if (current.ScreensFor(display).Any(s => s.Id != id && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Another screen on this display already has that name.");
        if (name == expected)
            return false;
        entry["Name"] = name;
        return true;
    }));
    public Task DeleteScreenAsync(string display, ScreenOptions expected) => Task.Run(() => Update(root =>
    {
        ConfigurationSchema.Upgrade(root);
        var current = ExperienceOptions.Parse(root.ToJsonString());
        var entry = ScreenEntry(root, display, expected.Id);
        if (!current.ConfigurationMode || current.ScreensFor(display).FirstOrDefault(s => s.Id == expected.Id) != expected)
            throw new PointEditConflictException();
        root["DisplayScreens"]![display]!.AsArray().Remove(entry);
        return true;
    }));
}
