using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class DashboardClearChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { if (clock.ElapsedMilliseconds > 2000) throw new TimeoutException("Dashboard clear condition not reached."); await Task.Delay(5); }
    }
    private static PlaylistCommand Cmd(string value) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { command = "test", value }) };
    internal static async Task Run()
    {
        var log = new ActivityLog();
        log.Info("Before", "old activity"); log.Warning("BeforeWarning", "old warning"); log.Clear();
        Check(log.Snapshot().Length == 0, "Clear removes the dashboard activity history");
        log.Info("After", "new activity");
        Check(log.Snapshot().Single().Message == "new activity", "New activity continues appearing after clearing");

        var options = new ControllerOptions
        {
            DeviceName = "dashboard-clear-test",
            Scenes = [new() { Name = "idle", AmbientPlaylist = "empty" }, new() { Name = "active", ActivatingPlaylist = "entry", AmbientPlaylist = "ambient" }],
            Playlists = [
                new() { Name = "empty", Type = "ambient" },
                new() { Name = "entry", Items = [new() { Name = "first", Commands = [Cmd("entry")] }, new() { Name = "late", DelayMilliseconds = 120, Commands = [Cmd("late-entry")] }] },
                new() { Name = "ambient", Type = "ambient", Items = [new() { Name = "tick", MinIntervalMilliseconds = 20, MaxIntervalMilliseconds = 20, Commands = [Cmd("ambient")] }] },
                new() { Name = "manual", Items = [new() { Name = "wait", DelayMilliseconds = 120, Commands = [Cmd("late-manual")] }] }
            ]
        };
        var recorder = new Recorder();
        await using var engine = new PlaylistEngine(options, recorder, log);
        await using var scenes = new SceneManager(options, engine, log);
        scenes.Start(); await Until(() => scenes.Snapshot().Phase == "Active");
        scenes.Request("active"); await Until(() => recorder.Values.Contains("entry"));
        var manual = engine.Run("manual");
        scenes.StopImmediately();
        Check(engine.Snapshot().Length == 0 && scenes.Snapshot().Phase == "Stopped" && scenes.Snapshot().Destination is null,
            "Clear immediately empties running status and abandons pending scene activation");
        await manual.Completion;
        await Task.Delay(160);
        Check(recorder.Values.SequenceEqual(["entry"]), "Clear suppresses remaining cues, automatic Idle and ambient startup");
        scenes.Apply(options); await Task.Delay(35);
        Check(scenes.Snapshot().Phase == "Stopped" && recorder.Values.Count == 1, "Configuration reload does not silently resume a cleared scene");

        scenes.Request("active");
        await Until(() => recorder.Values.Contains("ambient"));
        scenes.StopImmediately();
        var count = recorder.Values.Count;
        await Task.Delay(70);
        Check(recorder.Values.Count == count, "Clearing an active scene stops ambient scheduling");
        scenes.Request("idle"); await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" });
        var fresh = engine.Run("manual"); await fresh.Completion;
        Check(recorder.Values.Contains("late-manual"), "Clear does not permanently disable future manual or triggered playlists");
        CheckLayout();
    }
    private static void CheckLayout()
    {
        using var parent = new Panel { Size = new(400, 260) };
        using var clear = new Button { Text = "Clear", Size = new(76, 30) };
        DarkTheme.Button(clear);
        using var list = new ListView { Dock = DockStyle.Fill };
        using var card = ControlWindow.ListCard("RUNNING PLAYLISTS", "Live execution across scenes and triggers", list, Padding.Empty, clear);
        parent.Controls.Add(card); parent.CreateControl(); card.PerformLayout();
        var layout = (TableLayoutPanel)card.Controls[0]; layout.PerformLayout();
        Check(layout.GetRow(clear) == 0 && layout.GetColumn(clear) == 1 && clear.Right <= layout.ClientSize.Width && list.Top >= clear.Bottom,
            "Clear sits beside the section heading above the list without overlapping content");
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal readonly ConcurrentQueue<string> Values = new();
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Values.Enqueue(ControllerOptions.Text(command.Payload, "value") ?? ControllerOptions.Text(command.Payload, "command") ?? "unknown");
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
}
