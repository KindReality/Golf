using ExperienceX;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class WindowRecoveryChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var options = new ExperienceOptions { DeveloperMode = true };
            var monitor = Forms.Screen.PrimaryScreen?.DeviceName ?? throw new Exception("A display is required for the recovery integration test.");
            int inject = 0, phase = 0;
            using var window = new MainWindow(monitor, options, () =>
            {
                if (Interlocked.Exchange(ref inject, 0) == 1)
                    throw new COMException("Injected one-shot device failure", unchecked((int)0x887A0005));
            });
            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void End(Exception? error)
            {
                timer.Stop();
                window.ShutdownWindow();
                if (error is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(error);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (clock.Elapsed.TotalSeconds > 15)
                        throw new TimeoutException("Automatic window recovery did not finish.");
                    if (phase == 0 && window.State == RendererState.Idle)
                    {
                        phase = 1;
                        Volatile.Write(ref inject, 1);
                        window.UpdateOptions(options);
                    }
                    else if (phase == 1 && window.State == RendererState.Failed)
                    {
                        window.RefreshMonitor();
                        if (window.Visible)
                            throw new Exception("Failed monitor surface remained visible.");
                        phase = 2;
                    }
                    else if (phase == 2)
                    {
                        window.RefreshMonitor();
                        if (window.State == RendererState.Idle && window.Visible)
                            End(null);
                    }
                }
                catch (Exception ex) { End(ex); }
            };
            try
            {
                window.Start();
                timer.Start();
                Dispatcher.Run();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Passed automatic per-monitor hiding, retry, DirectX recreation, and transparent window restoration after an injected device failure.");
    }
}
