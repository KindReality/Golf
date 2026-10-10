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
    void FadeCalibration();
    void FlashCalibration(int point, bool success);
    void FlashCalibrationPoints(int pointMask);
    void PreviewPoints(PointOptions? value);
    Task<bool> ConfirmCalibrationAsync(PointOptions expected);
    void ReleaseCalibrationCapture();
}
