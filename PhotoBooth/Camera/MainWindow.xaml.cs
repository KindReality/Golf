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
        private const int CountdownSeconds = 6;
        private const int TargetFps = 30;
        private const int PreviewWidth = 1920;
        private const int PreviewHeight = 1080;

        // ── Timing configuration (seconds) ───────────────────────────────────
        private double _flashInSeconds = 1.0;
        private double _flashOutSeconds = 2.0;
        private double _holdSeconds = 0.3;
        private double _previewSeconds = 10.0;
        private double _videoDurationSeconds = 33.0; // 0 = play full file

        // ── Developer mode ────────────────────────────────────────────────────
        // When true: both buttons visible at equal size, window 800×600 centred.
        // When false: buttons collapsed/hidden, window maximised borderless.
        private bool _developerMode = false;

        // ── State ─────────────────────────────────────────────────────────────
        private AppState _state = AppState.Idle;

        // ── Camera ────────────────────────────────────────────────────────────
        private readonly object _sync = new();
        private VideoCapture? _capture;
        private Mat? _frameFull;
        private Mat? _framePreview;
        private string? _workDir;

        // ── Timers ────────────────────────────────────────────────────────────
        // Always-running live-preview pump (paused during Preview / Video states)
        private readonly DispatcherTimer _previewLoop = new();
        // Countdown: fires every 1 s while in Countdown state
        private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        // Preview: drives automatic return to Idle after _previewSeconds
        private DispatcherTimer? _previewTimer;
        // Video: optional hard cap on video playback duration
        private DispatcherTimer? _videoTimer;

        // ── Countdown state ───────────────────────────────────────────────────
        private int _countdownCounter;

        // ── UI references ─────────────────────────────────────────────────────
        private MediaElement? _videoControl;

        // ── Serial ────────────────────────────────────────────────────────────
        private ISerialService? _serialService;
        private SerialOptions? _serialOptions;

        // ─────────────────────────────────────────────────────────────────────
        // Construction / startup
        // ─────────────────────────────────────────────────────────────────────

        public MainWindow()
        {
            LoadTimingConfiguration();

            // Set window appearance before InitializeComponent creates the native handle.
            // Only developer mode needs pre-init settings (WindowStartupLocation, size).
            if (_developerMode)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                WindowStyle           = WindowStyle.SingleBorderWindow;
                ResizeMode            = ResizeMode.CanResize;
                Width                 = 800;
                Height                = 600;
            }

            InitializeComponent();

            // Production mode: maximise after InitializeComponent
            // (XAML defaults WindowState="Normal"; we override it here for kiosk mode)
            if (!_developerMode)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode  = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }

            _videoControl = (MediaElement?)FindName("videoControl");

            ApplyTimingsToResources();

            _previewLoop.Interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
            _previewLoop.Tick += OnPreviewLoopTick;

            _countdownTimer.Tick += OnCountdownTick;
        }

        private void LoadTimingConfiguration()
        {
            try
            {
                string? path = ResolveProjectRelativePath("appsettings.json")
                               ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (!File.Exists(path)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.TryGetProperty("FlashInSeconds",      out var v1)) _flashInSeconds      = v1.GetDouble();
                if (root.TryGetProperty("FlashOutSeconds",     out var v2)) _flashOutSeconds     = v2.GetDouble();
                if (root.TryGetProperty("HoldSeconds",         out var v3)) _holdSeconds         = v3.GetDouble();
                if (root.TryGetProperty("PreviewSeconds",      out var v4)) _previewSeconds      = v4.GetDouble();
                if (root.TryGetProperty("VideoDurationSeconds",out var v5)) _videoDurationSeconds= v5.GetDouble();
                if (root.TryGetProperty("DeveloperMode",       out var v6)) _developerMode       = v6.GetBoolean();

                if (root.TryGetProperty("Serial", out var s))
                {
                    try
                    {
                        _serialOptions = new SerialOptions();
                        if (s.TryGetProperty("PortName",  out var pn)) _serialOptions.PortName  = pn.GetString();
                        if (s.TryGetProperty("BaudRate",  out var br)) _serialOptions.BaudRate  = br.GetInt32();
                        if (s.TryGetProperty("AutoOpen",  out var ao)) _serialOptions.AutoOpen  = ao.GetBoolean();
                    }
                    catch (Exception ex) { Debug.WriteLine(ex); _serialOptions = null; }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private void ApplyTimingsToResources()
        {
            try
            {
                Resources["FlashInDuration"]  = new Duration(TimeSpan.FromSeconds(_flashInSeconds));
                Resources["FlashOutDuration"] = new Duration(TimeSpan.FromSeconds(_flashOutSeconds));
                Resources["HoldDuration"]     = new Duration(TimeSpan.FromSeconds(_holdSeconds));
                Resources["PreviewDuration"]  = new Duration(TimeSpan.FromSeconds(_previewSeconds));
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDeveloperMode();
            await StartCameraAsync(0);
            await InitializeSerialAsync();
            TransitionTo(AppState.Idle);
        }

        /// <summary>
        /// Applies window layout and button visibility based on <see cref="_developerMode"/>.
        /// DeveloperMode  = true  → 800×600 centred, both buttons visible at equal 50px width.
        /// DeveloperMode  = false → maximised borderless, both buttons collapsed and zero-width.
        /// </summary>
        private void ApplyDeveloperMode()
        {
            if (_developerMode)
            {
                // Window geometry already set in constructor (before Show).
                // Apply button visibility here after XAML named elements are available.
                const double devButtonWidth = 80;
                colLeft.Width  = new GridLength(devButtonWidth);
                colRight.Width = new GridLength(devButtonWidth);

                bCapture.Width      = devButtonWidth;
                bCapture.Visibility = Visibility.Visible;

                triggerPanel.Visibility = Visibility.Visible;
                foreach (var btn in new[] { bTrigger1, bTrigger2, bTrigger3, bTrigger4, bTrigger5 })
                    btn.Width = devButtonWidth;

                Debug.WriteLine("[DeveloperMode] ON — 800×600 centred, buttons visible");
            }
            else
            {
                // Buttons fully hidden
                colLeft.Width  = new GridLength(0);
                colRight.Width = new GridLength(0);

                bCapture.Width      = 0;
                bCapture.Visibility = Visibility.Collapsed;

                triggerPanel.Visibility = Visibility.Collapsed;
                foreach (var btn in new[] { bTrigger1, bTrigger2, bTrigger3, bTrigger4, bTrigger5 })
                    btn.Width = 0;

                Debug.WriteLine("[DeveloperMode] OFF — maximised kiosk, buttons hidden");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // State machine — single entry point
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The only method that may change <see cref="_state"/>.
        /// Each state has a corresponding Enter method that owns its setup logic.
        /// Completion of that state (animation Completed, timer Tick, task continuation)
        /// calls TransitionTo for the next state.
        /// </summary>
        private void TransitionTo(AppState next)
        {
            Debug.WriteLine($"[State] {_state} → {next}");
            _state = next;

            switch (_state)
            {
                case AppState.Idle:     EnterIdleState();     break;
                case AppState.Countdown:EnterCountdownState();break;
                case AppState.Capture:  EnterCaptureState();  break;
                case AppState.Preview:  EnterPreviewState();  break;
                case AppState.Video:    EnterVideoState();    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Idle — live camera feed, all UI reset
        // No animation. Camera loop drives display continuously.
        // Exits via: bCapture_Click → Countdown | bVideo_Click / serial → Video
        // ─────────────────────────────────────────────────────────────────────

        private void EnterIdleState()
        {
            StopAllStateTimers();

            // Reset flash overlay
            flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            flashOverlay.Opacity = 0;
            flashOverlay.Visibility = Visibility.Collapsed;

            // Reset captured image
            imageControl.Source = null;
            imageControl.Visibility = Visibility.Collapsed;

            // Stop video if playing
            try
            {
                _videoControl?.Stop();
                if (_videoControl is not null)
                    _videoControl.Visibility = Visibility.Collapsed;
            }
            catch { }

            // Clear overlay text
            SetOverlay(string.Empty, 0);

            // Resume live preview
            PreviewControl.Source = null;
            PreviewControl.Visibility = Visibility.Visible;
            if (!_previewLoop.IsEnabled)
                _previewLoop.Start();

            Debug.WriteLine($"[Idle] camera={(_capture is null ? "null" : "ok")}, previewLoop={_previewLoop.IsEnabled}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Countdown — _countdownTimer ticks every 1 s, CountdownNumberInOut
        // animation plays on each tick.
        // Exits via: _countdownTimer when counter reaches 0 → Capture
        // ─────────────────────────────────────────────────────────────────────

        private void EnterCountdownState()
        {
            imageControl.Visibility = Visibility.Collapsed;

            _countdownCounter = CountdownSeconds;
            ShowCountdownTick();                     // display first number immediately
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

            // Counter reached zero → move to Capture
            _countdownTimer.Stop();
            TransitionTo(AppState.Capture);
        }

        private void ShowCountdownTick()
        {
            SetOverlay(_countdownCounter.ToString(), 1);
            PlayAnimation("CountdownNumberInOut");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Capture — screen flashes white (FlashInFast), frame is grabbed at
        // peak white, then FlashOutSmooth reveals the captured image.
        // FlashInFast.Completed → grab frame → FlashOutSmooth.Completed → Preview
        // ─────────────────────────────────────────────────────────────────────

        private void EnterCaptureState()
        {
            SetOverlay(string.Empty, 0);
            PlayAnimation("FlashInFast", onCompleted: OnFlashInCompleted);
        }

        private void OnFlashInCompleted(object? sender, EventArgs e)
        {
            if (_state != AppState.Capture) return;

            // Grab frame at peak white — subject has reacted to flash,
            // white overlay hides any camera lag.
            Mat? src;
            lock (_sync) { src = _frameFull?.Clone(); }

            if (src is null || src.Empty())
            {
                Debug.WriteLine("[Capture] No frame available — returning to Idle");
                SetOverlay("No frame", 1);
                src?.Dispose();
                TransitionTo(AppState.Idle);
                return;
            }

            var bitmap = src.ToBitmapSource();
            bitmap.Freeze();
            src.Dispose();

            SaveCaptureAsync(bitmap);

            // Stage captured image behind the white overlay before fading out
            imageControl.Source = bitmap;
            imageControl.Visibility = Visibility.Visible;

            Debug.WriteLine("[Capture] Frame grabbed — starting FlashOutSmooth");

            // Clear any held opacity value left by FlashInFast (FillBehavior=Stop)
            flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);

            PlayAnimation("FlashOutSmooth", onCompleted: OnFlashOutCompleted);
        }

        private void OnFlashOutCompleted(object? sender, EventArgs e)
        {
            if (_state != AppState.Capture) return;

            Debug.WriteLine("[Capture] FlashOutSmooth completed → Preview");
            TransitionTo(AppState.Preview);
        }

        private void SaveCaptureAsync(BitmapSource bitmap)
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

        // ─────────────────────────────────────────────────────────────────────
        // Preview — frozen captured image shown with horizontal mirror.
        // _previewTimer drives automatic return to Idle after _previewSeconds.
        // Exits via: _previewTimer.Tick → Idle (via RestartCameraAndReturnToIdle)
        // ─────────────────────────────────────────────────────────────────────

        private void EnterPreviewState()
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_previewSeconds) };
            _previewTimer.Tick += OnPreviewTimerTick;
            _previewTimer.Start();

            Debug.WriteLine($"[Preview] Showing captured image for {_previewSeconds}s");
        }

        private void OnPreviewTimerTick(object? sender, EventArgs e)
        {
            _previewTimer?.Stop();
            _previewTimer = null;

            if (_state != AppState.Preview) return;

            Debug.WriteLine("[Preview] Timer elapsed → restarting camera and returning to Idle");
            RestartCameraAndReturnToIdle();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Video — MediaElement plays media\video.mp4.
        // Exits via: MediaEnded event | _videoTimer (duration cap) → Idle
        // ─────────────────────────────────────────────────────────────────────

        private void EnterVideoState()
        {
            imageControl.Visibility = Visibility.Collapsed;
            PreviewControl.Source = null;

            if (_videoControl is not null)
                _videoControl.Visibility = Visibility.Visible;

            PlayLocalVideo("media\\video.mp4");
        }

        private void videoControl_MediaEnded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[Video] MediaEnded → Idle");
            _videoTimer?.Stop();
            _videoTimer = null;
            TransitionTo(AppState.Idle);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Animation helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clones the named storyboard resource, optionally wires a Completed handler,
        /// then begins it against this window.
        /// </summary>
        private void PlayAnimation(string resourceKey, EventHandler? onCompleted = null)
        {
            try
            {
                var sb = ((Storyboard)FindResource(resourceKey)).Clone();
                if (onCompleted is not null)
                    sb.Completed += onCompleted;
                sb.Begin(this, true);
            }
            catch (Exception ex) { Debug.WriteLine($"[Animation] {resourceKey} failed: {ex}"); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Timer helpers
        // ─────────────────────────────────────────────────────────────────────

        private void StopAllStateTimers()
        {
            _countdownTimer.Stop();

            _previewTimer?.Stop();
            _previewTimer = null;

            _videoTimer?.Stop();
            _videoTimer = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Live preview loop (always running except during Preview / Video)
        // ─────────────────────────────────────────────────────────────────────

        private void OnPreviewLoopTick(object? sender, EventArgs e)
        {
            if (_state == AppState.Preview || _state == AppState.Video) return;

            VideoCapture? cap;
            Mat? full;
            Mat? prev;

            lock (_sync)
            {
                cap  = _capture;
                full = _frameFull;
                prev = _framePreview;
            }

            if (cap is null || full is null || prev is null) return;

            if (!cap.Read(full) || full.Empty())
            {
                Debug.WriteLine("[PreviewLoop] Failed to read frame");
                return;
            }

            try
            {
                Cv2.Resize(full, prev, new OpenCvSharp.Size(PreviewWidth, PreviewHeight));
                var bmp = BitmapSourceConverter.ToBitmapSource(prev);
                bmp.Freeze();
                PreviewControl.Source = bmp;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // UI input — buttons and serial
        // ─────────────────────────────────────────────────────────────────────

        private void bCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            TransitionTo(AppState.Countdown);
        }

        // ── Trigger buttons (developer mode) ─────────────────────────────────
        // General-purpose triggers — wire each to the desired state transition.

        private void bTrigger1_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            Debug.WriteLine("[Dev] Trigger 1 fired");
            TransitionTo(AppState.Video);  // currently: video
        }

        private void bTrigger2_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            Debug.WriteLine("[Dev] Trigger 2 fired");
        }

        private void bTrigger3_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            Debug.WriteLine("[Dev] Trigger 3 fired");
        }

        private void bTrigger4_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            Debug.WriteLine("[Dev] Trigger 4 fired");
        }

        private void bTrigger5_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            Debug.WriteLine("[Dev] Trigger 5 fired");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Serial
        // ─────────────────────────────────────────────────────────────────────

        private async Task InitializeSerialAsync()
        {
            try
            {
                if (_serialOptions is null) return;

                _serialService = new SerialService(_serialOptions);
                _serialService.LineReceived   += Serial_LineReceived;
                _serialService.StatusChanged  += Serial_StatusChanged;

                if (_serialOptions.AutoOpen)
                {
                    bool ok = await _serialService.OpenAsync();
                    Debug.WriteLine($"[Serial] Open ({_serialOptions.PortName}@{_serialOptions.BaudRate}) → {ok}");

                    if (!ok)
                        SetOverlay($"Serial {_serialOptions.PortName ?? "?"} not open", 1);
                    else
                    {
                        SetOverlay("Serial ready", 1);
                        _ = Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => SetOverlay(string.Empty, 0)));
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
                Debug.WriteLine($"[Serial] ← {line}");

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

            if (val.Equals("TAP",  StringComparison.OrdinalIgnoreCase))
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

        // ─────────────────────────────────────────────────────────────────────
        // Camera lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private async Task StartCameraAsync(int cameraIndex)
        {
            if (_capture is not null && _capture.IsOpened()) return;

            await StopCameraAsync();

            VideoCapture? cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                cap.Release(); cap.Dispose();
                cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
            }
            if (!cap.IsOpened())
            {
                cap.Release(); cap.Dispose();
                SetOverlay("Camera not found", 1);
                return;
            }

            TrySetHighestResolution(cap);

            var env = Environment.GetEnvironmentVariable("PhotoBoothWorking");
            if (string.IsNullOrWhiteSpace(env))
                env = Path.Combine(AppContext.BaseDirectory, "Output");
            try { Directory.CreateDirectory(env); } catch { }
            _workDir = env;

            lock (_sync) { _capture = cap; }

            _frameFull?.Dispose();
            _framePreview?.Dispose();
            _frameFull   = new Mat((int)cap.FrameHeight, (int)cap.FrameWidth, MatType.CV_8UC3);
            _framePreview = new Mat(PreviewHeight, PreviewWidth, MatType.CV_8UC3);

            imageControl.Visibility = Visibility.Collapsed;
            _previewLoop.Start();
        }

        private async Task StopCameraAsync()
        {
            _previewLoop.Stop();
            StopAllStateTimers();

            // Dispose serial
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

            // Release camera
            VideoCapture? cap;
            lock (_sync) { cap = _capture; _capture = null; }
            try { cap?.Release(); cap?.Dispose(); } catch { }

            _frameFull?.Dispose();  _frameFull   = null;
            _framePreview?.Dispose(); _framePreview = null;

            // Reset UI
            flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            flashOverlay.Opacity    = 0;
            flashOverlay.Visibility = Visibility.Collapsed;
            imageControl.Source     = null;
            imageControl.Visibility = Visibility.Collapsed;

            try { _videoControl?.Stop(); } catch { }
            if (_videoControl is not null) _videoControl.Visibility = Visibility.Collapsed;
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

        private void RestartCameraAndReturnToIdle()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        StopAllStateTimers();
                        await StopCameraAsync();
                        await StartCameraAsync(0);
                        TransitionTo(AppState.Idle);
                    });
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Video playback helpers
        // ─────────────────────────────────────────────────────────────────────

        private void PlayLocalVideo(string relativePath)
        {
            try
            {
                string? resolved = ResolveProjectRelativePath(relativePath);
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    Debug.WriteLine($"[Video] File not found: {relativePath}");
                    SetOverlay("Video not found", 1);
                    TransitionTo(AppState.Idle);
                    return;
                }

                if (_videoControl is null)
                {
                    Debug.WriteLine("[Video] videoControl is null");
                    TransitionTo(AppState.Idle);
                    return;
                }

                _videoControl.Source          = new Uri(resolved);
                _videoControl.LoadedBehavior  = MediaState.Manual;
                _videoControl.UnloadedBehavior = MediaState.Stop;
                _videoControl.Position        = TimeSpan.Zero;
                _videoControl.Play();

                if (_videoDurationSeconds > 0)
                {
                    _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_videoDurationSeconds) };
                    _videoTimer.Tick += OnVideoTimerTick;
                    _videoTimer.Start();
                    Debug.WriteLine($"[Video] Playing with {_videoDurationSeconds}s cap");
                }
                else
                {
                    Debug.WriteLine("[Video] Playing to natural end");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video] Error: {ex}");
                SetOverlay("Video error", 1);
                TransitionTo(AppState.Idle);
            }
        }

        private void OnVideoTimerTick(object? sender, EventArgs e)
        {
            _videoTimer?.Stop();
            _videoTimer = null;

            Debug.WriteLine("[Video] Duration cap elapsed → Idle");
            try { _videoControl?.Stop(); } catch { }
            TransitionTo(AppState.Idle);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Overlay helper
        // ─────────────────────────────────────────────────────────────────────

        private void SetOverlay(string text, double opacity)
        {
            tbOverlay.Text    = text;
            tbOverlay.Opacity = opacity;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Path resolution
        // ─────────────────────────────────────────────────────────────────────

        private string? ResolveProjectRelativePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath)) return relativePath;

            var candidate = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir.Parent is not null; i++)
            {
                dir = dir.Parent;
                candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Window lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected override async void OnClosed(EventArgs e)
        {
            await StopCameraAsync();
            base.OnClosed(e);
        }
    }
}
