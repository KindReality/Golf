namespace PhotoBooth.Networking;

// Controller-owned sampling/output settings. Experience owns only placement on its display.
public sealed record LedPerspective
{
    public string Address { get; init; } = "";
    public string DeviceMac { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public int SegmentId { get; init; }
    public int PixelCount { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FramesPerSecond { get; init; } = 30;
    public double SamplingThicknessPercent { get; init; } = 10;
    public void Validate(string kind)
    {
        WledClient.Endpoint(Address);
        if (DeviceMac.Length != 12 || !DeviceMac.All(Uri.IsHexDigit) || SegmentId is < 0 or > 255 ||
            FramesPerSecond is < 1 or > 120 || !double.IsFinite(SamplingThicknessPercent) || SamplingThicknessPercent is <= 0 or > 100)
            throw new ArgumentException("LED perspectives require a WLED MAC, segment ID, 1–120 FPS and thickness greater than zero and at most 100 percent.");
        if (kind == "pixelArray" && (PixelCount is < 1 or > 65535 || Width != 0 || Height != 0) ||
            kind == "pixelMatrix" && (Width is < 1 or > 4096 || Height is < 1 or > 4096 || (long)Width * Height > 65535 || PixelCount != Width * Height))
            throw new ArgumentException("Invalid LED array count or matrix dimensions.");
    }
}
