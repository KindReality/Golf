namespace ExperienceX;

public sealed record LocalControllerOptions
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 54244;
    public bool MediaSyncEnabled { get; init; } = true;
    public double RetryIntervalSeconds { get; init; } = 1;
    public double MediaSyncIntervalSeconds { get; init; } = 10;
    public void Validate()
    {
        if (Port is < 1 or > 65535 || !double.IsFinite(RetryIntervalSeconds) || RetryIntervalSeconds is < 1 or > 60 ||
            !double.IsFinite(MediaSyncIntervalSeconds) || MediaSyncIntervalSeconds is < 1 or > 3600)
            throw new ArgumentException("LocalController requires a valid TCP port, a 1–60 second retry interval and a 1–3600 second media sync interval.");
    }
}
