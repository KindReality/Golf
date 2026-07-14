using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private AppState? _pendingState;
        private bool _isTransitioning;

        private readonly object _sync = new();
        private VideoCapture? _capture;
        private Mat? _frameFull;
        private Mat? _framePreview;
        private string? _workDir;
        private Uri? _videoUri;
        private bool _videoPrepared;
        private BitmapSource? _lastCapturedImage;

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
        private bool _enableFaceDetection = true; // Default to true
        private bool _enableSerial = true;

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
                if (root.TryGetProperty("EnableFaceDetection",  out var v10)) _enableFaceDetection = v10.GetBoolean();
                if (root.TryGetProperty("EnableSerial",         out var v11)) _enableSerial        = v11.GetBoolean();

                if (_enableSerial && root.TryGetProperty("Serial", out var s))
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
                else
                {
                    _serialOptions = null;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDeveloperMode();
            _faceCascade = TryLoadFaceCascade();
            PrepareVideo();
            await StartCameraAsync(0);
            EnterState(AppState.Idle);
            await Dispatcher.InvokeAsync(() =>
            {
                PrepareLatestPicture();
                imgPreviewButton.Source = _lastCapturedImage;
            }, DispatcherPriority.Loaded);
            await InitializeSerialAsync();
            LoadBrandingFromIcon();
        }

        private void PrepareLatestPicture()
        {
            try
            {
                var searchDirs = GetPictureSearchDirectories().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Debug.WriteLine($"[Picture] Search directories: {string.Join(" | ", searchDirs)}");

                var newest = searchDirs
                    .Where(d => { try { return Directory.Exists(d); } catch { return false; } })
                    .SelectMany(dir =>
                    {
                        try { return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly); }
                        catch { return Enumerable.Empty<string>(); }
                    })
                    .Where(path =>
                    {
                        var ext = Path.GetExtension(path);
                        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(path => { try { return new FileInfo(path); } catch { return null; } })
                    .Where(fi => fi is not null && fi.Exists && fi.Length > 0)
                    .OrderByDescending(fi => fi!.LastWriteTimeUtc)
                    .ThenByDescending(fi => fi!.CreationTimeUtc)
                    .FirstOrDefault();

                if (newest is null)
                {
                    Debug.WriteLine("[Picture] Prepare skipped: no saved pictures found in any search directory.");
                    return;
                }

                Debug.WriteLine($"[Picture] Loading: {newest.FullName} ({newest.Length} bytes)");

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.UriSource    = new Uri(newest.FullName);
                bmp.EndInit();
                bmp.Freeze();

                _lastCapturedImage = bmp;
                imgPreviewButton.Source = bmp;

                Debug.WriteLine($"[Picture] Prepared latest picture: {newest.FullName} ({bmp.PixelWidth}x{bmp.PixelHeight})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Picture] Prepare failed: {ex}");
            }
        }

        private IEnumerable<string> GetPictureSearchDirectories()
        {
            var resolved = ResolvePictureDirectory();
            if (!string.IsNullOrWhiteSpace(resolved))
                yield return resolved;

            yield return Path.Combine(AppContext.BaseDirectory, "Output");

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir.Parent is not null; i++)
            {
                dir = dir.Parent;
                yield return Path.Combine(dir.FullName, "Output");
            }
        }

        private string ResolvePictureDirectory()
        {
            var dir = _workDir;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Environment.GetEnvironmentVariable("PhotoBoothWorking");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(AppContext.BaseDirectory, "Output");
            return dir;
        }

        private void PrepareVideo()
        {
            try
            {
                string? resolved = ResolveProjectRelativePath("media\\video.mp4");
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    _videoUri = null;
                    _videoPrepared = false;
                    Debug.WriteLine("[Video] Preload skipped: file not found.");
                    return;
                }

                _videoUri = new Uri(resolved);
                videoPlayer.LoadedBehavior   = MediaState.Manual;
                videoPlayer.UnloadedBehavior = MediaState.Stop;
                videoPlayer.Source           = _videoUri;
                videoPlayer.Position         = TimeSpan.Zero;
                videoPlayer.Volume           = 0;
                videoPlayer.Play();
                videoPlayer.Pause();
                videoPlayer.Position = TimeSpan.Zero;
                videoPlayer.Volume   = 1;
                _videoPrepared = true;
                Debug.WriteLine($"[Video] Prepared: {resolved}");
            }
            catch (Exception ex)
            {
                _videoPrepared = false;
                _videoUri = null;
                Debug.WriteLine($"[Video] Prepare failed: {ex}");
            }
        }

        private void ApplyDeveloperMode()
        {
            overlayGrid.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
            Debug.WriteLine(_developerMode ? "[DeveloperMode] ON" : "[DeveloperMode] OFF");
        }

        private void TransitionTo(AppState next)
        {
            Debug.WriteLine($"[Transition] Request {_state} -> {next} | transitioning={_isTransitioning} | pending={_pendingState?.ToString() ?? "null"}");

            if (_isTransitioning)
            {
                _pendingState = next;
                Debug.WriteLine($"[Transition] Queued {_pendingState}");
                return;
            }

            if (_state == next && next != AppState.Capture)
            {
                Debug.WriteLine($"[Transition] Ignored duplicate state {_state}");
                return;
            }

            // Exclusions requested: no shared fade for Idle->Countdown, Capture->Preview, or Preview->Idle
            if ((_state == AppState.Idle && next == AppState.Countdown)
                || (_state == AppState.Capture && next == AppState.Preview)
                || (_state == AppState.Preview && next == AppState.Idle))
            {
                Debug.WriteLine($"[Transition] Direct {_state} -> {next}");
                EnterState(next);
                return;
            }

            Debug.WriteLine($"[Transition] Animated {_state} -> {next}");
            _pendingState = next;
            _isTransitioning = true;

            StopAllStateTimers();
            transitionGrid.Visibility = Visibility.Visible;
            rectTransition.Visibility = Visibility.Visible;
            rectTransition.BeginAnimation(UIElement.OpacityProperty, null);
            rectTransition.Opacity = 0;

            Debug.WriteLine($"[Transition] Starting TransitionIn for {_state} -> {next}");
            PlayAnimation(
                "TransitionIn",
                duration: TimeSpan.FromSeconds(_transitionInSeconds),
                onCompleted: OnGlobalTransitionInCompleted,
                target: rectTransition);
        }

        private void OnGlobalTransitionInCompleted(object? sender, EventArgs e)
        {
            Debug.WriteLine($"[Transition] TransitionIn completed | pending={_pendingState?.ToString() ?? "null"}");

            if (_pendingState is null)
            {
                EndTransition();
                return;
            }

            var next = _pendingState.Value;
            _pendingState = null;
            Debug.WriteLine($"[Transition] Entering state {next}");
            EnterState(next);

            Debug.WriteLine($"[Transition] Starting TransitionOut for {next}");
            PlayAnimation(
                "TransitionOut",
                duration: TimeSpan.FromSeconds(_transitionOutSeconds),
                onCompleted: OnGlobalTransitionOutCompleted,
                target: rectTransition);
        }

        private void OnGlobalTransitionOutCompleted(object? sender, EventArgs e)
        {
            Debug.WriteLine($"[Transition] TransitionOut completed | pending={_pendingState?.ToString() ?? "null"}");
            EndTransition();

            if (_pendingState is AppState queued)
            {
                Debug.WriteLine($"[Transition] Processing queued state {queued}");
                _pendingState = null;
                TransitionTo(queued);
            }
        }

        private void EndTransition()
        {
            Debug.WriteLine("[Transition] EndTransition");
            rectTransition.BeginAnimation(UIElement.OpacityProperty, null);
            rectTransition.Opacity    = 0;
            rectTransition.Visibility = Visibility.Collapsed;
            transitionGrid.Visibility = Visibility.Collapsed;
            _isTransitioning = false;
        }

        private void EnterState(AppState next)
        {
            Debug.WriteLine($"[State] Enter {next}");
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

        private void HideAllContent()
        {
            captureGrid.Visibility   = Visibility.Collapsed;
            videoGrid.Visibility     = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Collapsed;
            previewGrid.Visibility   = Visibility.Collapsed;
            imgCapture.Source        = null;
            FaceLayer.Children.Clear();
            try { videoPlayer.Stop(); } catch { }
        }

        private void EnterIdleState()
        {
            Debug.WriteLine("[State:Idle] EnterIdleState");
            HideAllContent();
            SetCountdownText(string.Empty, 0);

            if (imgPreview.Source != _previewBitmap)
                imgPreview.Source = _previewBitmap;

            previewGrid.Visibility = Visibility.Visible;
            if (!_previewLoop.IsEnabled)
                _previewLoop.Start();

            Debug.WriteLine($"[State:Idle] camera={(_capture is null ? "null" : "ok")}, previewLoop={_previewLoop.IsEnabled}");
        }

        private void EnterCountdownState()
        {
            Debug.WriteLine("[State:Countdown] EnterCountdownState");
            captureGrid.Visibility   = Visibility.Collapsed;
            videoGrid.Visibility     = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Visible;
            previewGrid.Visibility   = Visibility.Visible;

            if (imgPreview.Source != _previewBitmap)
                imgPreview.Source = _previewBitmap;
            if (!_previewLoop.IsEnabled)
                _previewLoop.Start();

            _countdownCounter = _countdownSeconds;
            Debug.WriteLine($"[State:Countdown] Starting countdown at {_countdownCounter}");
            ShowCountdownTick();
            _countdownTimer.Start();
        }

        private void OnCountdownTick(object? sender, EventArgs e)
        {
            if (_state != AppState.Countdown) return;

            _countdownCounter--;
            Debug.WriteLine($"[State:Countdown] Tick -> {_countdownCounter}");

            if (_countdownCounter > 0)
            {
                ShowCountdownTick();
                return;
            }

            _countdownTimer.Stop();
            Debug.WriteLine("[State:Countdown] Complete -> Capture");
            TransitionTo(AppState.Capture);
        }

        private void ShowCountdownTick()
        {
            Debug.WriteLine($"[State:Countdown] ShowCountdownTick {_countdownCounter}");
            SetCountdownText(_countdownCounter.ToString(), 1);
            PlayAnimation("CountdownNumberInOut");
        }

        private void EnterCaptureState()
        {
            Debug.WriteLine("[State:Capture] EnterCaptureState");
            HideAllContent();
            SetCountdownText(string.Empty, 0);

            Mat? src;
            lock (_sync) { src = _frameFull?.Clone(); }

            if (src is null || src.Empty())
            {
                Debug.WriteLine("[State:Capture] No frame -> Idle");
                SetCountdownText("No frame", 1);
                src?.Dispose();
                TransitionTo(AppState.Idle);
                return;
            }

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
                        if (_state != AppState.Capture) return;
                        _lastCapturedImage      = bitmap;
                        imgCapture.Source       = bitmap;
                        imgPreviewButton.Source = bitmap;
                        captureGrid.Visibility  = Visibility.Visible;
                        Debug.WriteLine("[State:Capture] Capture image assigned to imgCapture");
                    });
                }
                catch (Exception ex) { Debug.WriteLine($"[State:Capture] {ex}"); src.Dispose(); }
            });

            Debug.WriteLine("[State:Capture] Queue Preview transition");
            _ = Dispatcher.BeginInvoke(new Action(() => TransitionTo(AppState.Preview)), DispatcherPriority.Background);
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
            Debug.WriteLine("[State:Preview] EnterPreviewState");
            videoGrid.Visibility     = Visibility.Collapsed;
            countdownGrid.Visibility = Visibility.Collapsed;
            previewGrid.Visibility   = Visibility.Collapsed;
            FaceLayer.Children.Clear();

            if (imgCapture.Source is null && _lastCapturedImage is not null)
                imgCapture.Source = _lastCapturedImage;

            captureGrid.Visibility = imgCapture.Source is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

            Debug.WriteLine($"[State:Preview] captureVisible={captureGrid.Visibility} hasImage={imgCapture.Source is not null}");

            _previewTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_previewSeconds) };
            _previewTimer.Tick += OnPreviewTimerTick;
            _previewTimer.Start();
            Debug.WriteLine($"[State:Preview] Preview timer started for {_previewSeconds}s");
        }

        private void OnPreviewTimerTick(object? sender, EventArgs e)
        {
            Debug.WriteLine("[State:Preview] Timer elapsed -> Idle");
            _previewTimer?.Stop();
            _previewTimer = null;
            if (_state != AppState.Preview) return;
            TransitionTo(AppState.Idle);
        }

        private void EnterVideoState()
        {
            Debug.WriteLine("[State:Video] EnterVideoState");
            HideAllContent();
            videoGrid.Visibility = Visibility.Visible;
            PlayLocalVideo("media\\video.mp4");
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[State:Video] MediaEnded -> Idle");
            _videoTimer?.Stop();
            _videoTimer = null;
            TransitionTo(AppState.Idle);
        }

        private void PlayAnimation(string resourceKey, TimeSpan? duration = null,
                                   EventHandler? onCompleted = null, UIElement? target = null)
        {
            try
            {
                Debug.WriteLine($"[Animation] Begin {resourceKey} duration={(duration.HasValue ? duration.Value.ToString() : "resource-default")} target={(target as FrameworkElement)?.Name ?? target?.GetType().Name ?? "window"}");
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

                sb.Completed += (_, __) => Debug.WriteLine($"[Animation] Completed {resourceKey}");
                sb.Begin(this, handoffBehavior: HandoffBehavior.SnapshotAndReplace, isControllable: true);
            }
            catch (Exception ex) { Debug.WriteLine($"[Animation] {resourceKey}: {ex}"); }
        }

        private void StopAllStateTimers()
        {
            Debug.WriteLine("[Timers] StopAllStateTimers");
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

            HideAllContent();
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

            bool previewVisible = previewGrid.Visibility == Visibility.Visible;

            if (_enableFaceDetection && previewVisible && _faceCascade is not null && !_faceDetecting)
            {
                _faceFrameSkip++;
                if (_faceFrameSkip >= FaceDetectEveryNFrames)
                {
                    _faceFrameSkip = 0;
                    _faceDetecting = true;
                    var detectFrame = frame;
                    frame = null;
                    _ = Task.Run(() => DetectFacesBackground(detectFrame));
                }
            }
            else if (!previewVisible)
            {
                FaceLayer.Children.Clear();
            }

            frame?.Dispose();
        }

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

                Dispatcher.InvokeAsync(() =>
                {
                    _lastFaces      = faces;
                    _imgPixelWidth  = w;
                    _imgPixelHeight = h;
                    if (previewGrid.Visibility == Visibility.Visible)
                        RenderFaceOverlay();
                    else
                        FaceLayer.Children.Clear();
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
            if (_state != AppState.Idle || _isTransitioning) return;
            TransitionTo(AppState.Countdown);
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            if (_lastCapturedImage is null) return;
            if (_state == AppState.Video) return;

            imgCapture.Source = _lastCapturedImage;

            if (_state == AppState.Idle)
            {
                EnterState(AppState.Preview);
                return;
            }

            TransitionTo(AppState.Preview);
        }

        private void BtnTrigger1_Click(object sender, RoutedEventArgs e)
        {
            if (_state == AppState.Video || _isTransitioning) return;
            TransitionTo(AppState.Video);
        }

        private void BtnTrigger2_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle || _isTransitioning) return;
            TransitionTo(AppState.Countdown);
        }
        private void BtnTrigger3_Click(object sender, RoutedEventArgs e) { }
        private void BtnTrigger4_Click(object sender, RoutedEventArgs e) { }
        private void BtnTrigger5_Click(object sender, RoutedEventArgs e) { }

        private async Task InitializeSerialAsync()
        {
            try
            {
                if (_serialOptions is null) return;

                _serialService                = new SerialService(_serialOptions);
                _serialService.LineReceived  += Serial_LineReceived;
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
                        Dispatcher.Invoke(() => { if (_state != AppState.Video && !_isTransitioning) TransitionTo(AppState.Video); });
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
                Dispatcher.Invoke(() => { if (_state != AppState.Video && !_isTransitioning) TransitionTo(AppState.Video); });
            else if (val.Equals("LONG", StringComparison.OrdinalIgnoreCase))
                Dispatcher.Invoke(() => { if (_state == AppState.Idle && !_isTransitioning) TransitionTo(AppState.Countdown); });
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
                && !val.Equals("NO", StringComparison.OrdinalIgnoreCase)
                && !val.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                && !val.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => { if (_state != AppState.Video && !_isTransitioning) TransitionTo(AppState.Video); });
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
                if (!_videoPrepared)
                    PrepareVideo();

                string? resolved = ResolveProjectRelativePath(relativePath);
                if (_videoUri is null && (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved)))
                    _videoUri = new Uri(resolved);

                if (_videoUri is null)
                {
                    SetCountdownText("Video not found", 1);
                    TransitionTo(AppState.Idle);
                    return;
                }

                videoPlayer.LoadedBehavior   = MediaState.Manual;
                videoPlayer.UnloadedBehavior = MediaState.Stop;
                if (videoPlayer.Source is null || videoPlayer.Source != _videoUri)
                    videoPlayer.Source = _videoUri;
                videoPlayer.Position = TimeSpan.Zero;
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

        private void LoadBrandingFromIcon()
        {
            try
            {
                string? iconPath = ResolveProjectRelativePath("media\\icon.png")
                                   ?? Path.Combine(AppContext.BaseDirectory, "media", "icon.png");
                if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                {
                    Debug.WriteLine("[Branding] icon.png not found");
                    return;
                }

                // Use System.Drawing to sample the image and compute an average primary color
                using var bmp = new System.Drawing.Bitmap(iconPath);
                long rSum = 0, gSum = 0, bSum = 0, count = 0;
                int stepX = Math.Max(1, bmp.Width / 64);
                int stepY = Math.Max(1, bmp.Height / 64);
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    for (int y = 0; y < bmp.Height; y += stepY)
                    {
                        var p = bmp.GetPixel(x, y);
                        if (p.A < 64) continue;
                        rSum += p.R; gSum += p.G; bSum += p.B; count++;
                    }
                }

                if (count == 0) return;

                byte r = (byte)(rSum / count);
                byte g = (byte)(gSum / count);
                byte b = (byte)(bSum / count);

                var primaryColor = System.Windows.Media.Color.FromRgb(r, g, b);

                // Choose accent foreground (white/black) based on luminance
                double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                var accentFore = luminance > 0.6 ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White;

                // Slightly desaturate/adjust primary for UI surfaces
                var adjusted = System.Windows.Media.Color.FromScRgb(1f,
                    Math.Min(1f, (float)(r / 255.0 * 0.95)),
                    Math.Min(1f, (float)(g / 255.0 * 0.95)),
                    Math.Min(1f, (float)(b / 255.0 * 0.95)));

                // Apply to resources in this window so DynamicResource picks it up
                this.Resources["PrimaryBrush"] = new SolidColorBrush(primaryColor);
                this.Resources["AccentBrush"]  = new SolidColorBrush(adjusted);
                this.Resources["AccentForeground"] = new SolidColorBrush(accentFore);

                // Set window icon from icon.ico if available, else fallback to icon.png
                try
                {
                    string? icoPath = ResolveProjectRelativePath("media\\icon.ico")
                                      ?? ResolveProjectRelativePath("media\\icon.png")
                                      ?? Path.Combine(AppContext.BaseDirectory, "media", "icon.ico");

                    if (!string.IsNullOrWhiteSpace(icoPath) && File.Exists(icoPath))
                    {
                        try
                        {
                            var icoUri = new Uri(icoPath, UriKind.Absolute);
                            this.Icon = BitmapFrame.Create(icoUri);
                            Debug.WriteLine($"[Branding] Window.Icon set from {icoPath}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Branding] Failed to set Window.Icon from {icoPath}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Branding] Window icon assignment failed: {ex}");
                }

                // Also set the preview button to the icon if no last captured image
                if (_lastCapturedImage is null)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(iconPath);
                    bi.EndInit();
                    bi.Freeze();
                    _lastCapturedImage = bi;
                    imgPreviewButton.Source = bi;
                    Debug.WriteLine($"[Branding] Set preview button image to icon.png");
                }

                // Note: Taskbar uses Window.Icon and the application embedded icon. Ensure ApplicationIcon is set in the project file.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Branding] failed: {ex}");
            }
        }
    }
}
