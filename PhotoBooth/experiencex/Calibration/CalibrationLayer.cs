using System.Diagnostics;
using System.Drawing;

namespace ExperienceX;

internal sealed class CalibrationLayer(Func<double>? clock = null) : IDisposable
{
    private readonly CalibrationTexture _texture = new();
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private long _animationFrame = -1;
    private bool _valid;
    internal int Rebuilds { get; private set; }
    internal void Draw(GraphicsResources graphics, bool changed, bool visible, Action draw)
    {
        if (!visible) { _valid = false; _texture.Dispose(); return; }
        var resized = _texture.Ensure(graphics, graphics.Width, graphics.Height);
        var frame = (long)Math.Floor(_clock() * 30);
        if (!_valid || resized || changed || frame != _animationFrame)
        {
            _valid = false;
            using var operation = graphics.Operation("RefreshCalibrationLayer");
            _texture.Render(graphics, draw);
            _animationFrame = frame; _valid = true; Rebuilds++;
        }
        using var composite = graphics.Operation("CompositeCalibrationLayer");
        _texture.Composite(graphics, new Rectangle(0, 0, graphics.Width, graphics.Height));
    }
    public void Dispose() => _texture.Dispose();
}
