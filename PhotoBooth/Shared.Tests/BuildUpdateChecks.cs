using System.Diagnostics;
using PhotoBooth.Updates;

internal static class BuildUpdateChecks
{
    public static async Task Run(string application)
    {
        var project = application == "Controller" ? "Controller" : "experiencex";
        var package = Path.GetFullPath(Path.Combine(project, "bin", "Release", "net9.0-windows"));
        BuildUpdatePackage.Verify(package, application, File.ReadAllText(Path.Combine(package, BuildUpdatePackage.ManifestName)));
        var directory = Path.Combine(Path.GetTempPath(), "BuildUpdate-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "source & user's files");
        var runtime = Path.Combine(directory, "runtime");
        Directory.CreateDirectory(source); Directory.CreateDirectory(runtime);
        var restartName = application == "Controller" ? "Restart-Controller.ps1" : "Restart-Experience.ps1";
        var required = new[] { application + ".exe", application + ".dll", application + ".deps.json", application + ".runtimeconfig.json",
            "appsettings.defaults.json", restartName, "Stage-ApplicationUpdate.ps1", "runtimes/win-x64/native/dependency.dll" }
            .Concat(application == "Controller" ? ["appsettings.environment.json"] : []).ToArray();
        try
        {
            foreach (var name in required)
            {
                var path = Path.Combine(source, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "version-one");
            }
            foreach (var name in new[] { application + ".exe", application + ".dll" }) File.Copy(Path.Combine(source, name), Path.Combine(runtime, name));
            string Manifest() => string.Join('\n', required.Select(name => BuildUpdatePackage.Hash(Path.Combine(source, name)) + "|" + name));
            var manifestPath = Path.Combine(source, BuildUpdatePackage.ManifestName);
            await File.WriteAllTextAsync(manifestPath, Manifest());
            BuildUpdatePackage.Verify(source, application, await File.ReadAllTextAsync(manifestPath));
            BuildUpdatePackage.Verify(source + Path.DirectorySeparatorChar, application, await File.ReadAllTextAsync(manifestPath));
            var fingerprint = BuildUpdatePackage.BinaryFingerprint(source, application);
            File.SetLastWriteTimeUtc(Path.Combine(source, application + ".exe"), DateTime.UtcNow.AddMinutes(1));
            if (fingerprint != BuildUpdatePackage.BinaryFingerprint(source, application)) throw new Exception("Timestamp-only change looks like new code.");
            var stable = new UpdateStability(TimeSpan.FromSeconds(5));
            if (stable.IsReady("a", TimeSpan.Zero) || stable.IsReady("a", TimeSpan.FromSeconds(4)) || !stable.IsReady("a", TimeSpan.FromSeconds(5)) ||
                stable.IsReady("b", TimeSpan.FromSeconds(6)) || stable.IsReady("b", TimeSpan.FromSeconds(10))) throw new Exception("Build settling failed.");
            stable.Reset();
            if (stable.IsReady("b", TimeSpan.FromSeconds(12))) throw new Exception("Interrupted sync did not reset settling.");
            void Reject(string manifest)
            {
                try { BuildUpdatePackage.Verify(source, application, manifest); }
                catch (IOException) { return; }
                throw new Exception("Incomplete or invalid build was accepted.");
            }
            var valid = await File.ReadAllTextAsync(manifestPath);
            Reject(""); Reject(valid + "\n" + valid.Split('\n')[0]);
            Reject(new string('A', 64) + "|../outside.dll");
            Reject(new string('A', 64) + "|C:/outside.dll");
            Reject(valid.Replace(application + ".exe", "missing.exe"));

            // Run the real polling/launch path with a harmless script instead of stopping an app.
            await File.WriteAllTextAsync(Path.Combine(runtime, restartName),
                "param([string]$SourceDirectory, [switch]$AutomaticUpdate)\n" +
                "if (!$AutomaticUpdate) { exit 2 }\n" +
                "[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'restart-requested.txt'), $SourceDirectory + [Environment]::NewLine)\nexit 1\n");
            await using (var monitor = new BuildUpdateMonitor(application, source, runtime))
            {
                await File.WriteAllTextAsync(Path.Combine(source, "appsettings.TEST.json"), "user edit");
                await File.WriteAllTextAsync(Path.Combine(source, application + ".dll"), "version-two");
                var dependency = Path.Combine(source, required[^1]);
                if (application == "Controller") dependency = Path.Combine(source, "runtimes/win-x64/native/dependency.dll");
                await File.WriteAllTextAsync(dependency, "version-two");
                await File.WriteAllTextAsync(manifestPath, Manifest());
                await File.WriteAllTextAsync(dependency, "version-one"); // OneDrive has not delivered this file yet.
                var marker = Path.Combine(runtime, "restart-requested.txt");
                await Task.Delay(7500);
                if (File.Exists(marker)) throw new Exception("Restart was launched before dependencies finished syncing.");
                await File.WriteAllTextAsync(dependency, "version-two");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(100);
                if (!File.Exists(marker) || !(await File.ReadAllTextAsync(marker)).Contains(source)) throw new Exception("DLL-only update did not request a restart with the correct source.");
                await Task.Delay(2500);
                if (File.ReadAllLines(marker).Length != 1) throw new Exception("Failed restart caused a rapid retry loop.");
                var manualFailures = new List<string>();
                monitor.ManualRestartFailed += reason => { lock(manualFailures) manualFailures.Add(reason); };
                monitor.RequestRestart(); // Explicit restart must work without a binary change and during the automatic retry delay.
                deadline.Restart();
                while (File.ReadAllLines(marker).Length < 2 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
                if(File.ReadAllLines(marker).Length != 2) throw new Exception("Manual restart was skipped because the build was unchanged or retry was cooling down.");
                await Task.Delay(300);
                lock(manualFailures) if(manualFailures.Count!=1) throw new Exception("Manual restart failure did not notify the application.");
                await File.WriteAllTextAsync(dependency,"incomplete-sync");
                monitor.RequestRestart(); await Task.Delay(1500);
                lock(manualFailures) if(manualFailures.Count!=2) throw new Exception("Incomplete manual restart was not reported.");
                if(File.ReadAllLines(marker).Length!=2) throw new Exception("Manual restart launched an incomplete package.");
            }
            await File.WriteAllTextAsync(Path.Combine(source, "runtimes/win-x64/native/dependency.dll"), "version-two");
            await File.WriteAllTextAsync(manifestPath, Manifest());
            var pending = Path.Combine(runtime,"pending-restart.txt");
            await File.WriteAllTextAsync(Path.Combine(runtime,restartName),
                "param([string]$SourceDirectory, [switch]$AutomaticUpdate)\n" +
                "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'pending-restart.txt'),[string]$PID)\nStart-Sleep -Seconds 60\n");
            await using (var monitor = new BuildUpdateMonitor(application,source,runtime))
            {
                monitor.RequestRestart();
                var waiting=Stopwatch.StartNew();
                while(!File.Exists(pending)&&waiting.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(50);
                if(!File.Exists(pending))throw new Exception("Pending restart did not start.");
                using var launcher=Process.GetProcessById(int.Parse(File.ReadAllText(pending)));
                await monitor.StopAsync(cancelPendingRestart:true).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if(!launcher.HasExited)throw new Exception("Intentional shutdown left a pending restart launcher running.");
            }
            Console.WriteLine($"PASS: {application} complete package, partial sync, DLL-only update, settle delay, safe paths, quiet retry, restart cancellation and monitor disposal.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
