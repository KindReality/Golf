using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Windows;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

internal static class PerspectiveShortcutChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "Controller-shortcuts-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                PerspectiveEditor? editor = null;
                try
                {
                    var first = new PerspectiveDefinition { Name = "first", Rectangle = new() { X = 20, Y = 20, Width = 40, Height = 40 } };
                    var second = new PerspectiveDefinition { Name = "second" };
                    var options = new ControllerOptions { ConfigurationMode = true, DeviceName = "shortcut-controller", UdpCommandsPort = FreeUdpPort(), UdpEventsPort = FreeUdpPort(), DefinitionsPort = FreeTcpPort(), UdpSendAttempts = 1, Perspectives = [first, second], Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") }) };
                    var path = Path.Combine(folder, "appsettings.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    await using (var host = new ControllerHost(path))
                    {
                        editor = new(host);
                        var preview = editor.Preview;
                        preview.SetPerspectives(host.Options.Perspectives, first.Id);
                        preview.Nudge(1, 0, 20);
                        Check(preview.Editing && preview.Shortcut!(Keys.Control | Keys.Z) && !preview.Editing, "Ctrl+Z cancels an unfinished movement.");
                        preview.Nudge(1, 0, 20); preview.FinishKeyboardEdit();
                        await Until(() => host.Options.Perspectives[0].Rectangle != first.Rectangle);
                        await Task.Delay(50);
                        Check(preview.Shortcut!(Keys.OemCloseBrackets), "] shortcut was not handled.");
                        await Until(() => host.Options.Perspectives[^1].Id == first.Id);
                        await Task.Delay(50);
                        Check(preview.Shortcut!(Keys.Control | Keys.Z), "Ctrl+Z shortcut was not handled.");
                        await Until(() => host.Options.Perspectives.Single(item => item.Id == first.Id).Rectangle == first.Rectangle);
                        Check(host.Options.Perspectives[^1].Id == first.Id, "Movement undo unexpectedly reverted layer order.");
                        await Task.Delay(50);
                        Check(preview.Shortcut!(Keys.OemOpenBrackets), "[ shortcut was not handled.");
                        await Until(() => host.Options.Perspectives[0].Id == first.Id);
                        await Task.Delay(50);
                        preview.Measure(new System.Windows.Size(960, 540)); preview.Arrange(new System.Windows.Rect(0, 0, 960, 540));
                        Check(preview.BeginPointer(new(384, 108)), "Perspective top edge was not selectable.");
                        preview.MovePointer(new(424, 128)); preview.FinishPointer();
                        await Until(() => host.Options.Perspectives.Single(p => p.Id == first.Id).Rectangle.Y > first.Rectangle.Y);
                        var edgeRectangle = host.Options.Perspectives.Single(p => p.Id == first.Id).Rectangle;
                        Check(edgeRectangle.X == first.Rectangle.X && edgeRectangle.Width == first.Rectangle.Width && Math.Abs(edgeRectangle.Y + edgeRectangle.Height - 60) < 1e-8, "Perspective edge changed its angle or moved its opposite side.");
                        await Task.Delay(50); Check(preview.Shortcut!(Keys.Control | Keys.Z), "Edge movement undo was not handled.");
                        await Until(() => host.Options.Perspectives.Single(p => p.Id == first.Id).Rectangle == first.Rectangle);
                        await host.SetConfigurationModeAsync(false);
                        await Until(() => !host.Options.ConfigurationMode);
                        Check(!preview.Shortcut!(Keys.Control | Keys.Z) && !preview.Shortcut!(Keys.OemOpenBrackets), "Editor shortcuts remained active outside configuration mode.");
                        preview.Stop(); editor.Dispose(); editor = null;
                    }
                    Console.WriteLine("Passed Controller Ctrl+Z for draft/saved rectangle movement, persisted bracket layer ordering and independent movement undo.");
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { editor?.Preview.Stop(); editor?.Dispose(); Telemetry.Stop(); Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static async Task Until(Func<bool> ready) { var end = DateTime.UtcNow.AddSeconds(5); while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException("Perspective shortcut did not apply."); await Task.Delay(10); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static int FreeUdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
}
