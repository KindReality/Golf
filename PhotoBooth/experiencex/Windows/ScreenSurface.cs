namespace ExperienceX;

internal sealed class ScreenSurface(MainWindow owner, string id) : IPointSurface
{
    public string DisplayName => owner.DisplayName;
    public string ScreenId => id;
    public string ScreenName => owner.ScreenDefinition(id)?.Name ?? "";
    public int PixelWidth => owner.ClientSize.Width;
    public int PixelHeight => owner.ClientSize.Height;
    private bool _selected;
    private int _point = -1;
    public void SelectScreen(bool selected)
    {
        _selected = selected;
        owner.Renderer?.ShowCalibration(_point, id, selected);
    }
    public void ShowCalibration(int point)
    {
        _point = point;
        owner.Renderer?.ShowCalibration(point, id, _selected);
    }
    public void FadeCalibration() => owner.Renderer?.FadeCalibration(id);
    public void FlashCalibration(int point, bool success) => owner.Renderer?.FlashCalibration(point, success, id);
    public void FlashCalibrationPoints(int mask) => owner.Renderer?.FlashCalibrationPoints(mask, true, id);
    public void PreviewPoints(PointOptions? value) => owner.Renderer?.PreviewPoints(value, id);
    public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => owner.Renderer?.ConfirmPointsAsync(expected, id) ?? Task.FromResult(false);
    public void ReleaseCalibrationCapture() => owner.ReleaseCalibrationCapture();
}
