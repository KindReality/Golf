using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// A separate copy of this EXE captures native thread stacks; it never initializes the application.
internal static class RendererDiagnosticCapture
{
    internal const string HelperArgument = "--capture-renderer-dump";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _captures;
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "logs", "ExperienceX", "diagnostics");
    private static string HostPrefix => string.Concat(Environment.MachineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))+"-render-";

    internal static Task CaptureAsync(RenderHealth health, string reason) => CaptureCoreAsync(health, reason);
    private static async Task CaptureCoreAsync(RenderHealth health, string reason)
    {
        // Two captures per application session, three retained capture pairs per computer.
        if (Interlocked.Increment(ref _captures) > 2)
        {
            Telemetry.Warning("RendererDiagnosticCaptureSkipped", new { health.DisplayName, Reason = reason, LimitPerSession = 2 });
            return;
        }
        await Gate.WaitAsync().ConfigureAwait(false);
        string? dump = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var stem = HostPrefix+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)+"-"+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N");
            dump = Path.Combine(DirectoryPath, stem+".dmp");
            var context = Path.Combine(DirectoryPath, stem+".json");
            using var current = Process.GetCurrentProcess();
            await File.WriteAllTextAsync(context, JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.UtcNow, Host = Environment.MachineName, ProcessId = Environment.ProcessId,
                Reason = reason, Health = health, Dump = dump, DumpType = "Native thread stacks, thread information and modules; no full memory heap"
            }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            foreach (var old in new DirectoryInfo(DirectoryPath).GetFiles(HostPrefix+"*.json").OrderByDescending(f => f.Name).Skip(3))
            {
                File.Delete(Path.ChangeExtension(old.FullName, ".dmp"));
                File.Delete(Path.ChangeExtension(old.FullName, ".capture-error.txt"));
                old.Delete();
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "ExperienceX.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in new[] { HelperArgument, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), Path.GetFileName(dump) }) start.ArgumentList.Add(argument);
            Telemetry.Info("RendererDiagnosticCaptureStarted", new { health.DisplayName, Reason = reason, ContextPath = context, DumpPath = dump });
            using var helper = Process.Start(start) ?? throw new IOException("Could not start the renderer diagnostic helper.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync().ConfigureAwait(false); }
                throw new TimeoutException("Renderer diagnostic capture exceeded twenty seconds.");
            }
            if (helper.ExitCode != 0) throw new IOException($"Renderer diagnostic helper exited with code {helper.ExitCode}. See {context} and its capture-error file.");
            Telemetry.Info("RendererDiagnosticCaptured", new { health.DisplayName, Reason = reason, ContextPath = context, DumpPath = dump, Bytes = new FileInfo(dump).Length });
        }
        catch (Exception ex)
        {
            if (dump is not null) try { File.Delete(dump); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Telemetry.Error("RendererDiagnosticCaptureFailed", ex, new { health.DisplayName, Reason = reason, DumpPath = dump });
        }
        finally { Gate.Release(); }
    }

    internal static int RunHelper(string[] arguments)
    {
        string? path = null;
        try
        {
            if (arguments.Length != 4 || arguments[0] != HelperArgument || !int.TryParse(arguments[1], out var pid) ||
                !long.TryParse(arguments[2], out var ticks) || arguments[3] != Path.GetFileName(arguments[3]) ||
                arguments[3].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !arguments[3].StartsWith(HostPrefix, StringComparison.Ordinal) || !arguments[3].EndsWith(".dmp", StringComparison.Ordinal)) return 2;
            using var target = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            // Refuse PID reuse, another session, another application or self-capture.
            if (pid == Environment.ProcessId || target.SessionId != current.SessionId || target.StartTime.ToUniversalTime().Ticks != ticks ||
                !string.Equals(target.MainModule?.FileName, current.MainModule?.FileName, StringComparison.OrdinalIgnoreCase)) return 3;
            Directory.CreateDirectory(DirectoryPath);
            path = Path.Combine(DirectoryPath, arguments[3]);
            using var process = OpenProcess(0x0410, false, pid); // QUERY_INFORMATION | VM_READ
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // MINIDUMP_NORMAL | MINIDUMP_WITH_UNLOADED_MODULES | MINIDUMP_WITH_THREAD_INFO.
            if (!MiniDumpWriteDump(process, (uint)pid, output.SafeFileHandle, 0x1020, 0, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return 0;
        }
        catch (Exception ex)
        {
            if (path is not null) try { File.WriteAllText(Path.ChangeExtension(path, ".capture-error.txt"), ex.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return 1;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("dbghelp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(SafeProcessHandle process, uint pid, SafeFileHandle file, uint type, nint exception, nint userStream, nint callback);
}
