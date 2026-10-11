using System.Collections.Concurrent;
using System.IO;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace ExperienceX;

internal sealed class ControllerMediaDiscovery(Func<ExperienceOptions> options, Func<string, string>? sharePath = null,
    Func<byte[], ExperienceOptions, CancellationToken, Task>? send = null) : IAsyncDisposable
{
    private readonly AsyncTaskScope _tasks = new("Controller media discovery");
    private ConcurrentDictionary<string, byte> _requests = new(), _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _copyGate = new(1);
    private readonly ControllerMediaCache _cache = new();
    private readonly List<CancellationTokenSource> _rounds = [];
    private CancellationTokenSource? _round;
    private MediaDiscoveryOptions? _settings;
    private string? _routing;
    public void Configure(ExperienceOptions current)
    {
        var routing = $"{current.EffectiveDeviceName}|{current.UdpCommandsPort}|{current.UdpEventsPort}|{current.VideoDirectory}|{current.AudioDirectory}";
        if (_settings == current.MediaDiscovery && _routing == routing) return;
        _routing = routing;
        _settings = current.MediaDiscovery;
        _round?.Cancel();
        _requests = new(); _hosts = new(StringComparer.OrdinalIgnoreCase);
        if (!current.MediaDiscovery.Enabled) return;
        _round = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token);
        _rounds.Add(_round);
        var token = _round.Token;
        var requests = _requests;
        _tasks.Run(_ => Task.Run(() => DiscoverAsync(current, requests, token), token), "Discover controllers");
    }
    private async Task DiscoverAsync(ExperienceOptions current, ConcurrentDictionary<string, byte> requests, CancellationToken token)
    {
        for (int attempt = 0; attempt < current.MediaDiscovery.Attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            requests[id] = 0;
            try
            {
                var bytes = ControllerDiscovery.Request(options(), id);
                if (send is null) await UdpBroadcastSender.SendAsync(bytes, options(), token);
                else await send(bytes, options(), token);
                Telemetry.Info("ControllerDiscoverySent", new { Attempt = attempt + 1 }, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Telemetry.Warning("ControllerDiscoveryFailed", new { Reason = ex.Message }, id); }
            if (attempt + 1 < current.MediaDiscovery.Attempts) await Task.Delay(TimeSpan.FromSeconds(current.MediaDiscovery.RetryIntervalSeconds), token);
        }
    }
    public void Receive(BroadcastEnvelope message)
    {
        var current = options();
        if (_round is null || _round.IsCancellationRequested || !current.MediaDiscovery.Enabled || !message.Targets(current.EffectiveDeviceName) || message.IsExpired(DateTimeOffset.UtcNow)) return;
        try
        {
            var hostname = ControllerDiscovery.Hostname(message, _requests.Keys.ToHashSet());
            if (hostname is null) return;
            var destinations = new[] { current.VideoDirectory, current.AudioDirectory }.Select(d => MediaPath.ResolveDirectory(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var hosts = _hosts;
            if (!hosts.TryAdd(hostname, 0)) return;
            var share = sharePath?.Invoke(hostname) ?? ControllerDiscovery.MediaShare(hostname);
            Telemetry.Info("ControllerDiscovered", new { Hostname = hostname, MediaShare = share, message.Source }, message.RequestId);
            var token = _round.Token;
            _tasks.Run(_ => Task.Run(() => CopyAsync(hostname, share, destinations, hosts, token), token), "Cache controller media");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Telemetry.Warning("ControllerDiscoveryResponseInvalid", new { message.Source, Reason = ex.Message }, message.RequestId); }
    }
    private async Task CopyAsync(string hostname, string share, string[] destinations, ConcurrentDictionary<string, byte> hosts, CancellationToken token)
    {
        bool complete = false;
        try
        {
            await _copyGate.WaitAsync(token);
            try
            {
                complete = true;
                foreach (var destination in destinations) complete &= await _cache.SynchronizeAsync(hostname, share, destination, token);
                Telemetry.Info("ControllerMediaSyncCompleted", new { Hostname = hostname, Complete = complete, Destinations = destinations });
            }
            finally { _copyGate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Telemetry.Warning("ControllerMediaSyncFailed", new { Hostname = hostname, share, Reason = ex.Message }); }
        finally { if (!complete) hosts.TryRemove(hostname, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        _round?.Cancel();
        await _tasks.DisposeAsync();
        foreach (var round in _rounds) round.Dispose();
    }
}
