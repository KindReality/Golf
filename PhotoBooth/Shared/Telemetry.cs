using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace PhotoBooth.Diagnostics;

public sealed record TelemetryOptions
{
    public string MinimumLevel { get; init; } = "Information";
    public string Directory { get; init; } = "logs";
    public long MaxFileBytes { get; init; } = 10485760;
    public int RetainedFiles { get; init; } = 20;
    public int HealthIntervalSeconds { get; init; } = 60;
}

public static class Telemetry
{
    private sealed record Entry(DateTimeOffset Timestamp, string Level, string MachineName, string Application, string SessionId,
        int ProcessId, string Event, string? RequestId, object? Data, string? Exception);
    private static readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly string HostName = Environment.MachineName;
    private static readonly string HostPrefix = string.Concat(HostName.Select(c =>
        Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string _app = "Application", _directory = "";
    private static TelemetryOptions _options = new();
    private static Task? _writer;
    private static System.Threading.Timer? _health;
    private static Func<object>? _state;
    private static int _minimum = 1, _stopped;
    private static long _dropped;

    public static void Start(string app,string? configurationPath=null)
    {
        _app = app;
        Exception? error = null;
        var path = configurationPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Telemetry", out var value))
                    _options = value.Deserialize<TelemetryOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            if (!new[] { "Debug", "Information", "Warning", "Error" }.Contains(_options.MinimumLevel, StringComparer.OrdinalIgnoreCase) ||
                _options.MaxFileBytes < 1024 || _options.RetainedFiles is < 1 or > 1000 ||
                _options.HealthIntervalSeconds is < 1 or > 86400 || string.IsNullOrWhiteSpace(_options.Directory))
                throw new ArgumentException("Invalid Telemetry configuration.");
            _directory = Path.Combine(Path.GetFullPath(_options.Directory, AppContext.BaseDirectory), app);
        }
        catch (Exception ex) { error = ex; _options = new(); _directory = Path.Combine(AppContext.BaseDirectory, "logs", app); }
        _minimum = Rank(_options.MinimumLevel);
        _writer = Task.Run(WriteLoopAsync);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Error("UnhandledException", e.ExceptionObject as Exception, new { e.IsTerminating }); Stop(); };
        TaskScheduler.UnobservedTaskException += (_, e) => Error("UnobservedTaskException", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        Info("ApplicationStarted", new { ConfigurationPath = path, LogDirectory = _directory,
            Version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });
        if (error is not null) Error("TelemetryConfigurationInvalid", error);
        _health = new System.Threading.Timer(_ =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                Info("Health", new { WorkingSetBytes = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false),
                    State = _state?.Invoke(), DroppedEntries = Interlocked.Read(ref _dropped) });
            }
            catch (Exception ex) { Error("HealthCollectionFailed", ex); }
        }, null, TimeSpan.FromSeconds(_options.HealthIntervalSeconds), TimeSpan.FromSeconds(_options.HealthIntervalSeconds));
    }

    public static void SetHealthProvider(Func<object> provider) => _state = provider;
    public static void Debug(string name, object? data = null, string? requestId = null) => Emit("Debug", name, data, null, requestId);
    public static void Info(string name, object? data = null, string? requestId = null) => Emit("Information", name, data, null, requestId);
    public static void Warning(string name, object? data = null, string? requestId = null) => Emit("Warning", name, data, null, requestId);
    public static void Error(string name, Exception? exception = null, object? data = null, string? requestId = null) => Emit("Error", name, data, exception, requestId);
    public static void Legacy(object? value)
    {
        if (value is Exception ex) { Error("CameraError", ex); return; }
        var message = value?.ToString() ?? "";
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            Warning("CameraDiagnostic", new { Message = message });
        else if (message.StartsWith("[Transition]") || message.StartsWith("[Animation]") || message.StartsWith("[Timers]") || message.StartsWith("[State:Countdown]"))
            Debug("CameraDiagnostic", new { Message = message });
        else Info("CameraDiagnostic", new { Message = message });
    }
    private static int Rank(string level) => level.ToUpperInvariant() switch { "DEBUG" => 0, "INFORMATION" => 1, "WARNING" => 2, _ => 3 };
    private static void Emit(string level, string name, object? data, Exception? exception, string? requestId)
    {
        if (_writer is null || Volatile.Read(ref _stopped) != 0 || Rank(level) < _minimum) return;
        if (!Queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, level, HostName, _app, Session, Environment.ProcessId,
            name, requestId, data, exception?.ToString()))) Interlocked.Increment(ref _dropped);
    }
    private static async Task WriteLoopAsync()
    {
        StreamWriter? output = null;
        DateOnly day = default;
        long bytes = 0;
        var sequence = 0;
        try
        {
            while (await Queue.Reader.WaitToReadAsync())
            {
                while (Queue.Reader.TryRead(out var entry))
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(entry);
                        var size = System.Text.Encoding.UTF8.GetByteCount(json) + 1;
                        var entryDay = DateOnly.FromDateTime(entry.Timestamp.UtcDateTime);
                        if (output is null || day != entryDay || bytes + size > _options.MaxFileBytes)
                        {
                            if (output is not null) await output.DisposeAsync();
                            output = null;
                            System.IO.Directory.CreateDirectory(_directory);
                            var path = Path.Combine(_directory, $"{HostPrefix}-{entryDay:yyyy-MM-dd}-{Environment.ProcessId}-{Session[..8]}-{sequence++:D4}.jsonl");
                            output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false));
                            output.NewLine = "\n";
                            day = entryDay; bytes = 0;
                            foreach (var file in new DirectoryInfo(_directory).GetFiles(HostPrefix + "-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Skip(_options.RetainedFiles))
                                if (file.FullName != path) try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                        await output.WriteLineAsync(json);
                        bytes += size;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _dropped);
                        Trace.WriteLine($"Telemetry write failed: {ex}");
                        if (output is not null) { try { await output.DisposeAsync(); } catch { } output = null; }
                    }
                }
                if (output is not null)
                {
                    try { await output.FlushAsync(); }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Telemetry flush failed: {ex}");
                        try { await output.DisposeAsync(); } catch { }
                        output = null;
                        Interlocked.Increment(ref _dropped);
                    }
                }
            }
        }
        catch (Exception ex) { Trace.WriteLine($"Telemetry writer failed: {ex}"); }
        finally { if (output is not null) { try { await output.DisposeAsync(); } catch { } } }
    }
    public static void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _health?.Dispose(); _state = null;
        Queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, "Information", HostName, _app, Session, Environment.ProcessId,
            "ApplicationStopped", null, new { DroppedEntries = Interlocked.Read(ref _dropped) }, null));
        Queue.Writer.TryComplete();
        try { _writer?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
