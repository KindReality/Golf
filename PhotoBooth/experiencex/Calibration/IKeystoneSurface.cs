namespace ExperienceX;

internal interface IKeystoneSurface
{
    string MonitorName
    {
        get;
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
    void PreviewKeystone(KeystoneOptions? value);
    Task<bool> ConfirmCalibrationAsync(KeystoneOptions expected);
    void ReleaseCalibrationCapture();
}
