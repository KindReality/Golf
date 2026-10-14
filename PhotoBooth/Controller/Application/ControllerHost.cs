using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Hardware;
using Controller.Networking;
using Controller.Scheduling;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Application;

internal sealed class ControllerHost : IAsyncDisposable
{
    private readonly ConfigurationService _configuration;
    private readonly ArduinoConnections _arduinos;
    private readonly UdpBroadcastListener _listener;
    private readonly RequestDeduplicator _received = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ControllerOptions _options;
    private readonly ControllerAnnouncements _announcements;
    public ExperienceRegistry Experiences { get; }
    public Task DiscoverExperiencesAsync() => UdpBroadcastSender.SendAsync(ExperienceDiscovery.Request(Options), Options, _shutdown.Token);
    public async Task SendLiveAsync(byte[] bytes)
    {
        var message = BroadcastEnvelope.Parse(bytes);
        await UdpBroadcastSender.SendAsync(bytes, Options, _shutdown.Token);
        Log.Info("LivePlaybackSent", $"Live {ControllerOptions.Text(message.Body, "experience")}: {ControllerOptions.Text(message.Body, "value")} to {message.Device}", new { message.Device, message.Body }, message.RequestId);
    }
    public ActivityLog Log { get; } = new();
    public PlaylistEngine Engine { get; }
    public SceneManager Scenes { get; }
    public ControllerOptions Options => Volatile.Read(ref _options);
    public string ConfigurationPath => _configuration.Path;
    public Task SetConfigurationModeAsync(bool value) => _configuration.SetConfigurationModeAsync(value);
    public async Task SavePerspectivesAsync(PerspectiveDefinition[] expected, PerspectiveDefinition[] next)
    {
        await _configuration.SavePerspectivesAsync(expected, next);
        Log.Info("PerspectivesSaved", "Saved shared perspective definitions", new
        {
            Count = next.Length,
            Changed = next.Where(p => !expected.Contains(p)).ToArray(),
            Removed = expected.Where(p => !next.Any(n => n.Id == p.Id)).Select(p => new { p.Id, p.Name }).ToArray()
        });
    }
    public ArduinoStatus[] ArduinoStatus => _arduinos.Snapshot();

    public ControllerHost(string path, string? templateDirectory = null)
    {
        Telemetry.Start("Controller", path);
        var environment = BuildEnvironment.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.environment.json"));
        _configuration = new(path, environment, Log, ApplyAsync, templateDirectory);
        _options = _configuration.Initial;
        Experiences = new(Log);
        _announcements = new(() => Options, Log);
        _arduinos = new(Log, ReceiveSerialAsync);
        Engine = new(_options, new CommandDispatcher(_arduinos, ReceiveUdpAsync, _shutdown.Token), Log);
        Scenes = new(_options, Engine, Log);
        _listener = new(() => Options, ReceiveUdpAsync);
        try
        {
            _listener.Bind(_options.UdpCommandsPort, _options.UdpEventsPort);
            _arduinos.Apply(_options.Arduinos);
            _announcements.Start();
            Engine.Start();
            Scenes.Start();
            _configuration.Start();
            Log.Info("ControllerReady", $"{_options.EffectiveDeviceName}: ready ({environment.Environment})",
                new { Path = path, environment.Environment, _options.EffectiveDeviceName, _options.UdpEventsPort, _options.UdpCommandsPort });
            Telemetry.SetHealthProvider(() => new { Scene = Scenes.Snapshot(), Running = Engine.Snapshot(), Arduinos = ArduinoStatus });
        }
        catch
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private Task ApplyAsync(ControllerOptions options)
    {
        if (Options.UdpEventsPort != options.UdpEventsPort || Options.UdpCommandsPort != options.UdpCommandsPort)
            _listener.Bind(options.UdpCommandsPort, options.UdpEventsPort);
        _arduinos.Apply(options.Arduinos);
        Volatile.Write(ref _options, options);
        Engine.Apply(options);
        Scenes.Apply(options);
        _announcements.Publish();
        return Task.CompletedTask;
    }

    private async Task ReceiveUdpAsync(BroadcastEnvelope message, CancellationToken token)
    {
        if (token.IsCancellationRequested || !message.Targets(Options.EffectiveDeviceName) || message.IsExpired(DateTimeOffset.UtcNow)) return;
        if (!_received.Accept(message.Source.ToUpperInvariant(), message.RequestId, Options.RequestDeduplicationSeconds)) return;
        if (message.Event == ExperienceDiscovery.Event) { Experiences.Receive(message); return; }
        if (ExperienceDiscovery.IsRequest(message)) return;
        Telemetry.SetRequestContext(message.RequestId, new()
        {
            Source = message.Source,
            Device = message.Device,
            Scene = message.Context?.Scene,
            Playlist = message.Context?.Playlist,
            Item = message.Context?.Item,
            IsAmbient = message.Context?.IsAmbient == true,
            Roles = ControllerOptions.Text(message.Body, "roles"),
            Experience = ControllerOptions.Text(message.Body, "experience"),
            Value = ControllerOptions.Text(message.Body, "value"),
            CorrelationId = ControllerOptions.Text(message.Body, "correlationId")
        });
        if (ControllerDiscovery.IsRequest(message))
        {
            await _announcements.SendAsync(token).ConfigureAwait(false);
            Log.Info("DiscoveryResponseSent", $"Media discovery response to {message.Source}", new { Hostname = Environment.MachineName, MediaShare = ControllerDiscovery.MediaShare(Environment.MachineName) }, message.RequestId);
            return;
        }
        if (message.Type == "event")
        {
            if (message.Event == ControllerDiscovery.ResponseEvent) return;
            ControllerOptions.RequireInput(message.Body);
            Engine.Receive(message);
        }
        else
        {
            var command = ControllerOptions.Text(message.Body, "command");
            if (command == "perspective.create")
            {
                var request=PerspectiveCreateRequest.Parse(message.Body);
                if(request.ControllerId!=Options.ControllerId)return;
                var saved=await _configuration.CreatePerspectiveAsync(request).ConfigureAwait(false);
                Log.Info("PerspectiveCreatedFromExperience", $"{message.Source}: saved {saved.Name}", new { message.Source, saved.Id, saved.Name, saved.Rectangle }, message.RequestId);
                return;
            }
            if (command is "scene" or "playlist")
            {
                var name = ControllerOptions.Text(message.Body, "value") ?? throw new ArgumentException("Controller commands require value (a scene or playlist name).");
                var chain = BroadcastChain.From(message);
                if (command == "scene") Scenes.Request(name, chain);
                else Engine.Run(name, chain: chain, owner: $"Network: {message.Source}", isAmbient: message.Context?.IsAmbient == true);
            }
            else Log.Info("CommandObserved", $"Command observed from {message.Source}", new { message.Source, message.Device }, message.RequestId);
        }
    }

    private async Task ReceiveSerialAsync(byte[] bytes, string device, CancellationToken token)
    {
        var message = BroadcastEnvelope.Parse(bytes);
        if (message.Type != "event" || !ControllerOptions.Same(message.Source, device))
            throw new ArgumentException("Arduino input must be an event whose source matches its configured device name.");
        ControllerOptions.RequireInput(message.Body);
        if (message.IsExpired(DateTimeOffset.UtcNow) || !_received.Accept(message.Source.ToUpperInvariant(), message.RequestId, Options.RequestDeduplicationSeconds)) return;
        if (message.Targets(Options.EffectiveDeviceName)) Engine.Receive(message);
        // The Arduino announces its event to all intended LAN recipients just like an ESP32.
        try { await UdpBroadcastSender.SendAsync(bytes, Options, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("ArduinoEventBroadcastFailed", ex, new { Device = device }, message.RequestId); }
    }

    public async ValueTask DisposeAsync()
    {
        await _configuration.DisposeAsync().ConfigureAwait(false);
        await _announcements.DisposeAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        await Experiences.DisposeAsync().ConfigureAwait(false);
        _shutdown.Cancel();
        await Scenes.DisposeAsync().ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
        await _arduinos.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
