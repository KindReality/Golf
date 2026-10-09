namespace Experience;

public sealed record BlackKeyOptions
{
    public bool Enabled { get; init; } = true;
    public double Threshold { get; init; } = 0.015;
    public double Softness { get; init; } = 0.03;
    public void Validate()
    {
        if (!double.IsFinite(Threshold) || Threshold is < 0 or > 1 ||
            !double.IsFinite(Softness) || Softness is < 0 or > 1 || Threshold + Softness > 1)
            throw new ArgumentException("BlackKey threshold and softness must be finite, nonnegative, and total at most 1.");
    }
}
