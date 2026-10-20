namespace Controller.Configuration;

internal sealed record PeerDiscoveryOptions
{
    public bool Enabled { get; init; } = true;
    public double MediaSyncIntervalSeconds { get; init; } = 10;
    public void Validate()
    {
        if (!double.IsFinite(MediaSyncIntervalSeconds) || MediaSyncIntervalSeconds is < 1 or > 3600)
            throw new ArgumentException("PeerDiscovery.MediaSyncIntervalSeconds must be between 1 and 3600.");
    }
}
