namespace ExperienceX;

internal sealed class RendererRecovery
{
    private static readonly int[] Delays = [1, 2, 5, 10, 30];
    public int Attempts
    {
        get; private set;
    }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MaxValue;
    public void Failed(DateTimeOffset now)
    {
        NextAttempt = now.AddSeconds(Delays[Math.Min(Attempts, Delays.Length - 1)]);
    }
    public bool Due(DateTimeOffset now) => now >= NextAttempt;
    public void Begin()
    {
        Attempts++;
        NextAttempt = DateTimeOffset.MaxValue;
    }
    public void Ready()
    {
        Attempts = 0;
        NextAttempt = DateTimeOffset.MaxValue;
    }
}
