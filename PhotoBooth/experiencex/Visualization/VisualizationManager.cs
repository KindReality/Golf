using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.ComponentModel;
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
        public CancellationTokenSource? Lifetime;
        public GForceControlClient? Client;
        public VisualizationControls? Controls;
        public VisualizationOptions? Applied;
        public VisualizationOptions? Rejected;
        public Task? Configuring;
        public Exception? ControlFailure;
        public bool Polling;
        public string? ActiveRequestId;
        public readonly SemaphoreSlim Operations = new(1);
    }
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncTaskScope _tasks = new("Visualizations");
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
                    state.Lifetime = new();
                    state.ControlFailure = null;
                    state.Applied = null;
                    state.Rejected = null;
                    Configure(window.DisplayName, state, options.Visualization);
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
                if (state.ControlFailure is { } failure) throw new InvalidOperationException("G-Force control failed.", failure);
                if (state.Configuring is null or { IsCompleted: true } && state.Applied != options.Visualization && state.Rejected != options.Visualization)
                    Configure(window.DisplayName, state, options.Visualization);
                if (state.Configuring is { IsCompleted: false }) continue;
                if (!state.Polling)
                {
                    state.Polling = true;
                    _tasks.Run(_ => PollControlAsync(window.DisplayName, state), "Enforce visualization automatic changes off");
                }
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
                if (now - state.Started > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("G-Force controls/fullscreen did not become ready within 30 seconds.");
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
                    _tasks.Run(_ => RequestFullscreenAsync(window.DisplayName, state), "Request owned G-Force fullscreen");
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
        instance.Lifetime?.Cancel();
        instance.Client?.Dispose();
        instance.Client = null;
        instance.Controls = null;
        instance.Applied = null;
        GForceWindow.Hide(owner.Process.Id);
        Telemetry.Info("VisualizationStopping", new
        {
            DisplayName = display,
            ProcessId = owner.Process.Id,
            Reason = reason
        });
        instance.Stopping = StopOwnedAsync(owner, display, instance.Configuring, instance.Lifetime);
    }
    private static async Task StopOwnedAsync(VisualizationProcess owner, string display, Task? configuring, CancellationTokenSource? lifetime)
    {
        try
        {
            if (configuring is not null) await configuring;
            await owner.StopAsync();
        }
        catch (Exception ex) { owner.Dispose(); Telemetry.Error("VisualizationStopFailed", ex, new { DisplayName = display }); }
        finally { lifetime?.Dispose(); }
    }
    private void Configure(string display, Instance instance, VisualizationOptions options)
    {
        var task = ApplyControlAsync(display, instance, options, instance.Lifetime!.Token);
        instance.Configuring = task;
        _tasks.Run(_ => task, "Apply visualization configuration");
    }
    private static async Task ApplyControlAsync(string display, Instance instance, VisualizationOptions options, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await instance.Operations.WaitAsync(token); acquired = true;
            var processId = instance.Owner!.Process.Id;
            if (instance.Client is null)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try { instance.Client = await GForceControlClient.ConnectAsync(processId, token); break; }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or SocketException && DateTimeOffset.UtcNow < deadline)
                    { await Task.Delay(100, token); }
                }
                instance.Client.ConsoleMessage += message => Telemetry.Info("VisualizationConsoleMessage", new { DisplayName = display, ProcessId = processId, Message = message }, instance.ActiveRequestId);
                instance.Controls = new(instance.Client);
                Telemetry.Info("VisualizationControlConnected", new { DisplayName = display, ProcessId = processId });
            }
            await instance.Controls!.ApplyAsync(options, token);
            token.ThrowIfCancellationRequested();
            instance.Applied = options;
            instance.Rejected = null;
            Telemetry.Info("VisualizationDefaultsApplied", new { DisplayName = display, ProcessId = processId, Defaults = options, Selections = instance.Controls.Selections, AutomaticChanges = false });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException ex) when (instance.Applied is not null)
        {
            instance.Rejected = options;
            Telemetry.Warning("VisualizationDefaultsRejected", new { DisplayName = display, Reason = ex.Message, RetainedDefaults = instance.Applied });
        }
        catch (Exception ex) { instance.ControlFailure = ex; Telemetry.Error("VisualizationDefaultsFailed", ex, new { DisplayName = display }); }
        finally { if (acquired) instance.Operations.Release(); }
    }
    private static async Task PollControlAsync(string display, Instance instance)
    {
        var token = instance.Lifetime!.Token;
        bool acquired = false;
        try
        {
            await instance.Operations.WaitAsync(token); acquired = true;
            if (instance.Client is not null && await instance.Client.EnsureAutomaticChangesOffAsync(token))
                Telemetry.Info("VisualizationAutomaticChangesDisabled", new { DisplayName = display });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { instance.ControlFailure = ex; }
        finally { if (acquired) instance.Operations.Release(); instance.Polling = false; }
    }
    private static async Task RequestFullscreenAsync(string display, Instance instance)
    {
        var token = instance.Lifetime!.Token;
        bool acquired = false;
        try
        {
            await instance.Operations.WaitAsync(token); acquired = true;
            if (instance.Client is null || !await instance.Client.EnterFullscreenAsync(token))
                throw new InvalidOperationException("G-Force did not confirm fullscreen mode.");
            Telemetry.Info("VisualizationFullscreenRequested", new { DisplayName = display, ProcessId = instance.Owner!.Process.Id, Transport = "LocalTCP", Delivered = true });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { instance.ControlFailure = ex; }
        finally { if (acquired) instance.Operations.Release(); }
    }
    internal async Task ExecuteAsync(ExperienceRequest request, ExperienceOptions options, CancellationToken cancellation)
    {
        var outputs = ScreenSelection.VisualizationOutputs(request, options, Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName);
        if (outputs.Count == 0) throw new InvalidOperationException("No visualization screen matches the requested display/screen.");
        foreach (var output in outputs)
        {
            if (!_instances.TryGetValue(output.Display, out var instance) || instance.Owner is null || instance.Lifetime is null)
                throw new InvalidOperationException("The requested visualization is unavailable or recovering.");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, instance.Lifetime.Token);
            var token = lifetime.Token;
            if (instance.Configuring is { } configuring) await configuring.WaitAsync(token);
            await instance.Operations.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (request.Expired?.Invoke() == true) throw new InvalidOperationException("Visualization command expired while waiting for readiness.");
                if (instance.Controls is null || instance.ControlFailure is not null) throw new InvalidOperationException("Visualization controls are not ready.");
                instance.ActiveRequestId = request.RequestId;
                await instance.Controls.ExecuteAsync(request, instance.Applied ?? options.Visualization, token);
                Telemetry.Info("VisualizationCommandApplied", new { DisplayName = output.Display, ScreenName = output.Screen.Name, request.Command, request.VisualType, request.Value, request.TransitionSeconds, request.DelaySeconds, request.DurationSeconds, Selections = instance.Controls.Selections }, request.RequestId);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException)
            { instance.ControlFailure = ex; throw; }
            finally { instance.ActiveRequestId = null; instance.Operations.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _instances)
            Stop(entry.Key, entry.Value, "ApplicationShutdown");
        await Task.WhenAll(_instances.Values.Where(i => i.Stopping is not null).Select(i => i.Stopping!));
        await _tasks.DisposeAsync();
        _instances.Clear();
    }
}
