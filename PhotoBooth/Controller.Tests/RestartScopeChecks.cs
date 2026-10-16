using PhotoBooth.Updates;
using System.Text.Json;

internal static class RestartScopeChecks
{
    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Controller-restart-scope-"+Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source & user's build");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(source); Directory.CreateDirectory(runtime);
        try
        {
            const string script = """
                param([string]$SourceDirectory, [switch]$AutomaticUpdate, [switch]$RestartExperience)
                [IO.File]::WriteAllText((Join-Path $SourceDirectory 'observed.json'), (@{Automatic=[bool]$AutomaticUpdate;Both=[bool]$RestartExperience} | ConvertTo-Json))
                exit 1
                """;
            foreach (var name in new[] { "Controller.exe", "Controller.dll", "Controller.deps.json", "Controller.runtimeconfig.json", "appsettings.defaults.json", "appsettings.environment.json", "Stage-ApplicationUpdate.ps1", "Restart-Controller.ps1" })
            {
                File.WriteAllText(Path.Combine(source, name), name == "Restart-Controller.ps1" ? script : "fixture");
                File.Copy(Path.Combine(source, name), Path.Combine(runtime, name));
            }
            File.WriteAllText(Path.Combine(source, BuildUpdatePackage.ManifestName), string.Join('\n', Directory.GetFiles(source).Select(file => BuildUpdatePackage.Hash(file)+"|"+Path.GetFileName(file))));
            foreach (var both in new[] { false, true })
            {
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var monitor = new BuildUpdateMonitor("Controller", source, runtime);
                // The fixture script deliberately returns without closing its host.
                monitor.ManualRestartFailed += _ => finished.TrySetResult();
                monitor.RequestRestart(restartExperience: both);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(12));
                using var observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "observed.json")));
                if (!observed.RootElement.GetProperty("Automatic").GetBoolean() || observed.RootElement.GetProperty("Both").GetBoolean() != both)
                    throw new Exception("Restart scope did not reach the external restart process.");
            }
            Console.WriteLine("PASS: Controller joint restart reaches the script with package verification; ordinary restarts retain their own scope.");
        }
        finally { Directory.Delete(root, true); }
    }
}
