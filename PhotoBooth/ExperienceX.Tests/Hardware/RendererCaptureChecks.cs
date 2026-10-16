using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class RendererCaptureChecks
{
    internal static async Task Run()
    {
        if (Process.GetProcessesByName("ExperienceX").Length > 0 || Process.GetProcessesByName("Controller").Length > 0)
            throw new Exception("Stop local applications before the isolated capture check.");
        var root = Path.Combine(Path.GetTempPath(), "ExperienceX-capture-"+Guid.NewGuid().ToString("N"));
        var drive = Path.Combine(root, "OneDrive");
        var source = Path.Combine(drive, "Home Technologies", "Bin", "ExperienceX");
        var media = Path.Combine(root, "Media");
        Directory.CreateDirectory(source); Directory.CreateDirectory(media);
        Process? application = null;
        try
        {
            var package = Path.GetFullPath("experiencex/bin/Release/net9.0-windows");
            foreach (var line in File.ReadAllLines(Path.Combine(package, "build-manifest.sha256")))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var relative = line.Split('|', 2)[1]; var target = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(package, relative), target);
            }
            File.Copy(Path.Combine(package, "build-manifest.sha256"), Path.Combine(source, "build-manifest.sha256"));
            int Port() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
            var options = new ExperienceOptions
            {
                DeveloperMode = true, MediaDiscovery = new() { Enabled = false }, VideoDirectory = media, AudioDirectory = media,
                UdpCommandsPort = Port(), UdpEventsPort = Port(), InventoryPort = Port(),
                DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                DisplayScreens = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new List<ScreenOptions>())
            };
            File.WriteAllText(Path.Combine(source, $"appsettings.{Environment.MachineName}.json"), JsonSerializer.Serialize(options));
            var executable = Path.Combine(source, "ExperienceX.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source };
            start.Environment["OneDrive"] = drive;
            application = Process.Start(start) ?? throw new Exception("Could not start the isolated Experience process.");
            var deadline = Stopwatch.StartNew();
            while (!Ready()) { if (application.HasExited || deadline.Elapsed.TotalSeconds > 15) throw new Exception("Isolated Experience did not start."); await Task.Delay(50); }
            var prefix = string.Concat(Environment.MachineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))+"-render-";
            var name = prefix+"capture-test-"+Guid.NewGuid().ToString("N")+".dmp";
            async Task<int> Helper(long ticks, string filename)
            {
                var command = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source };
                foreach (var arg in new[] { RendererDiagnosticCapture.HelperArgument, application.Id.ToString(), ticks.ToString(), filename }) command.ArgumentList.Add(arg);
                using var helper = Process.Start(command)!;
                try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch { if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); } throw; }
                return helper.ExitCode;
            }
            var stamp = application.StartTime.ToUniversalTime().Ticks;
            if (await Helper(stamp+1, name) != 3 || await Helper(stamp, "../outside.dmp") != 2)
                throw new Exception("Helper accepted PID reuse or a path outside its capture directory.");
            if (await Helper(stamp, name) != 0) throw new Exception("Native capture failed; evidence retained at "+root);
            var dump = Path.Combine(source, "logs", "ExperienceX", "diagnostics", name);
            using (var file = File.OpenRead(dump))
            using (var reader = new BinaryReader(file))
            {
                if (reader.ReadUInt32() != 0x504d444d) throw new Exception("Capture is not a Windows minidump.");
                reader.ReadUInt32(); var count = reader.ReadUInt32(); var directory = reader.ReadUInt32();
                if (count > 1000 || directory+(long)count*12 > file.Length) throw new Exception("Invalid minidump stream directory.");
                file.Position = directory; var types = new HashSet<uint>(); uint threads = 0;
                for (int i = 0; i < count; i++)
                {
                    var type = reader.ReadUInt32(); var size = reader.ReadUInt32(); var location = reader.ReadUInt32(); types.Add(type);
                    if (location+(long)size > file.Length) throw new Exception("Truncated minidump stream.");
                    if (type == 3) { var cursor = file.Position; file.Position = location; threads = reader.ReadUInt32(); file.Position = cursor; }
                }
                if (threads < 2 || !types.Contains(4) || !types.Contains(17)) throw new Exception("Capture lacks thread stacks, modules or thread information.");
            }
            if (application.HasExited || ReadLogs().Contains("SecondInstanceRejected", StringComparison.Ordinal)) throw new Exception("Capture helper entered normal application startup or stopped the target.");
            Console.WriteLine("PASS real out-of-process native thread dump contains thread/module streams without elevation or normal app startup; invalid PID/path requests rejected.");
            bool Ready() => ReadLogs().Contains("DirectXRendererReady", StringComparison.Ordinal);
            string ReadLogs()
            {
                var logs = Path.Combine(source, "logs", "ExperienceX");
                if (!Directory.Exists(logs)) return "";
                return string.Join('\n', Directory.GetFiles(logs, "*.jsonl").Select(path =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream); return reader.ReadToEnd();
                }));
            }
        }
        finally
        {
            if (application is not null)
            {
                if (!application.HasExited) { application.CloseMainWindow(); if (!application.WaitForExit(7000)) { application.Kill(); application.WaitForExit(5000); } }
                application.Dispose();
            }
            Console.WriteLine("Native capture evidence: "+root);
        }
    }
}
