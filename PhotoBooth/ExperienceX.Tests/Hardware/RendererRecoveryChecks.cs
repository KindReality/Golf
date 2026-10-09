using ExperienceX;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

internal static class RendererRecoveryChecks
{
    public static async Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeRenderer();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
        Console.WriteLine("Passed GPU failure, disposal timeout and recreation checks.");
    }
    private static void NativeRenderer()
    {
        using var window = new Forms.Form { Text = "ExperienceX renderer verification", Width = 640, Height = 360, ShowInTaskbar = false };
        var hwnd = window.Handle;
        var options = new ExperienceOptions { ConfigurationMode = true, DisplayKeystones = new() { ["TEST"] = new() } };
        int fail = 0;
        using var hidden = new ManualResetEventSlim();
        using (var renderer = new VideoRenderer(hwnd, 640, 360, options, "TEST", () => hidden.Set(), () =>
        {
            if (Volatile.Read(ref fail) != 0)
                throw new COMException("Injected graphics device removal", unchecked((int)0x887A0005));
        }))
        {
            Wait(() => renderer.State == RendererState.Idle);
            if (!renderer.ConfirmKeystoneAsync(options.KeystoneFor("TEST")).GetAwaiter().GetResult())
                throw new Exception("Renderer did not acknowledge applied geometry.");
            var count = renderer.TransformComputationCount;
            for (int i = 0; i < 20; i++)
                if (!renderer.ConfirmKeystoneAsync(options.KeystoneFor("TEST")).GetAwaiter().GetResult())
                    throw new Exception("Repeated renderer confirmation failed.");
            if (renderer.TransformComputationCount != count)
                throw new Exception("Unchanged keystone was recomputed per frame.");
            Volatile.Write(ref fail, 1);
            renderer.Configure(options with
            {
                CalibrationGridEnabled = false
            });
            Wait(() => renderer.State == RendererState.Failed);
            if (!hidden.Wait(1000))
                throw new Exception("Device failure did not hide the surface.");
            renderer.Dispose();
            if (!renderer.Stopped)
                throw new Exception("Failed graphics worker did not stop.");
            if (renderer.ConfirmKeystoneAsync(options.KeystoneFor("TEST")).GetAwaiter().GetResult())
                throw new Exception("Failed renderer acknowledged a save.");
        }
        using var restored = new VideoRenderer(hwnd, 640, 360, options, "TEST", () => { });
        Wait(() => restored.State == RendererState.Idle);
        if (!restored.ConfirmKeystoneAsync(options.KeystoneFor("TEST")).GetAwaiter().GetResult())
            throw new Exception("Recreated renderer did not present the calibration.");
        restored.Dispose();
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int block = 0;
        using var hung = new VideoRenderer(hwnd, 640, 360, options, "TEST", () => { }, () => { if (Volatile.Read(ref block) != 0) { blocked.Set(); release.Wait(); } });
        Wait(() => hung.State == RendererState.Idle);
        Volatile.Write(ref block, 1);
        hung.Configure(options);
        if (!blocked.Wait(1000))
            throw new Exception("The shutdown timeout test did not reach its blocking point.");
        var shutdown = Stopwatch.StartNew();
        try
        {
            hung.Dispose();
            if (shutdown.Elapsed.TotalSeconds > 6 || hung.Stopped)
                throw new Exception("Blocked renderer shutdown was not bounded correctly.");
        }
        finally { release.Set(); }
        Wait(() => hung.Stopped);
    }
    private static void Wait(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed.TotalSeconds > 8)
                throw new TimeoutException("Native renderer did not reach the expected state.");
            Thread.Sleep(10);
        }
    }
}
