using System.IO;
using PhotoBooth.Networking;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

internal sealed class ExperienceAnnouncements(Func<ExperienceOptions> options) : IAsyncDisposable
{
    private readonly SnapshotHttpServer _server = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private string _revision = "";
    private Task _loop = Task.CompletedTask;
    private bool _started;
    private volatile string? _failure;
    public void Publish(IEnumerable<MainWindow> windows, ScreenMediaCatalog catalog, AudioOutput[] audio, string? defaultId)
    {
        var settings = options();
        var active = windows.ToDictionary(w => w.DisplayName, w => w.State != RendererState.Failed, StringComparer.OrdinalIgnoreCase);
        var inventory = new ExperienceInventory
        {
            InstanceId = _instance, DeviceName = settings.EffectiveDeviceName, Hostname = Environment.MachineName,
            Screens = settings.DisplayScreens.SelectMany(d => d.Value.Select(s => new ExperienceScreen
            {
                Id = s.Id, Name = s.Name, Display = d.Key,
                DisplayLabel = settings.DisplayAliases.FirstOrDefault(a => a.Value.Equals(d.Key, StringComparison.OrdinalIgnoreCase)).Key ?? d.Key,
                Kind = s.Type, Roles = s.Roles, Available = active.GetValueOrDefault(d.Key) && System.Windows.Forms.Screen.AllScreens.Any(display => display.DeviceName.Equals(d.Key, StringComparison.OrdinalIgnoreCase)),
                MediaBound = s.IsVideo && catalog.Match(s) is not null
            })).ToArray(),
            Speakers = audio.Select(d => new ExperienceSpeaker { Id = d.Id, Name = d.Name, Channels = d.Channels, Default = d.Id == defaultId, Roles = AudioDeviceOptions.RolesFor(d, settings) }).ToArray(),
            VideoFiles = Files(settings.VideoDirectory, VideoExtensions), AudioFiles = Files(settings.AudioDirectory, AudioExtensions)
        };
        var bytes = inventory.Serialize(); var revision = ExperienceInventory.Revision(bytes);
        var portChanged = _server.Port != settings.InventoryPort;
        try { _server.Bind(settings.InventoryPort); _failure = null; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        { if (_failure != ex.Message) Telemetry.Error("ExperienceInventoryUnavailable", ex, new { settings.InventoryPort, Hint = "Check the port and run Install-Experience.cmd for remote access." }); _failure = ex.Message; return; }
        if (revision == _revision) { if (portChanged) RequestAnnouncement(); return; }
        _server.Publish(bytes, revision); Volatile.Write(ref _revision, revision);
        RequestAnnouncement();
    }
    internal static readonly HashSet<string> VideoExtensions = new([".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".mkv", ".wmv"], StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> AudioExtensions = new([".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".wma"], StringComparer.OrdinalIgnoreCase);
    private static string[] Files(string directory, HashSet<string> extensions)
    {
        var path = MediaPath.ResolveDirectory(directory);
        return PhotoBooth.Media.MediaLibrary.Read(path).Where(entry => !entry.IsFolder && extensions.Contains(Path.GetExtension(entry.RelativePath)))
            .Select(entry => entry.RelativePath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void Start() { if (_started) return; _started = true; _loop = RunAsync(); }
    public void RequestAnnouncement() { if (_signal.CurrentCount == 0) try { _signal.Release(); } catch (SemaphoreFullException) { } }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var revision = Volatile.Read(ref _revision);
                if (revision.Length > 0 && _failure is null)
                    await UdpBroadcastSender.SendAsync(ExperienceDiscovery.Announce(options(), _instance, revision, _server.Port), options(), _stop.Token);
                await _signal.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Telemetry.Error("ExperienceAnnouncementFailed", ex); try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; await _server.DisposeAsync(); _signal.Dispose(); _stop.Dispose(); }
}
