using PhotoBooth.Networking;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using I = System.Windows.Input;
using S = System.Windows.Shapes;

namespace Controller.Windows;

// Media is a disposable preview reference. Perspective data contains only normalized rectangles.
internal sealed class PerspectivePreview : C.Grid
{
    private readonly C.MediaElement _video = new() { LoadedBehavior = C.MediaState.Manual, UnloadedBehavior = C.MediaState.Manual, IsMuted = true, Stretch = M.Stretch.Uniform };
    private readonly C.Image _image = new() { Stretch = M.Stretch.Uniform, IsHitTestVisible = false };
    private readonly C.Canvas _overlay = new() { Background = M.Brushes.Transparent };
    private PerspectiveDefinition[] _perspectives = [];
    private PerspectiveDefinition? _original;
    private W.Point _start;
    private int _handle = -1, _cycle = -1;
    private string _hits = "";
    private double _mediaWidth = 1920, _mediaHeight = 1080;
    private bool _playing;
    private string? _previewPath;
    private int _loops;
    private long _flashUntil;
    private bool _flashSuccess;
    private readonly System.Windows.Threading.DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(50) };
    public string? SelectedId { get; private set; }
    public double HitDiameterPercent { get; set; } = 5;
    public bool Editing => _original is not null;
    public bool Playing => _playing;
    public double Duration => _video.NaturalDuration.HasTimeSpan ? _video.NaturalDuration.TimeSpan.TotalSeconds : 0;
    public double Position => _video.Position.TotalSeconds;
    public Func<Keys, bool>? Shortcut { get; set; }
    public event Action<string?>? Selected;
    public event Action? RenameRequested;
    public event Action<PerspectiveDefinition, PerspectiveDefinition>? Completed;
    public event Action<string>? Failed;
    public PerspectivePreview()
    {
        Focusable = true;
        PreviewKeyDown += (_, e) =>
        {
            var key = (Keys)I.KeyInterop.VirtualKeyFromKey(e.Key == I.Key.System ? e.SystemKey : e.Key);
            if ((I.Keyboard.Modifiers & I.ModifierKeys.Shift) != 0) key |= Keys.Shift;
            if ((I.Keyboard.Modifiers & I.ModifierKeys.Control) != 0) key |= Keys.Control;
            if ((I.Keyboard.Modifiers & I.ModifierKeys.Alt) != 0) key |= Keys.Alt;
            if (Shortcut?.Invoke(key) == true) e.Handled = true;
        };
        Background = new M.SolidColorBrush(M.Color.FromRgb(9, 15, 23));
        Children.Add(_video); Children.Add(_image); Children.Add(_overlay);
        _video.MediaOpened += (_, _) =>
        {
            _mediaWidth = Math.Max(1, _video.NaturalVideoWidth); _mediaHeight = Math.Max(1, _video.NaturalVideoHeight); Draw();
            PhotoBooth.Diagnostics.Telemetry.Info("ControllerPreviewOpened",new{Path=_previewPath,Width=_mediaWidth,Height=_mediaHeight,DurationSeconds=Duration,Muted=true,Loop=true});
        };
        _video.MediaEnded += (_, _) =>
        {
            _video.Position = TimeSpan.Zero;
            if (_playing) { _video.Play(); _loops++; PhotoBooth.Diagnostics.Telemetry.Info("ControllerPreviewLooped",new{Path=_previewPath,Count=_loops}); }
        };
        _video.MediaFailed += (_, e) =>
        {
            _playing = false;
            PhotoBooth.Diagnostics.Telemetry.Error("ControllerPreviewFailed",e.ErrorException,new{Path=_previewPath});
            Failed?.Invoke(e.ErrorException.Message);
        };
        SizeChanged += (_, _) => Draw();
        _overlay.MouseLeftButtonDown += Down;
        _overlay.MouseMove += Move;
        _overlay.MouseLeftButtonUp += Up;
        _overlay.LostMouseCapture += (_, _) => { if (_original is not null) CancelEdit(); };
        _pulse.Tick += (_, _) => Draw(); _pulse.Start();
    }
    public void SetPerspectives(PerspectiveDefinition[] perspectives, string? selected = null)
    {
        CancelEdit(); _perspectives = perspectives;
        SelectedId = perspectives.Any(p => p.Id == selected) ? selected : perspectives.FirstOrDefault()?.Id;
        Draw();
    }
    public void Select(string? id) { SelectedId = id; Selected?.Invoke(id); Draw(); }
    public void Open(string path)
    {
        _previewPath=path;_loops=0;
        _video.Close(); _video.Source=null; _image.Source = null;
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
        {
            var image = new M.Imaging.BitmapImage(); image.BeginInit(); image.CacheOption = M.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            _image.Source = image; _mediaWidth = image.PixelWidth; _mediaHeight = image.PixelHeight; _playing = false;
        }
        else { _video.Source = new Uri(path); _video.Play(); _playing = true; }
        Draw();
    }
    public void TogglePlay()
    {
        if (_video.Source is null) return;
        if (_playing) _video.Pause(); else _video.Play(); _playing = !_playing;
        PhotoBooth.Diagnostics.Telemetry.Info("ControllerPreviewPlaybackChanged",new{Path=_previewPath,Playing=_playing,PositionSeconds=Position});
    }
    public void Seek(double seconds)
    {
        if (Duration <= 0) return;
        _video.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration));
        PhotoBooth.Diagnostics.Telemetry.Info("ControllerPreviewSeeked",new{Path=_previewPath,PositionSeconds=Position});
    }
    private W.Rect MediaArea
    {
        get { var scale = Math.Min(ActualWidth / _mediaWidth, ActualHeight / _mediaHeight); var w = _mediaWidth * scale; var h = _mediaHeight * scale; return new((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h); }
    }
    private W.Rect Rectangle(PerspectiveRectangle p)
    { var a = MediaArea; return new(a.X + p.X / 100 * a.Width, a.Y + p.Y / 100 * a.Height, p.Width / 100 * a.Width, p.Height / 100 * a.Height); }
    private static W.Point[] Corners(W.Rect r) => [r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight];
    private void Draw()
    {
        _overlay.Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var pulse = .72 + .28 * Math.Sin(Environment.TickCount64 / 260.0);
        foreach (var p in _perspectives)
        {
            var selected = p.Id == SelectedId; var r = Rectangle(p.Rectangle);
            var shape = new S.Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                StrokeThickness = 1.5,
                Stroke = new M.SolidColorBrush(M.Color.FromArgb(160, 0, 166, 255)),
                Fill = new M.SolidColorBrush(M.Color.FromArgb(selected ? (byte)41 : (byte)10, 0, 166, 255)),
                IsHitTestVisible = false
            };
            C.Canvas.SetLeft(shape, r.X); C.Canvas.SetTop(shape, r.Y); _overlay.Children.Add(shape);
            for (int i = 0; i < 4; i++)
            {
                var point = Corners(r)[i];
                var azure = selected && i == (_handle < 0 ? 0 : _handle);
                var color = azure ? M.Color.FromRgb(0, 166, 255) : M.Color.FromRgb(255, 67, 83);
                var orb = new S.Ellipse
                {
                    Width = 13 + 4 * pulse,
                    Height = 13 + 4 * pulse,
                    Opacity = .6 + .25 * pulse,
                    Fill = new M.RadialGradientBrush(M.Colors.White, color),
                    IsHitTestVisible = false,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 14, ShadowDepth = 0, Opacity = .7 }
                };
                C.Canvas.SetLeft(orb, point.X - orb.Width / 2); C.Canvas.SetTop(orb, point.Y - orb.Height / 2); _overlay.Children.Add(orb);
            }
            if (selected)
            {
                var label = new C.TextBlock
                {
                    Text = p.Name,
                    Tag = p.Id,
                    FontFamily = new M.FontFamily("Segoe UI"),
                    FontSize = 22,
                    Foreground = new M.SolidColorBrush(M.Color.FromArgb(230, 25, 130, 230)),
                    Cursor = I.Cursors.IBeam
                };
                label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
                C.Canvas.SetLeft(label, r.X + (r.Width - label.DesiredSize.Width) / 2); C.Canvas.SetTop(label, r.Y + (r.Height - label.DesiredSize.Height) / 2); _overlay.Children.Add(label);
            }
        }
        if (Environment.TickCount64 < _flashUntil)
            _overlay.Children.Add(new S.Rectangle
            {
                Width = ActualWidth,
                Height = ActualHeight,
                Fill = _flashSuccess ? M.Brushes.White : M.Brushes.Red,
                IsHitTestVisible = false,
                Opacity = .3 * (_flashUntil - Environment.TickCount64) / 250.0
            });
    }
    private void Down(object sender, I.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is C.TextBlock { Tag: string id }) { Select(id); RenameRequested?.Invoke(); e.Handled = true; return; }
        Focus();
        var pointer = e.GetPosition(_overlay); var a = MediaArea;
        if (a.Width <= 0 || a.Height <= 0) return;
        var radius = HitDiameterPercent / 200 * Math.Sqrt(a.Width * a.Width + a.Height * a.Height);
        var candidates = new List<(PerspectiveDefinition P, int H)>();
        foreach (var p in _perspectives)
        { var corners = Corners(Rectangle(p.Rectangle)); for (int i = 0; i < 4; i++) if ((corners[i] - pointer).Length <= radius) candidates.Add((p, i)); }
        if (candidates.Count == 0) foreach (var p in _perspectives) if (Rectangle(p.Rectangle).Contains(pointer)) candidates.Add((p, -1));
        if (candidates.Count == 0) { Select(null); return; }
        var hits = string.Join("|", candidates.Select(c => c.P.Id + ":" + c.H));
        _cycle = hits == _hits ? (_cycle + 1) % candidates.Count : 0; _hits = hits;
        var target = candidates[_cycle]; SelectedId = target.P.Id; _handle = target.H; _original = target.P; _start = pointer;
        _overlay.CaptureMouse(); Selected?.Invoke(SelectedId); Draw(); e.Handled = true;
    }
    private void Move(object sender, I.MouseEventArgs e)
    {
        if (_original is null || e.LeftButton != I.MouseButtonState.Pressed) return;
        var a = MediaArea; var delta = e.GetPosition(_overlay) - _start;
        var dx = delta.X / a.Width * 100; var dy = delta.Y / a.Height * 100; var r = _original.Rectangle;
        double left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;
        if (_handle < 0) { left = Math.Clamp(left + dx, 0, 100 - r.Width); top = Math.Clamp(top + dy, 0, 100 - r.Height); right = left + r.Width; bottom = top + r.Height; }
        else
        {
            if (_handle is 0 or 2) left = Math.Clamp(left + dx, 0, right - Math.Min(.1,r.Width)); else right = Math.Clamp(right + dx, left + Math.Min(.1,r.Width), 100);
            if (_handle is 0 or 1) top = Math.Clamp(top + dy, 0, bottom - Math.Min(.1,r.Height)); else bottom = Math.Clamp(bottom + dy, top + Math.Min(.1,r.Height), 100);
        }
        var next = _original with { Rectangle = new() { X = left, Y = top, Width = right - left, Height = bottom - top } };
        _perspectives = _perspectives.Select(p => p.Id == next.Id ? next : p).ToArray(); Draw();
    }
    private void Up(object sender, I.MouseButtonEventArgs e)
    {
        if (_original is not { } original) return;
        var next = _perspectives.Single(p => p.Id == original.Id); _original = null; _overlay.ReleaseMouseCapture();
        if (next != original) Completed?.Invoke(original, next);
        Draw();
    }
    public void CancelEdit()
    {
        if (_original is { } original) _perspectives = _perspectives.Select(p => p.Id == original.Id ? original : p).ToArray();
        _original = null; _overlay.ReleaseMouseCapture(); Draw();
    }
    public void Stop() { _pulse.Stop(); _video.Close(); _image.Source = null; }
    public void NextPoint(bool reverse)
    {
        FinishKeyboardEdit();
        _handle = (_handle + (reverse ? 3 : 1) + 4) % 4; Draw();
    }
    public void Nudge(int x, int y, int pixels)
    {
        var selected = _perspectives.FirstOrDefault(p => p.Id == SelectedId); if (selected is null) return;
        _original ??= selected;
        var r = selected.Rectangle;
        var dx = x * pixels / Math.Max(1, _mediaWidth) * 100; var dy = y * pixels / Math.Max(1, _mediaHeight) * 100;
        double left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;
        if (_handle < 0) { left = Math.Clamp(left + dx, 0, 100 - r.Width); top = Math.Clamp(top + dy, 0, 100 - r.Height); right = left + r.Width; bottom = top + r.Height; }
        else
        {
            if (_handle is 0 or 2) left = Math.Clamp(left + dx, 0, right - Math.Min(.1,r.Width)); else right = Math.Clamp(right + dx, left + Math.Min(.1,r.Width), 100);
            if (_handle is 0 or 1) top = Math.Clamp(top + dy, 0, bottom - Math.Min(.1,r.Height)); else bottom = Math.Clamp(bottom + dy, top + Math.Min(.1,r.Height), 100);
        }
        var next = selected with { Rectangle = new() { X = left, Y = top, Width = right - left, Height = bottom - top } };
        _perspectives = _perspectives.Select(p => p.Id == next.Id ? next : p).ToArray(); Draw();
    }
    public void FinishKeyboardEdit()
    {
        if (_original is not { } original) return;
        var next = _perspectives.Single(p => p.Id == original.Id); _original = null;
        if (next != original) Completed?.Invoke(original, next);
    }
    public void Flash(bool success)
    {
        _flashSuccess = success; _flashUntil = Environment.TickCount64 + 250; Draw();
    }
}
