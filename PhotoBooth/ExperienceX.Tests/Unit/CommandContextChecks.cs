using System.Text.Json;
using ExperienceX;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

internal static class CommandContextChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExperienceX-command-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var context = new CommandContext { Scene = "idle", Playlist = "media-loop", Item = "Eye", IsAmbient = true };
            var chain = BroadcastChain.Root("controller");
            var bytes = BroadcastEnvelope.Create(new() { DeviceName = "controller" }, "command", "*",
                new { experience = "video", value = "Eye.mp4", screen = "*", mute = true }, "context-check", chain, context);
            var message = BroadcastEnvelope.Parse(bytes);
            var request = ExperienceRequest.Parse(message.Body) with { Source = message.Source, TargetDevice = message.Device };
            Check(request.Context == context && request.CorrelationId == chain.CorrelationId && request.Value == "Eye.mp4", "Execution context did not survive command serialization/parsing.");
            var uncontextual = ExperienceRequest.Parse(BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new(), "command", "*",
                new { experience = "video", value = "Eye.mp4" })).Body);
            Check(uncontextual.Context is null, "Commands from other producers require Controller metadata.");
            try { BroadcastEnvelope.Create(new(), "command", "*", new { experience = "video", value = "Eye.mp4" }, context: context with { Item = new string('x', 129) }); throw new Exception("Invalid context was accepted."); }
            catch (ArgumentException) { }
            var telemetryConfig = Path.Combine(root, "appsettings.json");
            File.WriteAllText(telemetryConfig, JsonSerializer.Serialize(new { Telemetry = new { Directory = Path.Combine(root, "logs") } }));
            Telemetry.Start("ContextChecks", telemetryConfig);
            Telemetry.SetRequestContext(request.RequestId, request.LogContext());
            Telemetry.Info("UdpRequestReceived", new { }, request.RequestId);
            var localPath = Path.Combine(root, "Media", "Eye.mp4");
            Telemetry.SetRequestContext(request.RequestId, request.LogContext(localPath));
            Telemetry.Info("VideoPlaybackConfirmed", new { DisplayName = "display1", ScreenName = "screen1" }, request.RequestId);
            Telemetry.Error("VideoPlaybackFailed", new IOException("test failure"), requestId: request.RequestId);
            for (int index = 0; index < 4200; index++) Telemetry.SetRequestContext("request-" + index, new() { Value = "clip" + index });
            Telemetry.Info("EvictedContext", requestId: request.RequestId);
            Telemetry.Info("RecentContext", requestId: "request-4199");
            Telemetry.Stop();
            var logs = Directory.EnumerateFiles(Path.Combine(root, "logs", "ContextChecks"), "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                foreach (var entry in logs.Where(doc => doc.RootElement.GetProperty("Event").GetString() is "UdpRequestReceived" or "VideoPlaybackConfirmed" or "VideoPlaybackFailed"))
                {
                    var details = entry.RootElement.GetProperty("Context");
                    Check(details.GetProperty("Value").GetString() == "Eye.mp4" && details.GetProperty("Scene").GetString() == "idle" &&
                        details.GetProperty("Playlist").GetString() == "media-loop" && details.GetProperty("Item").GetString() == "Eye" && details.GetProperty("IsAmbient").GetBoolean(), "A lifecycle log lost media/scene/playlist/item/ambient context.");
                    if (entry.RootElement.GetProperty("Event").GetString() != "UdpRequestReceived")
                        Check(details.GetProperty("Path").GetString() == localPath, "Playback/failure log lost its local file path.");
                }
                Check(logs.Count(doc => doc.RootElement.GetProperty("Event").GetString() is "UdpRequestReceived" or "VideoPlaybackConfirmed" or "VideoPlaybackFailed") == 3, "Lifecycle logs were not flushed.");
                Check(!logs.Single(doc => doc.RootElement.GetProperty("Event").GetString() == "EvictedContext").RootElement.TryGetProperty("Context", out _) &&
                    logs.Single(doc => doc.RootElement.GetProperty("Event").GetString() == "RecentContext").RootElement.GetProperty("Context").GetProperty("Value").GetString() == "clip4199", "Telemetry request context was not bounded to recent requests.");
            }
            finally { foreach (var doc in logs) doc.Dispose(); }
            await Task.CompletedTask;
            Console.WriteLine("Passed optional command context, serialization, validation, media/scene/playlist/item lifecycle logs, local paths and bounded context retention.");
        }
        finally { Telemetry.Stop(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
