using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExperienceX;

// The job owns only this launched process and its descendants, never existing vendor processes.
// Closing Experience (including an unexpected exit) also closes its visualizers.
internal sealed class VisualizationProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    internal Process Process
    {
        get;
    }
    private VisualizationProcess(SafeFileHandle job, Process process)
    {
        _job = job;
        Process = process;
    }

    internal static VisualizationProcess Start(string path)
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Process? process = null;
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("G-Force did not start.");
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(job, process);
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            job.Dispose();
            throw;
        }
    }
    internal async Task StopAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                GForceWindow.Close(Process.Id);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await Process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) { }
            }
        }
        finally { Dispose(); }
    }
    public void Dispose()
    {
        _job.Dispose();
        Process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
