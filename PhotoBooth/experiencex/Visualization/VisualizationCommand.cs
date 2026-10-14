namespace ExperienceX;

// A closed command vocabulary: UDP never carries executable G-Force code.
internal static class VisualizationCommand
{
    internal static string Family(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "theme" => "Theme", "colormap" => "ColorMap", "waveshape" => "WaveShape",
        "flowfield" => "FlowField", "flowfieldunion" => "FlowField Union",
        "particle" => "Particle", "sprite" => "Sprite",
        _ => throw new ArgumentException("visualType must be theme, colorMap, waveShape, flowField, flowFieldUnion, particle or sprite.")
    };
    internal static void Validate(ExperienceRequest request)
    {
        if (request.Value is null) throw new ArgumentException("value must be a string when supplied.");
        var family = Family(request.VisualType);
        var action = request.Command?.Trim().ToLowerInvariant();
        if (action is not ("select" or "previous" or "next" or "start" or "stop"))
            throw new ArgumentException("Visualization commands support select, previous, next, start or stop.");
        if (action is "start" or "stop" && family is not ("Particle" or "Sprite"))
            throw new ArgumentException("Only particles and sprites support start/stop.");
        if (action == "select" && family is "Particle" or "Sprite")
            throw new ArgumentException("Use start/stop for particles and sprites.");
        if (action is "select" or "start")
        {
            if (string.IsNullOrWhiteSpace(request.Value) || request.Value.Length > 512 || request.Value.Any(char.IsControl))
                throw new ArgumentException("Selecting a visualization requires a visual name, Random or None.");
            if (family is "Theme" or "ColorMap" or "WaveShape" or "FlowField" && request.Value.Equals("None", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("None applies only to unions, particles and sprites.");
        }
        else if (!string.IsNullOrEmpty(request.Value) && action != "stop")
            throw new ArgumentException("Previous/next commands do not take a value.");
        if (action == "stop" && (request.Value.Length > 512 || request.Value.Any(char.IsControl)))
            throw new ArgumentException("Invalid particle/sprite name.");
        if (action == "stop" && request.Value.Equals("Random", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Stop requires an exact effect name or an empty value for all effects.");
        VisualizationOptions.ValidateTime(request.TransitionSeconds);
        VisualizationOptions.ValidateTime(request.DelaySeconds);
        VisualizationOptions.ValidateTime(request.DurationSeconds);
        if (request.DelaySeconds is not null && (family is not ("FlowField" or "FlowField Union") || action == "stop"))
            throw new ArgumentException("delaySeconds applies only to motion-field changes.");
        if (request.DurationSeconds is not null && (family is not ("Particle" or "Sprite") || action == "stop"))
            throw new ArgumentException("durationSeconds applies only to starting particles or sprites.");
        if (family is "Particle" or "Sprite" && action != "stop" && request.TransitionSeconds is not null)
            throw new ArgumentException("Particle/sprite starts use G-Force's effect fades; transitionSeconds applies to effect stops.");
        if (request.Loop || request.PlaybackRequestId is not null || request.AudioDevice is not null || request.AudioDevices is not null ||
            request.Channel is not null || request.Volume is not null || request.Mute)
            throw new ArgumentException("Visualization commands cannot include media playback or audio options.");
        _ = ScreenSelection.Target(request);
    }
}
