namespace ExperienceX;

public sealed record VisualizationOptions
{
    public string Theme { get; init; } = "(Standard)";
    public string ColorMap { get; init; } = "Clear Blue Water";
    public string WaveShape { get; init; } = "Random";
    public string FlowField { get; init; } = "Random";
    public string FlowFieldUnion { get; init; } = "None";
    public string Particle { get; init; } = "None";
    public string Sprite { get; init; } = "None";
    // Null preserves G-Force's per-family behavior. Zero explicitly means immediate.
    public double? TransitionSeconds { get; init; }
    public double? ChangeDelaySeconds { get; init; }
    public double? ParticleDurationSeconds { get; init; }
    public double? SpriteDurationSeconds { get; init; }
    // Empty discovers the installed Standalone. G-Force must be installed separately.
    public string? ExecutablePath
    {
        get; init;
    }

    internal void Validate()
    {
        foreach (var name in new[] { Theme, ColorMap, WaveShape, FlowField, FlowFieldUnion, Particle, Sprite })
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(char.IsControl))
                throw new ArgumentException("Visualization selections require a name, Random or None; use null only for optional timing values.");
        if (new[] { Theme, ColorMap, WaveShape, FlowField }.Any(name => name.Equals("None", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Theme, ColorMap, WaveShape and FlowField require a name or Random; None applies to unions, particles and sprites.");
        foreach (var value in new[] { TransitionSeconds, ChangeDelaySeconds, ParticleDurationSeconds, SpriteDurationSeconds })
            ValidateTime(value);
    }
    internal static void ValidateTime(double? value)
    {
        if (value is { } seconds && (!double.IsFinite(seconds) || seconds < 0 || seconds > 3600))
            throw new ArgumentException("Visualization times must be null or a finite number from 0 to 3600 seconds.");
    }
}
