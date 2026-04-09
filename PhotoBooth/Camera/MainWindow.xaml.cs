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
        // All bool-marker DPs removed. CaptureMarker and FadeOutMarker are now
        // driven by DispatcherTimer offsets and storyboard Completed events.

        private const int CountdownSeconds = 6;
        private const int TargetFps = 30;
        private const int PreviewWidth = 1920;
        private const int PreviewHeight = 1080;

        // timing config values (seconds)
        private double _flashInSeconds = 1.0;
        private double _flashOutSeconds = 2.0;
        private double _holdSeconds = 0.3;
        private double _previewSeconds = 10.0;
        private double _videoDurationSeconds = 0.0; // 0 = use full file duration

        private readonly object _sync = new();
        private readonly DispatcherTimer _previewLoop = new();
        private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private VideoCapture? _capture;
        private AppState _state = AppState.Idle;
        private int _counter;
        private string? _workDir;

        private Mat? _frameFull;
        private Mat? _framePreview;

        private Storyboard? _runningFlashIn;

        // MediaElement from XAML
        private MediaElement? _videoControl;

        // timer used to limit video playback when VideoDurationSeconds > 0
        private DispatcherTimer? _videoTimer;

        // timer that drives preview timeout → window restart
        private DispatcherTimer? _previewFallbackTimer;

        // timer used for the hold delay between snapshot and flash-out
        private DispatcherTimer? _holdTimer;

        // Serial support
        private ISerialService? _serialService;
        private SerialOptions? _serialOptions;

        public MainWindow()
        {
            InitializeComponent();

            // get reference to named MediaElement in XAML
            _videoControl = (MediaElement?)FindName("videoControl");

            LoadTimingConfiguration();
            ApplyTimingsToResources();

            _previewLoop.Interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
            _previewLoop.Tick += OnPreviewLoop;

            _countdownTimer.Tick += OnCountdownTick;
        }

        private void LoadTimingConfiguration()
        {
            try
            {
                // Try to find appsettings.json in project tree (same approach as ResolveProjectRelativePath)
                string? path = ResolveProjectRelativePath("appsettings.json");
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                }

                if (!File.Exists(path)) return;
                var txt = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                if (root.TryGetProperty("FlashInSeconds", out var v1)) _flashInSeconds = v1.GetDouble();
                if (root.TryGetProperty("FlashOutSeconds", out var v2)) _flashOutSeconds = v2.GetDouble();
                if (root.TryGetProperty("HoldSeconds", out var v3)) _holdSeconds = v3.GetDouble();
                if (root.TryGetProperty("PreviewSeconds", out var v4)) _previewSeconds = v4.GetDouble();
                if (root.TryGetProperty("VideoDurationSeconds", out var v5)) _videoDurationSeconds = v5.GetDouble();

                // Load serial options if present
                if (root.TryGetProperty("Serial", out var s))
                {
                    try
                    {
                        _serialOptions = new SerialOptions();
                        if (s.TryGetProperty("PortName", out var pn)) _serialOptions.PortName = pn.GetString();
                        if (s.TryGetProperty("BaudRate", out var br)) _serialOptions.BaudRate = br.GetInt32();
                        if (s.TryGetProperty("AutoOpen", out var ao)) _serialOptions.AutoOpen = ao.GetBoolean();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        _serialOptions = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void ApplyTimingsToResources()
        {
            try
            {
                Resources["FlashInDuration"] = new Duration(TimeSpan.FromSeconds(_flashInSeconds));
                Resources["FlashOutDuration"] = new Duration(TimeSpan.FromSeconds(_flashOutSeconds));
                Resources["HoldDuration"] = new Duration(TimeSpan.FromSeconds(_holdSeconds));
                Resources["PreviewDuration"] = new Duration(TimeSpan.FromSeconds(_previewSeconds));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await StartAsync(0);
            // initialize serial support after camera startup
            await InitializeSerialAsync();
            SetState(AppState.Idle);
        }

        private async Task InitializeSerialAsync()
        {
            try
            {
                if (_serialOptions is null) return;

                _serialService = new SerialService(_serialOptions);
                _serialService.LineReceived += Serial_LineReceived;
                _serialService.StatusChanged += Serial_StatusChanged;

                if (_serialOptions.AutoOpen)
                {
                    bool ok = await _serialService.OpenAsync();
                    Debug.WriteLine($"Serial open attempt ({_serialOptions.PortName}@{_serialOptions.BaudRate}) -> {ok}");
                    if (!ok)
                    {
                        SetOverlay($"Serial {(_serialOptions.PortName ?? "?")} not open", 1);
                    }
                    else
                    {
                        SetOverlay("Serial ready", 1);
                        _ = Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => SetOverlay("", 0)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void Serial_StatusChanged(object? sender, SerialStatusEventArgs e)
        {
            Trace.WriteLine($"[Serial] {e}");
        }

        private void Serial_LineReceived(object? sender, string e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e)) return;
                Debug.WriteLine($"Serial line received: {e}");

                var line = e.Trim();

                // --- Protocol event lines: EVENT:TRIGGER (break beam) ---
                if (line.StartsWith("EVENT:", StringComparison.OrdinalIgnoreCase))
                {
                    var eventVal = line.Substring(6).Trim();
                    if (eventVal.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            Debug.WriteLine("Serial EVENT:TRIGGER (break beam) -> StartVideoMode");
                            if (_state == AppState.Idle)
                                StartVideoMode();
                            else
                                Debug.WriteLine($"EVENT:TRIGGER ignored, current state={_state}");
                        });
                    }
                    return;
                }

                // --- DEBUG: Status lines: parse ManualButtonEvent and BreakBeamTriggered ---
                if (line.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                {
                    var upper = line.ToUpperInvariant();

                    // ManualButtonEvent: TAP  -> video
                    // ManualButtonEvent: LONG -> photo countdown
                    int mbi = upper.IndexOf("MANUALBUTTONEVENT", StringComparison.Ordinal);
                    if (mbi >= 0)
                    {
                        int colon = upper.IndexOf(':', mbi);
                        if (colon >= 0 && colon + 1 < upper.Length)
                        {
                            var val = upper.Substring(colon + 1)
                                          .Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                          .FirstOrDefault()?.Trim();

                            if (!string.IsNullOrEmpty(val) && !val.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                            {
                                if (val.Equals("TAP", StringComparison.OrdinalIgnoreCase))
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        Debug.WriteLine("ManualButtonEvent: TAP -> StartVideoMode");
                                        if (_state == AppState.Idle)
                                            StartVideoMode();
                                    });
                                    return;
                                }

                                if (val.Equals("LONG", StringComparison.OrdinalIgnoreCase))
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        Debug.WriteLine("ManualButtonEvent: LONG -> StartCountdownMode (photo)");
                                        if (_state == AppState.Idle)
                                            StartCountdownMode();
                                    });
                                    return;
                                }
                            }
                        }
                    }

                    // BreakBeamTriggered: YES -> video
                    int bbi = upper.IndexOf("BREAKBEAMTRIGGERED", StringComparison.Ordinal);
                    if (bbi >= 0)
                    {
                        int colon = upper.IndexOf(':', bbi);
                        if (colon >= 0 && colon + 1 < upper.Length)
                        {
                            var val = upper.Substring(colon + 1)
                                          .Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                          .FirstOrDefault()?.Trim();

                            if (!string.IsNullOrEmpty(val) &&
                                !val.Equals("NO", StringComparison.OrdinalIgnoreCase) &&
                                !val.Equals("FALSE", StringComparison.OrdinalIgnoreCase) &&
                                !val.Equals("0", StringComparison.OrdinalIgnoreCase))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    Debug.WriteLine($"BreakBeamTriggered: {val} -> StartVideoMode");
                                    if (_state == AppState.Idle)
                                        StartVideoMode();
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private async Task StartAsync(int cameraIndex)
        {
            if (_capture is not null && _capture.IsOpened()) return;

            await StopAsync();

            VideoCapture? cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                cap.Release();
                cap.Dispose();
                cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
            }
            if (!cap.IsOpened())
            {
                cap.Release();
                cap.Dispose();
                SetOverlay("Camera not found", 1);
                return;
            }

            TrySetHighestResolution(cap);

            var env = Environment.GetEnvironmentVariable("PhotoBoothWorking");
            Debug.WriteLine($"PhotoBoothWorking: {env}");
            if (string.IsNullOrWhiteSpace(env))
                env = Path.Combine(AppContext.BaseDirectory, "Output");
            try { Directory.CreateDirectory(env); } catch { }
            _workDir = env;

            lock (_sync) { _capture = cap; }

            _frameFull?.Dispose();
            _framePreview?.Dispose();

            int captureW = (int)cap.FrameWidth;
            int captureH = (int)cap.FrameHeight;

            _frameFull = new Mat(captureH, captureW, MatType.CV_8UC3);
            _framePreview = new Mat(PreviewHeight, PreviewWidth, MatType.CV_8UC3);

            imageControl.Visibility = Visibility.Collapsed;
            _previewLoop.Start();
        }

        private void TrySetHighestResolution(VideoCapture cap)
        {
            (int w, int h)[] tries =
            {
                (7680,4320),
                (5120,2880),
                (4096,2160),
                (3840,2160),
                (2560,1440),
                (1920,1080)
            };

            foreach (var t in tries)
            {
                cap.FrameWidth = t.w;
                cap.FrameHeight = t.h;
                if ((int)cap.FrameWidth == t.w && (int)cap.FrameHeight == t.h) break;
            }
        }

        private async Task StopAsync()
        {
            _runningFlashIn?.Stop(this);
            _runningFlashIn = null;

            _previewLoop.Stop();
            _countdownTimer.Stop();

            // dispose serial service
            try
            {
                if (_serialService is not null)
                {
                    _serialService.LineReceived -= Serial_LineReceived;
                    _serialService.StatusChanged -= Serial_StatusChanged;
                    _serialService.Dispose();
                }
            }
            catch { }
            _serialService = null;

            // stop and dispose video timer
            try { _videoTimer?.Stop(); } catch { }
            _videoTimer = null;
            try { _previewFallbackTimer?.Stop(); } catch { }
            _previewFallbackTimer = null;

            // stop hold timer
            try { _holdTimer?.Stop(); } catch { }
            _holdTimer = null;

            VideoCapture? cap;
            lock (_sync)
            {
                cap = _capture;
                _capture = null;
            }

            try
            {
                if (cap is not null)
                {
                    cap.Release();
                    cap.Dispose();
                }
            }
            catch { }

            _frameFull?.Dispose();
            _framePreview?.Dispose();
            _frameFull = null;
            _framePreview = null;

            flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            flashOverlay.Opacity = 0;
            flashOverlay.Visibility = Visibility.Collapsed;
            imageControl.Source = null;
            imageControl.Visibility = Visibility.Collapsed;

            // stop video if playing
            try
            {
                _videoControl?.Stop();
                if (_videoControl is not null)
                    _videoControl.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void OnPreviewLoop(object? sender, EventArgs e)
        {
            if (_state == AppState.Preview || _state == AppState.Video) return;

            VideoCapture? cap;
            Mat? full;
            Mat? prev;

            lock (_sync)
            {
                cap = _capture;
                full = _frameFull;
                prev = _framePreview;
            }

            // Avoid noisy per-frame logging; only log on problems
            if (cap is null || full is null || prev is null) return;
            if (!cap.Read(full) || full.Empty())
            {
                Debug.WriteLine("OnPreviewLoop: failed to read frame from capture");
                return;
            }

            try
            {
                Cv2.Resize(full, prev, new OpenCvSharp.Size(PreviewWidth, PreviewHeight));
                var bmp = BitmapSourceConverter.ToBitmapSource(prev);
                bmp.Freeze();
                PreviewControl.Source = bmp;
                // intentionally no per-frame debug log to reduce noise
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void bGo_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            // allow starting either countdown or video; here we'll start Countdown as before
            _counter = CountdownSeconds;
            SetOverlay(_counter.ToString(), 1);
            PlayCountdownNumberAnimation();
            SetState(AppState.Countdown);
            OnCountdownTick(null, EventArgs.Empty);
        }

        // New capture button handler
        private void bCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            StartCountdownMode();
        }

        // New video button handler
        private void bVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_state != AppState.Idle) return;
            StartVideoMode();
        }

        private void StartCountdownMode()
        {
            _counter = CountdownSeconds;
            SetOverlay(_counter.ToString(), 1);
            PlayCountdownNumberAnimation();
            SetState(AppState.Countdown);
            OnCountdownTick(null, EventArgs.Empty);
        }

        private void StartVideoMode()
        {
            SetState(AppState.Video);
            // new location: project root\media\video.mp4
            PlayLocalVideo("media\\video.mp4");
        }

        private void OnCountdownTick(object? sender, EventArgs e)
        {
            if (_state != AppState.Countdown) return;

            if (_counter > 1)
            {
                _counter--;
                SetOverlay(_counter.ToString(), 1);
                PlayCountdownNumberAnimation();
                return;
            }

            _countdownTimer.Stop();
            SetState(AppState.Capture);
        }

        private async void CaptureSnapshot()
        {
            Mat? src;
            lock (_sync)
            {
                src = _frameFull?.Clone();
            }

            if (src is null || src.Empty())
            {
                SetOverlay("No frame", 1);
                src?.Dispose();
                return;
            }

            var bmpOut = src.ToBitmapSource();
            bmpOut.Freeze();

            var dir = string.IsNullOrWhiteSpace(_workDir) ? AppContext.BaseDirectory : _workDir;
            var name = $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var path = Path.Combine(dir, name);

            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmpOut));
                    using var fs = File.Create(path);
                    enc.Save(fs);
                }
                catch { }
            });

            imageControl.Source = bmpOut;
            imageControl.Visibility = Visibility.Visible;
            SetOverlay("Saved", 1);

            // Start hold timer for flash-out delay
            try
            {
                _holdTimer?.Stop();
                _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_holdSeconds) };
                _holdTimer.Tick += (s, e) =>
                {
                    _holdTimer?.Stop();
                    _holdTimer = null;
                    Debug.WriteLine("Hold timer elapsed, starting flash-out");
                    // flash-out handled by storyboard in OnCaptureComplete
                    OnCaptureComplete();
                };
                _holdTimer.Start();
                Debug.WriteLine($"Hold timer started ({_holdSeconds}s).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void OnCaptureComplete()
        {
            Debug.WriteLine("OnCaptureComplete fired");

            _runningFlashIn?.Stop(this);
            _runningFlashIn = null;

            flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);

            try
            {
                var outSb = ((Storyboard)FindResource("FlashOutSmooth")).Clone();
                outSb.Completed += (_, __) =>
                {
                    Debug.WriteLine("FlashOutSmooth completed, moving to Preview state");
                    SetState(AppState.Preview);
                };
                outSb.CurrentStateInvalidated += (s, e) => Debug.WriteLine($"FlashOutSmooth state: {((Clock)s).CurrentState}");
                Debug.WriteLine("Starting FlashOutSmooth storyboard");
                outSb.Begin(this, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private async Task RestartCameraAsync()
        {
            Debug.WriteLine("RestartCameraAsync: reopening camera...");
            try
            {
                await StopAsync();
                await StartAsync(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void SetState(AppState next)
        {
            Debug.WriteLine($"SetState: {_state} -> {next}");
            _state = next;

            if (_state == AppState.Idle)
            {
                SetOverlay("", 0);

                _runningFlashIn?.Stop(this);
                _runningFlashIn = null;

                flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                flashOverlay.Opacity = 0;
                flashOverlay.Visibility = Visibility.Collapsed;

                // stop video if it was playing
                try
                {
                    _videoControl?.Stop();
                    if (_videoControl is not null)
                        _videoControl.Visibility = Visibility.Collapsed;
                }
                catch { }

                // Ensure preview loop is running and preview control will be refreshed
                try
                {
                    if (!_previewLoop.IsEnabled)
                        _previewLoop.Start();
                    // clear any frozen preview image so next frame is displayed
                    PreviewControl.Source = null;
                    // ensure the preview image control is visible so frames are shown
                    try { PreviewControl.Visibility = Visibility.Visible; } catch { }
                    Debug.WriteLine($"Entered Idle: preview loop started={_previewLoop.IsEnabled}, capture={( _capture is null ? "null" : "ok") }");
                    // reset any mirroring/transform applied during preview so live feed appears normally
                    try
                    {
                        PreviewControl.RenderTransform = null;
                        imageControl.RenderTransform = null;
                    }
                    catch { }
                    Debug.WriteLine("Entered Idle: preview loop started and preview cleared.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }

                return;
            }

            if (_state == AppState.Countdown)
            {
                imageControl.Visibility = Visibility.Collapsed;
                if (!_countdownTimer.IsEnabled) _countdownTimer.Start();
                return;
            }

            if (_state == AppState.Capture)
            {
                CaptureSnapshot();
                return;
            }

            if (_state == AppState.Preview)
            {
                // apply horizontal flip to the static preview so it matches typical mirrored live previews
                try
                {
                    imageControl.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                    imageControl.RenderTransform = new ScaleTransform(-1, 1);
                }
                catch { }

                // Single authoritative timer — drives the preview timeout and window restart
                try
                {
                    _previewFallbackTimer?.Stop();
                    _previewFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_previewSeconds) };
                    _previewFallbackTimer.Tick += (s, e) =>
                    {
                        _previewFallbackTimer?.Stop();
                        _previewFallbackTimer = null;
                        Debug.WriteLine("Preview timer elapsed, invoking OnPreviewDoneMarker.");
                        if (_state == AppState.Preview)
                            OnPreviewDoneMarker();
                    };
                    _previewFallbackTimer.Start();
                    Debug.WriteLine($"Entered Preview state, started timer ({_previewSeconds}s).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                return;
            }

            if (_state == AppState.Video)
            {
                // prepare UI for video playback
                imageControl.Visibility = Visibility.Collapsed;
                if (_videoControl is not null)
                    _videoControl.Visibility = Visibility.Visible;
                PreviewControl.Source = null;
                return;
            }
        }

        private void PlayCountdownNumberAnimation()
        {
            var sb = ((Storyboard)FindResource("CountdownNumberInOut")).Clone();
            sb.Begin(this, true);
        }

        private void SetOverlay(string text, double opacity)
        {
            tbOverlay.Text = text;
            tbOverlay.Opacity = opacity;
        }

        private void PlayLocalVideo(string relativePath)
        {
            try
            {
                // Resolve video location. The file is expected at projectRoot\media\video.mp4
                string? resolved = ResolveProjectRelativePath(relativePath);
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    SetOverlay("Video not found", 1);
                    SetState(AppState.Idle);
                    return;
                }

                if (_videoControl is null)
                {
                    SetOverlay("Video control missing", 1);
                    SetState(AppState.Idle);
                    return;
                }

                _videoControl.Source = new Uri(resolved);
                _videoControl.LoadedBehavior = MediaState.Manual;
                _videoControl.UnloadedBehavior = MediaState.Stop;
                _videoControl.Position = TimeSpan.Zero;
                _videoControl.Play();

                // if a bounded duration configured, start/replace timer to stop playback
                try
                {
                    _videoTimer?.Stop();
                    _videoTimer = null;
                    if (_videoDurationSeconds > 0)
                    {
                        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_videoDurationSeconds) };
                        _videoTimer.Tick += (s, e) =>
                        {
                            _videoTimer?.Stop();
                            _videoTimer = null;
                            try { _videoControl?.Stop(); } catch { }
                            SetState(AppState.Idle);
                        };
                        _videoTimer.Start();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                SetOverlay("Video error", 1);
                SetState(AppState.Idle);
            }
        }

        private string? ResolveProjectRelativePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath)) return relativePath;

            // check base directory first
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, relativePath);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

            // walk up parent directories (up to 6 levels) to find project root where 'media\video.mp4' may exist
            var dirInfo = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dirInfo.Parent is not null; i++)
            {
                dirInfo = dirInfo.Parent;
                candidate = Path.Combine(dirInfo.FullName, relativePath);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            return null;
        }

        private void videoControl_MediaEnded(object sender, RoutedEventArgs e)
        {
            // when video ends return to idle
            try { _videoTimer?.Stop(); } catch { }
            _videoTimer = null;
            try { _previewFallbackTimer?.Stop(); } catch { }
            _previewFallbackTimer = null;
            SetState(AppState.Idle);
        }

        protected override async void OnClosed(EventArgs e)
        {
            await StopAsync();
            base.OnClosed(e);
        }

        private void OnPreviewDoneMarker()
        {
            Debug.WriteLine("OnPreviewDoneMarker fired");

            try { _previewFallbackTimer?.Stop(); } catch { }
            _previewFallbackTimer = null;

            try
            {
                imageControl.Source = null;
                imageControl.Visibility = Visibility.Collapsed;

                try
                {
                    imageControl.RenderTransform = null;
                    PreviewControl.RenderTransform = null;
                }
                catch { }

                try
                {
                    flashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                    flashOverlay.Opacity = 0;
                    flashOverlay.Visibility = Visibility.Collapsed;
                }
                catch { }

                RestartWindow();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                try { RestartWindow(); } catch { SetState(AppState.Idle); }
            }
        }

        private void RestartWindow()
        {
            Debug.WriteLine("RestartWindow fired");

            // implement foolproof restart: reopen camera, reset state, and ensure idle display
            _ = Task.Run(async () =>
            {
                try
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        // stop any running timers
                        try { _previewFallbackTimer?.Stop(); } catch { }
                        try { _videoTimer?.Stop(); } catch { }
                        try { _holdTimer?.Stop(); } catch { }

                        // restart camera
                        await RestartCameraAsync();

                        // reset state to idle
                        SetState(AppState.Idle);
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            });
        }

        private void DoSnapshot()
        {
            Debug.WriteLine("DoSnapshot marker fired");
            // ignore if not in preview state
            if (_state != AppState.Preview) return;

            // Ensure render transforms are cleared before snapshot
            try
            {
                imageControl.RenderTransform = null;
                PreviewControl.RenderTransform = null;
            }
            catch { }

            // snapshot exported regardless of file type settings
            const string forcePngName = "snapshot_{0:yyyyMMdd_HHmmss_fff}.png";
            var dir = string.IsNullOrWhiteSpace(_workDir) ? AppContext.BaseDirectory : _workDir;
            var forcePngPath = Path.Combine(dir, string.Format(forcePngName, DateTime.Now));

            OnCaptureComplete(); // just perform flash-out

            // run snapshot in background, ignore errors
            _ = Task.Run(() =>
            {
                try
                {
                    // always export to PNG format
                    var pngPath = Path.ChangeExtension(forcePngPath, ".png");

                    // small delay to ensure previous frame processing is complete
                    Task.Delay(100).Wait();

                    lock (_sync)
                    {
                        // direct pixel access for high performance
                        if (_frameFull is not null && !_frameFull.Empty())
                        {
                            using var tmp = _frameFull.Clone();
                            tmp.SaveImage(pngPath);
                            Debug.WriteLine($"Snapshot saved: {pngPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Snapshot error: {ex}");
                }
            });
        }

        private void StartFlashOut()
        {
            Debug.WriteLine("StartFlashOut marker fired");

            // if in preview state, just perform flash-out
            if (_state == AppState.Preview)
            {
                OnCaptureComplete();
                return;
            }

            // if in video state, stop video and perform flash-out
            if (_state == AppState.Video)
            {
                try { _videoControl?.Stop(); } catch { }
                OnCaptureComplete();
                return;
            }

            Debug.WriteLine($"StartFlashOut: ignored, invalid state {_state}");
        }
    }
}
