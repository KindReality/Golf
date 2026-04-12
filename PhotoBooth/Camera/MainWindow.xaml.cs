using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfWindow = System.Windows.Window;
using SaftApp.Serial;

namespace SaftApp
{
    public enum AppState { Idle, Countdown, Capture, Preview, Video }

    public sealed partial class MainWindow : WpfWindow
    {
        // ── Configuration ─────────────────────────────────────────────────────
        private double _transitionInSeconds  = 1.0;
        private double _transitionOutSeconds = 2.0;
        private double _previewSeconds       = 10.0;
        private double _videoDurationSeconds = 30.0;
        private int    _countdownSeconds     = 6;
        private int    _targetFps            = 30;
        private int    _previewWidth         = 1920;
        private int    _previewHeight        = 1080;
        private bool   _developerMode        = false;

        // ── Runtime state ─────────────────────────────────────────────────────
        private AppState _state = AppState.Idle;

        private readonly object _sync = new();
        private VideoCapture? _capture;
        private Mat? _frameFull;
        private Mat? _framePreview;
        private string? _workDir;

        // ── WriteableBitmap reuse ─────────────────────────────────────────────
        private WriteableBitmap? _previewBitmap;

        // ── Background frame pump ─────────────────────────────────────────────
        // Decoded frames are produced on a background thread and handed to the
        // UI thread via this double-buffer slot.
        private Mat?              _frameReady;        // background writes, UI thread reads
        private readonly object   _frameLock = new(); // guards _frameReady
        private Thread?           _captureThread;
        private volatile bool     _captureRunning;

        private readonly DispatcherTimer _previewLoop    = new();
        private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DispatcherTimer? _previewTimer;
        private DispatcherTimer? _videoTimer;

        private int _countdownCounter;

        private ISerialService? _serialService;
        private SerialOptions?  _serialOptions;

        // ── Face detection ────────────────────────────────────────────────────
        private CascadeClassifier?  _faceCascade;
        private OpenCvSharp.Rect[]  _lastFaces      = Array.Empty<OpenCvSharp.Rect>();
        private int                 _imgPixelWidth;
        private int                 _imgPixelHeight;
        private volatile bool       _faceDetecting;
        private int                 _faceFrameSkip;
        private const int           FaceDetectEveryNFrames = 10;

        public MainWindow()
        {
            LoadTimingConfiguration();

            if (_developerMode)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                WindowStyle           = WindowStyle.SingleBorderWindow;
                ResizeMode            = ResizeMode.CanResize;
                Width                 = 800;
                Height                = 600;
            }

            InitializeComponent();

            if (!_developerMode)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode  = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }

            _previewLoop.Interval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            _previewLoop.Tick    += OnPreviewLoopTick;
            _countdownTimer.Tick += OnCountdownTick;
        }

        private void LoadTimingConfiguration()
        {
            try
            {
                string? path = ResolveProjectRelativePath("appsettings.json")
                               ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (!File.Exists(path)) return;

                using var doc  = JsonDocument.Parse(File.ReadAllText(path));
                var       root = doc.RootElement;

                if (root.TryGetProperty("TransitionInSeconds",  out var v1)) _transitionInSeconds  = v1.GetDouble();
                if (root.TryGetProperty("TransitionOutSeconds", out var v2)) _transitionOutSeconds = v2.GetDouble();
                if (root.TryGetProperty("PreviewSeconds",       out var v3)) _previewSeconds       = v3.GetDouble();
                if (root.TryGetProperty("VideoDurationSeconds", out var v4)) _videoDurationSeconds = v4.GetDouble();
                if (root.TryGetProperty("CountdownSeconds",     out var v5)) _countdownSeconds     = v5.GetInt32();
                if (root.TryGetProperty("TargetFps",            out var v6)) _targetFps            = v6.GetInt32();
                if (root.TryGetProperty("PreviewWidth",         out var v7)) _previewWidth         = v7.GetInt32();
                if (root.TryGetProperty("PreviewHeight",        out var v8)) _previewHeight        = v8.GetInt32();
                if (root.TryGetProperty("DeveloperMode",        out var v9)) _developerMode        = v9.GetBoolean();

                if (root.TryGetProperty("Serial", out var s))
                {
                    try
                    {
                        _serialOptions = new SerialOptions();
                        if (s.TryGetProperty("PortName", out var pn)) _serialOptions.PortName = pn.GetString();
                        if (s.TryGetProperty("BaudRate", out var br)) _serialOptions.BaudRate = br.GetInt32();
                        if (s.TryGetProperty("AutoOpen", out var ao)) _serialOptions.AutoOpen = ao.GetBoolean();
                    }
                    catch (Exception ex) { Debug.WriteLine(ex); _serialOptions = null; }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDeveloperMode();
            _faceCascade = TryLoadFaceCascade();
            await StartCameraAsync(0);
            await InitializeSerialAsync();
            TransitionTo(AppState.Idle);
        }

        private void ApplyDeveloperMode()
        {
            overlayGrid.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
            Debug.WriteLine(_developerMode ? "[DeveloperMode] ON" : "[DeveloperMode] OFF");
        }

        private void TransitionTo(AppState next)
        {
            Debug.WriteLine($"[State] {_state} → {next}");
            _state = next;

            switch (_state)
            {
                case AppState.Idle:      EnterIdleState();      break;
                case AppState.Countdown: EnterCountdownState(); break;
                case AppState.Capture:   EnterCaptureState();   break;
                case AppState.Preview:   EnterPreviewState();   break;
                case AppState.Video:     EnterVideoState();     break;
            }
        }

        private void EnterIdleState()
        {
            StopAllStateTimers();

            rectTransition.BeginAnimation(UIElement.OpacityProperty, null);
            rectTransition.Opacity    = 0;
            rectTransition.Visibility = Visibility.Collapsed;
            transitionGrid.Visibility = Visibility.Collapsed;

            captureGrid.Visibility   = Visibility.Collapsed;
            imgCapture.Source        = null;
            videoGrid.Visibility     = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Collapsed;

            try { videoPlayer.Stop(); } catch { }

            SetCountdownText(string.Empty, 0);

            if (imgPreview.Source != _previewBitmap)
                imgPreview.Source = _previewBitmap;
            previewGrid.Visibility = Visibility.Visible;
            if (!_previewLoop.IsEnabled)
                _previewLoop.Start();

            Debug.WriteLine($"[Idle] camera={(_capture is null ? "null" : "ok")}");
        }

        private void EnterCountdownState()
        {
            captureGrid.Visibility   = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Visible;
            _countdownCounter        = _countdownSeconds;
            ShowCountdownTick();
            _countdownTimer.Start();
        }

        private void OnCountdownTick(object? sender, EventArgs e)
        {
            if (_state != AppState.Countdown) return;

            _countdownCounter--;

            if (_countdownCounter > 0)
            {
                ShowCountdownTick();
                return;
            }

            _countdownTimer.Stop();
            TransitionTo(AppState.Capture);
        }

        private void ShowCountdownTick()
        {
            SetCountdownText(_countdownCounter.ToString(), 1);
            PlayAnimation("CountdownNumberInOut");
        }

        private void EnterCaptureState()
        {
            countdownGrid.Visibility  = Visibility.Collapsed;
            SetCountdownText(string.Empty, 0);
            transitionGrid.Visibility = Visibility.Visible;
            PlayAnimation("TransitionIn",
                duration:    TimeSpan.FromSeconds(_transitionInSeconds),
                onCompleted: OnTransitionInCompleted,
                target:      rectTransition);
        }

        private void OnTransitionInCompleted(object? sender, EventArgs e)
        {
            if (_state != AppState.Capture) return;

            Mat? src;
            lock (_sync) { src = _frameFull?.Clone(); }

            if (src is null || src.Empty())
            {
                Debug.WriteLine("[Capture] No frame — returning to Idle");
                SetCountdownText("No frame", 1);
                src?.Dispose();
                TransitionTo(AppState.Idle);
                return;
            }

            rectTransition.BeginAnimation(UIElement.OpacityProperty, null);
            PlayAnimation("TransitionOut",
                duration:    TimeSpan.FromSeconds(_transitionOutSeconds),
                onCompleted: OnTransitionOutCompleted,
                target:      rectTransition);

            _ = Task.Run(() =>
            {
                try
                {
                    var bitmap = src.ToBitmapSource();
                    bitmap.Freeze();
                    src.Dispose();

                    SaveCapture(bitmap);

                    Dispatcher.Invoke(() =>
                    {
                        imgCapture.Source      = bitmap;
                        captureGrid.Visibility = Visibility.Visible;
                        previewGrid.Visibility = Visibility.Collapsed;
                    });
                }
                catch (Exception ex) { Debug.WriteLine($"[Capture] {ex}"); src.Dispose(); }
            });
        }

        private void OnTransitionOutCompleted(object? sender, EventArgs e)
        {
            if (_state != AppState.Capture) return;
            TransitionTo(AppState.Preview);
        }

        private void SaveCapture(BitmapSource bitmap)
        {
            var dir  = string.IsNullOrWhiteSpace(_workDir) ? AppContext.BaseDirectory : _workDir;
            var path = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var fs = File.Create(path);
                    encoder.Save(fs);
                    Debug.WriteLine($"[Capture] Saved: {path}");
                }
                catch (Exception ex) { Debug.WriteLine($"{ex}"); }
            });
        }

        private void EnterPreviewState()
        {
            _previewTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_previewSeconds) };
            _previewTimer.Tick += OnPreviewTimerTick;
            _previewTimer.Start();
        }

        private void OnPreviewTimerTick(object? sender, EventArgs e)
        {
            _previewTimer?.Stop();
            _previewTimer = null;
            if (_state != AppState.Preview) return;
            TransitionTo(AppState.Idle);
        }

        private void EnterVideoState()
        {
            captureGrid.Visibility = Visibility.Collapsed;
            previewGrid.Visibility = Visibility.Collapsed;
            videoGrid.Visibility   = Visibility.Visible;
            FaceLayer.Children.Clear();
            PlayLocalVideo("media\\video.mp4");
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _videoTimer?.Stop();
            _videoTimer = null;
            TransitionTo(AppState.Idle);
        }

        private void PlayAnimation(string resourceKey, TimeSpan? duration = null,
                                   EventHandler? onCompleted = null, UIElement? target = null)
        {
            try
            {
                var sb = ((Storyboard)FindResource(resourceKey)).Clone();

                if (target is not null)
                    Storyboard.SetTarget(sb, target);

                if (duration.HasValue)
                {
                    var d = new Duration(duration.Value);
                    foreach (Timeline child in sb.Children)
                        child.Duration = d;
                }

                if (onCompleted is not null)
                    sb.Completed += onCompleted;

                sb.Begin(this, handoffBehavior: HandoffBehavior.SnapshotAndReplace, isControllable: true);
            }
            catch (Exception ex) { Debug.WriteLine($"[Animation] {resourceKey}: {ex}"); }
        }

        private void StopAllStateTimers()
        {
            _countdownTimer.Stop();

            _previewTimer?.Stop();
            _previewTimer = null;

            _videoTimer?.Stop();
            _videoTimer = null;
        }

        private async Task StartCameraAsync(int cameraIndex)
        {
            if (_capture is not null && _capture.IsOpened()) return;

            await StopCameraAsync();

            VideoCapture? cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened()) { cap.Release(); cap.Dispose(); cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY); }
            if (!cap.IsOpened()) { cap.Release(); cap.Dispose(); SetCountdownText("Camera not found", 1); return; }

            TrySetHighestResolution(cap);

            var env = Environment.GetEnvironmentVariable("PhotoBoothWorking");
            if (string.IsNullOrWhiteSpace(env))
                env = Path.Combine(AppContext.BaseDirectory, "Output");
            try { Directory.CreateDirectory(env); } catch { }
            _workDir = env;

            lock (_sync) { _capture = cap; }

            _frameFull?.Dispose();
            _framePreview?.Dispose();
            _frameFull    = new Mat((int)cap.FrameHeight, (int)cap.FrameWidth, MatType.CV_8UC3);
            _framePreview = new Mat(_previewHeight, _previewWidth, MatType.CV_8UC3);

            _previewBitmap = new WriteableBitmap(
                _previewWidth, _previewHeight, 96, 96,
                PixelFormats.Bgr24, null);
            imgPreview.Source = _previewBitmap;

            _captureRunning = true;
            _captureThread  = new Thread(CaptureLoop) { IsBackground = true, Name = "CaptureLoop" };
            _captureThread.Start();

            _previewLoop.Start();
        }

        private async Task StopCameraAsync()
        {
            _captureRunning = false;

            _previewLoop.Stop();
            StopAllStateTimers();

            try
            {
                if (_serialService is not null)
                {
                    _serialService.LineReceived  -= Serial_LineReceived;
                    _serialService.StatusChanged -= Serial_StatusChanged;
                    _serialService.Dispose();
                }
            }
            catch { }
            _serialService = null;

            VideoCapture? cap;
            lock (_sync) { cap = _capture; _capture = null; }
            try { cap?.Release(); cap?.Dispose(); } catch { }

            _captureThread?.Join(500);
            _captureThread = null;

            _frameFull?.Dispose();    _frameFull    = null;
            _framePreview?.Dispose(); _framePreview = null;

            lock (_frameLock)
            {
                _frameReady?.Dispose();
                _frameReady = null;
            }

            _previewBitmap = null;

            rectTransition.BeginAnimation(UIElement.OpacityProperty, null);
            rectTransition.Opacity    = 0;
            rectTransition.Visibility = Visibility.Collapsed;
            transitionGrid.Visibility = Visibility.Collapsed;

            captureGrid.Visibility   = Visibility.Collapsed;
            imgCapture.Source        = null;
            videoGrid.Visibility     = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Collapsed;

            try { videoPlayer.Stop(); } catch { }
        }

        // Runs on background thread — reads camera, resizes, double-buffers result.
        private void CaptureLoop()
        {
            while (_captureRunning)
            {
                VideoCapture? cap;
                Mat?          full;
                Mat?          prev;

                lock (_sync)
                {
                    cap  = _capture;
                    full = _frameFull;
                    prev = _framePreview;
                }

                if (cap is null || full is null || prev is null) { Thread.Sleep(5); continue; }

                if (!cap.Read(full) || full.Empty()) { Thread.Sleep(5); continue; }

                Cv2.Resize(full, prev, new OpenCvSharp.Size(_previewWidth, _previewHeight),
                           interpolation: InterpolationFlags.Area);

                var ready = prev.Clone();
                lock (_frameLock)
                {
                    _frameReady?.Dispose();
                    _frameReady = ready;
                }
            }
        }

        // Called on UI thread every frame tick.
        private void OnPreviewLoopTick(object? sender, EventArgs e)
        {
            if (_state == AppState.Preview || _state == AppState.Video) return;
            if (_previewBitmap is null) return;

            Mat? frame;
            lock (_frameLock)
            {
                frame       = _frameReady;
                _frameReady = null;
            }

            if (frame is null) return;

            // Blit frame into WriteableBitmap
            try
            {
                long stride = (long)_previewWidth * 3;
                _previewBitmap.Lock();
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)frame.Data,
                        (void*)_previewBitmap.BackBuffer,
                        _previewBitmap.BackBufferStride * _previewHeight,
                        stride * _previewHeight);
                }
                _previewBitmap.AddDirtyRect(new Int32Rect(0, 0, _previewWidth, _previewHeight));
            }
            finally
            {
                _previewBitmap.Unlock();
            }

            // Throttle face detection: every N frames, hand the frame to a background task
            if (_faceCascade is not null && !_faceDetecting)
            {
                _faceFrameSkip++;
                if (_faceFrameSkip >= FaceDetectEveryNFrames)
                {
                    _faceFrameSkip = 0;
                    _faceDetecting = true;
                    var detectFrame = frame;
                    frame = null;           // ownership transferred
                    _ = Task.Run(() => DetectFacesBackground(detectFrame));
                }
            }

            frame?.Dispose();
        }

        // Runs on a background thread.
        private void DetectFacesBackground(Mat frame)
        {
            try
            {
                if (_faceCascade is null) return;

                int w = frame.Width;
                int h = frame.Height;

                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                var faces = _faceCascade.DetectMultiScale(
                    gray,
                    scaleFactor:  1.2,
                    minNeighbors: 5,
                    flags:        HaarDetectionTypes.ScaleImage,
                    minSize:      new OpenCvSharp.Size(80, 80));

                Debug.WriteLine($"[FaceDetect] {faces.Length} face(s).");

                Dispatcher.InvokeAsync(() =>
                {
                    _lastFaces      = faces;
                    _imgPixelWidth  = w;
                    _imgPixelHeight = h;
                    RenderFaceOverlay();
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[FaceDetect] {ex.Message}"); }
            finally
            {
                frame.Dispose();
                _faceDetecting = false;
            }
        }

        // Loads haarcascade_frontalface_default.xml from several well-known locations.
        private static CascadeClassifier? TryLoadFaceCascade()
        {
            const string fileName = "haarcascade_frontalface_default.xml";

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Haar", fileName),
            };

            string? found = null;
            foreach (var c in candidates)
                if (File.Exists(c)) { found = c; break; }

            if (found is null)
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 6 && dir.Parent is not null && found is null; i++)
                {
                    dir = dir.Parent;
                    var p = Path.Combine(dir.FullName, fileName);
                    if (File.Exists(p)) found = p;
                }
            }

            if (found is null)
            {
                Debug.WriteLine("[FaceDetect] haarcascade_frontalface_default.xml not found — face detection disabled.");
                return null;
            }

            try
            {
                var cc = new CascadeClassifier(found);
                if (cc.Empty()) { cc.Dispose(); Debug.WriteLine("[FaceDetect] Cascade empty."); return null; }
                Debug.WriteLine($"[FaceDetect] Cascade loaded: {found}");
                return cc;
            }
            catch (Exception ex) { Debug.WriteLine($"[FaceDetect] Load failed: {ex.Message}"); return null; }
        }

        // Called on UI thread after each detection pass.
        private void RenderFaceOverlay()
        {
            FaceLayer.Children.Clear();

            if (_lastFaces.Length == 0 || _imgPixelWidth <= 0 || _imgPixelHeight <= 0)
                return;

            double cw = FaceLayer.ActualWidth;
            double ch = FaceLayer.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // Match the Uniform stretch used by imgPreview
            double scale   = Math.Min(cw / _imgPixelWidth, ch / _imgPixelHeight);
            double offsetX = (cw - _imgPixelWidth  * scale) * 0.5;
            double offsetY = (ch - _imgPixelHeight * scale) * 0.5;

            foreach (var face in _lastFaces)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width           = face.Width  * scale,
                    Height          = face.Height * scale,
                    Stroke          = Brushes.LimeGreen,
                    StrokeThickness = 2,
                    Fill            = Brushes.Transparent,
                };
                Canvas.SetLeft(rect, offsetX + face.X * scale);
                Canvas.SetTop (rect, offsetY + face.Y * scale);
                FaceLayer.Children.Add(rect);
            }
        }

        private void BtnCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            TransitionTo(AppState.Countdown);
        }

        private void BtnTrigger1_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            TransitionTo(AppState.Video);
        }

        private void BtnTrigger2_Click(object sender, RoutedEventArgs e) { if (_state != AppState.Idle) return; }
        private void BtnTrigger3_Click(object sender, RoutedEventArgs e) { if (_state != AppState.Idle) return; }
        private void BtnTrigger4_Click(object sender, RoutedEventArgs e) { if (_state != AppState.Idle) return; }
        private void BtnTrigger5_Click(object sender, RoutedEventArgs e) { if (_state != AppState.Idle) return; }

        private async Task InitializeSerialAsync()
        {
            try
            {
                if (_serialOptions is null) return;

                _serialService               = new SerialService(_serialOptions);
                _serialService.LineReceived += Serial_LineReceived;
                _serialService.StatusChanged += Serial_StatusChanged;

                if (_serialOptions.AutoOpen)
                {
                    bool ok = await _serialService.OpenAsync();
                    Debug.WriteLine($"[Serial] Open ({_serialOptions.PortName}@{_serialOptions.BaudRate}) → {ok}");

                    if (!ok)
                        SetCountdownText($"Serial {_serialOptions.PortName ?? "?"} not open", 1);
                    else
                    {
                        SetCountdownText("Serial ready", 1);
                        _ = Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => SetCountdownText(string.Empty, 0)));
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private void Serial_StatusChanged(object? sender, SerialStatusEventArgs e)
            => Trace.WriteLine($"[Serial] {e}");

        private void Serial_LineReceived(object? sender, string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                var trimmed = line.Trim();

                if (trimmed.StartsWith("EVENT:", StringComparison.OrdinalIgnoreCase))
                {
                    if (trimmed[6..].Trim().Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                        Dispatcher.Invoke(() => { if (_state == AppState.Idle) TransitionTo(AppState.Video); });
                    return;
                }

                if (trimmed.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                {
                    var upper = trimmed.ToUpperInvariant();
                    ParseDebugManualButton(upper);
                    ParseDebugBreakBeam(upper);
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private void ParseDebugManualButton(string upper)
        {
            int idx = upper.IndexOf("MANUALBUTTONEVENT", StringComparison.Ordinal);
            if (idx < 0) return;

            int colon = upper.IndexOf(':', idx);
            if (colon < 0 || colon + 1 >= upper.Length) return;

            var val = upper[(colon + 1)..]
                          .Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault()?.Trim();

            if (string.IsNullOrEmpty(val) || val.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;

            if (val.Equals("TAP", StringComparison.OrdinalIgnoreCase))
                Dispatcher.Invoke(() => { if (_state == AppState.Idle) TransitionTo(AppState.Video); });
            else if (val.Equals("LONG", StringComparison.OrdinalIgnoreCase))
                Dispatcher.Invoke(() => { if (_state == AppState.Idle) TransitionTo(AppState.Countdown); });
        }

        private void ParseDebugBreakBeam(string upper)
        {
            int idx = upper.IndexOf("BREAKBEAMTRIGGERED", StringComparison.Ordinal);
            if (idx < 0) return;

            int colon = upper.IndexOf(':', idx);
            if (colon < 0 || colon + 1 >= upper.Length) return;

            var val = upper[(colon + 1)..]
                          .Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault()?.Trim();

            if (!string.IsNullOrEmpty(val)
                && !val.Equals("NO",    StringComparison.OrdinalIgnoreCase)
                && !val.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                && !val.Equals("0",     StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => { if (_state == AppState.Idle) TransitionTo(AppState.Video); });
            }
        }

        private void TrySetHighestResolution(VideoCapture cap)
        {
            (int w, int h)[] candidates =
            {
                (7680,4320),(5120,2880),(4096,2160),
                (3840,2160),(2560,1440),(1920,1080)
            };
            foreach (var (w, h) in candidates)
            {
                cap.FrameWidth  = w;
                cap.FrameHeight = h;
                if ((int)cap.FrameWidth == w && (int)cap.FrameHeight == h) break;
            }
        }

        private void PlayLocalVideo(string relativePath)
        {
            try
            {
                string? resolved = ResolveProjectRelativePath(relativePath);
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    SetCountdownText("Video not found", 1);
                    TransitionTo(AppState.Idle);
                    return;
                }

                videoPlayer.Source           = new Uri(resolved);
                videoPlayer.LoadedBehavior   = MediaState.Manual;
                videoPlayer.UnloadedBehavior = MediaState.Stop;
                videoPlayer.Position         = TimeSpan.Zero;
                videoPlayer.Play();

                if (_videoDurationSeconds > 0)
                {
                    _videoTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_videoDurationSeconds) };
                    _videoTimer.Tick += OnVideoTimerTick;
                    _videoTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video] {ex}");
                SetCountdownText("Video error", 1);
                TransitionTo(AppState.Idle);
            }
        }

        private void OnVideoTimerTick(object? sender, EventArgs e)
        {
            _videoTimer?.Stop();
            _videoTimer = null;
            try { videoPlayer.Stop(); } catch { }
            TransitionTo(AppState.Idle);
        }

        private void SetCountdownText(string text, double opacity)
        {
            tbCountdown.Text    = text;
            tbCountdown.Opacity = opacity;
        }

        private string? ResolveProjectRelativePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath)) return relativePath;

            var candidate = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir.Parent is not null; i++)
            {
                dir       = dir.Parent;
                candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            return null;
        }

        protected override async void OnClosed(EventArgs e)
        {
            await StopCameraAsync();
            _faceCascade?.Dispose();
            _faceCascade = null;
            base.OnClosed(e);
        }
    }
}
