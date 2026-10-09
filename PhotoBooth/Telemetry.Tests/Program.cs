using System.Text.Json;
using PhotoBooth.Diagnostics;

var directory = Path.Combine(AppContext.BaseDirectory, "test-logs", Guid.NewGuid().ToString("N"));
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), JsonSerializer.Serialize(new
{
    Telemetry = new { Directory = directory, MinimumLevel = "Information", MaxFileBytes = 2048, RetainedFiles = 3, HealthIntervalSeconds = 60 }
}));
Telemetry.Start("Test");
for (var i = 0; i < 40; i++) Telemetry.Info("Rotation", new { Index = i, Text = new string('x', 300) });
Telemetry.Debug("FilteredOut");
Telemetry.Info("Correlated", new { Value = "ping.mp4" }, "test-request");
try { throw new InvalidOperationException("test failure"); }
catch (Exception ex) { Telemetry.Error("ExceptionCheck", ex); }
Telemetry.Stop();
var files = System.IO.Directory.GetFiles(Path.Combine(directory, "Test"), "*.jsonl");
if (files.Length != 3) throw new Exception($"Retention failed: {files.Length}");
var entries = files.SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
if (files.Any(file => !Path.GetFileName(file).StartsWith(Environment.MachineName + "-"))) throw new Exception("Hostname filename prefix missing.");
if (entries.Any(e => e.RootElement.GetProperty("MachineName").GetString() != Environment.MachineName)) throw new Exception("MachineName missing.");
if (entries.Any(e => e.RootElement.GetProperty("Event").GetString() == "FilteredOut")) throw new Exception("Filtering failed.");
if (!entries.Any(e => e.RootElement.GetProperty("RequestId").GetString() == "test-request")) throw new Exception("Correlation failed.");
if (!entries.Any(e => e.RootElement.GetProperty("Exception").GetString()?.Contains("InvalidOperationException") == true)) throw new Exception("Exception missing.");
if (!entries.Any(e => e.RootElement.GetProperty("Event").GetString() == "ApplicationStopped")) throw new Exception("Shutdown flush failed.");
foreach (var entry in entries) entry.Dispose();
Console.WriteLine("Passed JSON validity, rotation, retention, filtering, correlation, exception, and shutdown flush checks.");
