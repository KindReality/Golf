using System.Drawing;
using System.IO;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// Called on the display manager's dispatcher every second. One owned Standalone per display.
internal sealed class VisualizationManager : IAsyncDisposable
{
    private sealed class Instance
    {
        public VisualizationProcess? Owner;
        public Rectangle Bounds;
        public string Path = "", ScreenId = "";
        public DateTimeOffset Started, RetryAt;
        public int Failures;
        public bool Requested;
        public nint Fullscreen;
        public Task? Stopping;
    }
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
    internal int[] ProcessIds => _instances.Values.Where(i => i.Owner is not null).Select(i => i.Owner!.Process.Id).ToArray();

    internal static string Executable(VisualizationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            var configured = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ExecutablePath));
            if (!File.Exists(configured))
                throw new FileNotFoundException("Configured G-Force Standalone is unavailable.", configured);
            return configured;
        }
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SoundSpectrum", "G-Force");
        foreach (var name in new[] { "G-Force Standalone.x64.exe", "G-Force Standalone.exe" })
        {
            var path = System.IO.Path.Combine(folder, name);
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("Install SoundSpectrum G-Force Standalone or set Visualization.ExecutablePath.");
    }

    internal void Refresh(ExperienceOptions options, IEnumerable<MainWindow> windows)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            var definition = options.ScreensFor(window.DisplayName).FirstOrDefault(s => s.IsVisualization);
            var display = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(window.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (definition is null || display is null || !window.Visible || window.State is RendererState.Failed or RendererState.Starting)
                continue;
            desired.Add(window.DisplayName);
            if (!_instances.TryGetValue(window.DisplayName, out var state))
                _instances[window.DisplayName] = state = new();
            if (state.Stopping is { IsCompleted: false })
                continue;
            state.Stopping = null;
            var now = DateTimeOffset.UtcNow;
            if (now < state.RetryAt)
                continue;
            try
            {
                var path = Executable(options.Visualization);
                if (state.Owner is not null && (state.Bounds != display.Bounds || state.Path != path || state.ScreenId != definition.Id))
                {
                    Stop(window.DisplayName, state, "ConfigurationChanged");
                    continue;
                }
                if (state.Owner is null)
                {
                    state.Owner = VisualizationProcess.Start(path);
                    state.Bounds = display.Bounds;
                    state.Path = path;
                    state.ScreenId = definition.Id;
                    state.Started = now;
                    state.Requested = false;
                    state.Fullscreen = 0;
                    Telemetry.Info("VisualizationStarting", new
                    {
                        window.DisplayName,
                        ScreenName = definition.Name,
                        ProcessId = state.Owner.Process.Id,
                        Path = path,
                        Fullscreen = true
                    });
                }
                var process = state.Owner.Process;
                if (process.HasExited)
                    throw new InvalidOperationException($"G-Force exited with code {process.ExitCode}.");
                var fullscreen = GForceWindow.Find(process.Id, GForceWindow.FullscreenClass);
                if (fullscreen != 0)
                {
                    if (GForceWindow.IsHung(fullscreen))
                        throw new InvalidOperationException("G-Force is not responding to window messages.");
                    if (GForceWindow.Bounds(fullscreen) != display.Bounds)
                        throw new InvalidOperationException($"G-Force fullscreen opened at {GForceWindow.Bounds(fullscreen)} instead of {display.Bounds}.");
                    if (state.Fullscreen == 0)
                    {
                        state.Fullscreen = fullscreen;
                        Telemetry.Info("VisualizationFullscreenReady", new
                        {
                            window.DisplayName,
                            ScreenName = definition.Name,
                            ProcessId = process.Id,
                            Bounds = display.Bounds
                        });
                    }
                    // Reset backoff only after a stable run, not a momentary recovery.
                    if (now - state.Started > TimeSpan.FromSeconds(30))
                        state.Failures = 0;
                    GForceWindow.Behind(fullscreen, window.Handle);
                    continue;
                }
                if (state.Fullscreen != 0)
                    throw new InvalidOperationException("G-Force left fullscreen mode.");
                if (now - state.Started > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("G-Force fullscreen did not become ready within 15 seconds.");
                var normal = GForceWindow.Find(process.Id, GForceWindow.MainClass, includeHidden: true);
                if (normal == 0)
                    continue;
                var b = display.Bounds;
                GForceWindow.Place(normal, b.X + b.Width / 4, b.Y + b.Height / 4, b.Width / 2, b.Height / 2);
                GForceWindow.ShowWithoutActivation(normal);
                GForceWindow.Behind(normal, window.Handle);
                if (!state.Requested && now - state.Started >= TimeSpan.FromSeconds(4))
                {
                    state.Requested = true;
                    var delivered = GForceWindow.EnterFullscreen(normal);
                    Telemetry.Info("VisualizationFullscreenRequested", new
                    {
                        window.DisplayName,
                        ProcessId = process.Id,
                        NormalBounds = GForceWindow.Bounds(normal),
                        Delivered = delivered
                    });
                    if (!delivered)
                        throw new InvalidOperationException("G-Force fullscreen request failed.");
                }
            }
            catch (Exception ex)
            {
                state.Failures++;
                state.RetryAt = now.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(state.Failures, 5))));
                Telemetry.Error("VisualizationFailed", ex, new
                {
                    window.DisplayName,
                    ScreenName = definition.Name,
                    RetryAt = state.RetryAt
                });
                Stop(window.DisplayName, state, "Failure");
            }
        }
        foreach (var entry in _instances.Where(e => !desired.Contains(e.Key)).ToArray())
        {
            Stop(entry.Key, entry.Value, "ScreenOrDisplayUnavailable");
            if (entry.Value.Stopping is null or { IsCompleted: true })
                _instances.Remove(entry.Key);
        }
    }
    private static void Stop(string display, Instance instance, string reason)
    {
        if (instance.Owner is not { } owner)
            return;
        instance.Owner = null;
        instance.Fullscreen = 0;
        GForceWindow.Hide(owner.Process.Id);
        Telemetry.Info("VisualizationStopping", new
        {
            DisplayName = display,
            ProcessId = owner.Process.Id,
            Reason = reason
        });
        instance.Stopping = StopOwnedAsync(owner, display);
    }
    private static async Task StopOwnedAsync(VisualizationProcess owner, string display)
    {
        try
        {
            await owner.StopAsync();
        }
        catch (Exception ex) { owner.Dispose(); Telemetry.Error("VisualizationStopFailed", ex, new { DisplayName = display }); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _instances)
            Stop(entry.Key, entry.Value, "ApplicationShutdown");
        await Task.WhenAll(_instances.Values.Where(i => i.Stopping is not null).Select(i => i.Stopping!));
        _instances.Clear();
    }
}
