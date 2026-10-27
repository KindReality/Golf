namespace ExperienceX;

internal sealed class ScreenSurface(MainWindow owner, string id) : IPointSurface
{
    public string DisplayName => owner.DisplayName;
    public string ScreenId => id;
    public string ScreenName => owner.ScreenDefinition(id)?.Name ?? "";
    public int PixelWidth => owner.ClientSize.Width;
    public int PixelHeight => owner.ClientSize.Height;
    private MainWindow Owner => owner;
    private bool _selected;
    private int _point = -1;
    public void SelectScreen(bool selected)
    {
        _selected = selected;
        owner.Renderer?.ShowCalibration(_point, id, selected);
        owner.ToolsRenderer?.ShowCalibration(_point, id, selected);
    }
    public void ShowCalibration(int point)
    {
        _point = point;
        owner.Renderer?.ShowCalibration(point, id, _selected);
        owner.ToolsRenderer?.ShowCalibration(point, id, _selected);
    }
    public void FadeCalibration() { owner.Renderer?.FadeCalibration(id); owner.ToolsRenderer?.FadeCalibration(id); }
    public void ShowJoinTarget(int point) { owner.Renderer?.ShowJoinTarget(point, id); owner.ToolsRenderer?.ShowJoinTarget(point, id); }
    public void FlashCalibration(int point, bool success) { owner.Renderer?.FlashCalibration(point, success, id); owner.ToolsRenderer?.FlashCalibration(point, success, id); }
    public void FlashCalibrationPoints(int mask) { owner.Renderer?.FlashCalibrationPoints(mask, true, id); owner.ToolsRenderer?.FlashCalibrationPoints(mask, true, id); }
    public void PreviewPoints(PointOptions? value) { owner.Renderer?.PreviewPoints(value, id); owner.ToolsRenderer?.PreviewPoints(value,id); owner.PreviewLed(id,value); }
    public void PreviewPointGroup(IReadOnlyDictionary<IPointSurface, PointOptions?> values)
    {
        if (values.Keys.All(surface => surface is ScreenSurface screen && screen.Owner == owner))
        {
            var group=values.ToDictionary(entry=>entry.Key.ScreenId!,entry=>entry.Value);
            owner.Renderer?.PreviewPointGroup(group); owner.ToolsRenderer?.PreviewPointGroup(group);
            foreach(var entry in group) owner.PreviewLed(entry.Key,entry.Value);
        }
        else
        foreach (var entry in values)
            entry.Key.PreviewPoints(entry.Value);
    }
    public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => owner.Renderer?.ConfirmPointsAsync(expected, id) ?? Task.FromResult(false);
    public void ReleaseCalibrationCapture() => owner.ReleaseCalibrationCapture();
    public void MovePointerToPoint(ScreenPoint point) => owner.MovePointerToPoint(point);
}
