namespace ExperienceX;

internal interface IPointSurface
{
    string DisplayName
    {
        get;
    }
    string? ScreenId => null;
    string ScreenName => "screen1";
    void SelectScreen(bool selected)
    {
    }
    int PixelWidth
    {
        get;
    }
    int PixelHeight
    {
        get;
    }
    void ShowCalibration(int point);
    void ShowJoinTarget(int point) { }
    void FadeCalibration();
    void FlashCalibration(int point, bool success);
    void FlashCalibrationPoints(int pointMask);
    void PreviewPoints(PointOptions? value);
    void PreviewPointGroup(IReadOnlyDictionary<IPointSurface, PointOptions?> values)
    {
        foreach (var entry in values)
            entry.Key.PreviewPoints(entry.Value);
    }
    Task<bool> ConfirmCalibrationAsync(PointOptions expected);
    void ReleaseCalibrationCapture();
}
