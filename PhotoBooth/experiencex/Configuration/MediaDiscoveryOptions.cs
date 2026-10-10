namespace ExperienceX;

public sealed record MediaDiscoveryOptions
{
    public bool Enabled { get; init; } = true;
    public int Attempts { get; init; } = 3;
    public double RetryIntervalSeconds { get; init; } = 5;
    public void Validate()
    {
        if (Attempts is < 1 or > 10 || !double.IsFinite(RetryIntervalSeconds) || RetryIntervalSeconds is < 1 or > 60)
            throw new ArgumentException("Media discovery requires 1–10 attempts and a retry interval of 1–60 seconds.");
    }
}
