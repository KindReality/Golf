namespace ExperienceX;

public sealed record RendererRecoveryOptions
{
    public bool Enabled { get; init; } = true;
    public double StallTimeoutSeconds { get; init; } = 5;
    public void Validate()
    {
        if (!double.IsFinite(StallTimeoutSeconds) || StallTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("RendererRecovery.StallTimeoutSeconds must be finite and between 1 and 3600 seconds.");
    }
}
