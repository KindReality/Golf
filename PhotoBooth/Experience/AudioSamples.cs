namespace Experience;

public static class AudioSamples
{
    public static void Route(ReadOnlySpan<float> input, Span<float> output, int? channel)
    {
        if (channel is <= 0 || channel > output.Length) throw new ArgumentOutOfRangeException(nameof(channel));
        if (input.Length == 0 || output.Length == 0) throw new ArgumentException("Audio requires channels.");
        output.Clear();
        if (channel.HasValue)
        {
            float sum = 0;
            foreach (var sample in input) sum += sample;
            output[channel.Value - 1] = sum / input.Length;
        }
        else if (input.Length == 1 && output.Length >= 2)
        {
            output[0] = output[1] = input[0];
        }
        else
        {
            if (input.Length > output.Length) throw new ArgumentException("Source layout exceeds the output layout; select a channel explicitly.");
            input.CopyTo(output);
        }
    }

    public static double Gain(double position, double end, double fadeIn, double fadeOut)
    {
        if (position >= end) return 0;
        var incoming = fadeIn <= 0 ? 1 : 1 - Math.Pow(1 - Math.Clamp(position / fadeIn, 0, 1), 3);
        var remaining = Math.Max(0, end - position);
        var outgoing = fadeOut <= 0 ? 1 : 1 - Math.Pow(1 - Math.Clamp(remaining / fadeOut, 0, 1), 3);
        return Math.Min(incoming, outgoing);
    }
}
