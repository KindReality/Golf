namespace ExperienceX;

// State belongs to one owned visualizer. Commands and configuration apply under its operation gate.
internal sealed class VisualizationControls(GForceControlClient client)
{
    private readonly Dictionary<string, string[]> _catalog = new(StringComparer.Ordinal);
    private string _field = "", _union = "";
    private VisualizationOptions? _defaults;
    internal IReadOnlyDictionary<string, string> Selections => _selections;
    private readonly Dictionary<string, string> _selections = new();

    internal async Task ApplyAsync(VisualizationOptions options, CancellationToken token)
    {
        options.Validate();
        // Check every named default before changing the running visualization.
        foreach (var (family, name) in new[] { ("Theme", options.Theme), ("ColorMap", options.ColorMap), ("WaveShape", options.WaveShape),
            ("FlowField", options.FlowField), ("FlowField Union", options.FlowFieldUnion), ("Particle", options.Particle), ("Sprite", options.Sprite) })
            if (!name.Equals("Random", StringComparison.OrdinalIgnoreCase) && !name.Equals("None", StringComparison.OrdinalIgnoreCase))
                _ = await NameAsync(family, name, token);
        var previous = _defaults;
        var full = previous is null || previous.Theme != options.Theme;
        await client.DisableAutomaticChangesAsync(token);
        if (full) await SelectAsync("Theme", options.Theme, options, null, null, null, token);
        if (full || previous!.ColorMap != options.ColorMap) await SelectAsync("ColorMap", options.ColorMap, options, null, null, null, token);
        if (full || previous!.WaveShape != options.WaveShape) await SelectAsync("WaveShape", options.WaveShape, options, null, null, null, token);
        // Select both motion parameters in one operation, avoiding a temporary mismatched field/union.
        if (full || previous!.FlowField != options.FlowField || previous.FlowFieldUnion != options.FlowFieldUnion)
        {
            if (full || previous!.FlowField != options.FlowField) _field = await NameAsync("FlowField", options.FlowField, token);
            if (full || previous!.FlowFieldUnion != options.FlowFieldUnion) _union = await NameAsync("FlowField Union", options.FlowFieldUnion, token);
            await client.SetFlowAsync(_field, _union, options.TransitionSeconds, options.ChangeDelaySeconds, token);
        }
        _selections["FlowField"] = _field; _selections["FlowField Union"] = _union;
        if (full || previous!.Particle != options.Particle) await SelectAsync("Particle", options.Particle, options, null, null, null, token);
        if (full || previous!.Sprite != options.Sprite) await SelectAsync("Sprite", options.Sprite, options, null, null, null, token);
        await client.DisableAutomaticChangesAsync(token);
        _defaults = options;
    }

    internal async Task ExecuteAsync(ExperienceRequest request, VisualizationOptions options, CancellationToken token)
    {
        VisualizationCommand.Validate(request);
        var family = VisualizationCommand.Family(request.VisualType);
        var action = request.Command!.Trim().ToLowerInvariant();
        var transition = request.TransitionSeconds ?? options.TransitionSeconds;
        if (action == "stop")
        {
            var name = string.IsNullOrEmpty(request.Value) || request.Value.Equals("None", StringComparison.OrdinalIgnoreCase) ? null :
                await NameAsync(family, request.Value, token);
            await client.StopEffectsAsync(family, name, transition, token);
            _selections[family] = "";
        }
        else
        {
            var name = request.Value;
            if (action is "next" or "previous")
            {
                var names = await CatalogAsync(family, token);
                var current = family == "FlowField" ? _field : family == "FlowField Union" ? _union :
                    _selections.TryGetValue(family, out var selected) ? selected : await client.CurrentAsync(family, token);
                var index = Array.FindIndex(names, name => name.Equals(current, StringComparison.OrdinalIgnoreCase));
                var direction = action == "next" ? 1 : -1;
                name = names[index < 0 ? direction > 0 ? 0 : names.Length - 1 : (index + direction + names.Length) % names.Length];
            }
            await SelectAsync(family, name, options, request.TransitionSeconds, request.DelaySeconds, request.DurationSeconds, token);
        }
        await client.EnsureAutomaticChangesOffAsync(token);
    }

    private async Task SelectAsync(string family, string value, VisualizationOptions options, double? transition, double? delay, double? duration, CancellationToken token)
    {
        var name = await NameAsync(family, value, token);
        transition ??= options.TransitionSeconds;
        if (family == "Theme")
        {
            await client.ActivateThemeAsync(name, token);
            await client.DisableAutomaticChangesAsync(token);
            var applied = await client.GetActiveThemeAsync(token);
            if (!applied.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"G-Force did not activate theme {name}.");
        }
        else if (family is "FlowField" or "FlowField Union")
        {
            if (family == "FlowField") _field = name; else _union = name;
            if (string.IsNullOrEmpty(_field)) _field = await client.CurrentAsync("FlowField", token);
            if (string.IsNullOrEmpty(_field) || _field == "(None)") throw new InvalidOperationException("No motion field is active; select one first.");
            await client.SetFlowAsync(_field, _union, transition, delay ?? options.ChangeDelaySeconds, token);
        }
        else if (name == "") await client.StopEffectsAsync(family, null, transition, token);
        else if (family is "Particle" or "Sprite")
            await client.StartEffectAsync(family, name, duration ?? (family == "Particle" ? options.ParticleDurationSeconds : options.SpriteDurationSeconds), token);
        else await client.SelectAsync(family, name, transition, token);
        _selections[family] = name;
    }
    private async Task<string[]> CatalogAsync(string family, CancellationToken token)
    {
        if (!_catalog.TryGetValue(family, out var names)) _catalog[family] = names = await client.NamesAsync(family, token);
        return names.Length > 0 ? names : throw new InvalidOperationException($"G-Force has no available {family} items.");
    }
    private async Task<string> NameAsync(string family, string name, CancellationToken token)
    {
        if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return "";
        if (name.Equals("Random", StringComparison.OrdinalIgnoreCase))
        {
            var choices = await client.NamesAsync(family, token, all: false);
            if (choices.Length == 0) throw new InvalidOperationException($"G-Force has no {family} choices in this theme.");
            return choices[Random.Shared.Next(choices.Length)];
        }
        var names = await CatalogAsync(family, token);
        return names.FirstOrDefault(value => value.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException($"Unknown G-Force {family}: {name}.");
    }
}
