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
    private readonly CommandDispatcher _dispatcher;
    private readonly ControllerPeers _controllers;
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
    public string[] KnownRoles => RoleCatalog.Names(PlaylistRoles.Names(Options.Playlists)
        .Concat(_controllers.Definitions.SelectMany(d => d.Roles)).Concat(LivePlayback.Roles(Experiences.Snapshot()).Select(r => r.Name)));
    public string ConfigurationPath => _configuration.Path;
    public Task SetConfigurationModeAsync(bool value) => _configuration.SetConfigurationModeAsync(value);
    public async Task SavePinnedActionAsync(PinnedActionOptions? expected, PinnedActionOptions? next)
    {
        await _configuration.SavePinnedActionAsync(expected, next);
        Log.Info("PinnedActionSaved", next is null ? $"Removed pin: {expected!.Name}" : $"Saved pin: {next.Name}");
    }
    public async Task SendManualAsync(PlaylistCommand command, string? eventSource = null, string? label = null)
    {
        var options = Options;
        PinnedActionOptions.ValidateCommand(command, options, eventSource);
        if (command.Playlist is { } playlist)
        {
            options.Playlist(playlist, "storyboard");
            Engine.RunManually(playlist);
            Log.Info("ManualPlaylistStarted", $"Manual playlist: {playlist}");
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        var source = eventSource ?? options.EffectiveDeviceName;
        var context = new CommandContext { Scene = Scenes.Snapshot().Active, Playlist = "Manual", Item = label ?? "Send action" };
        var outbound = command with { Context = context };
        Telemetry.SetRequestContext(id, new() { Source = source, Device = command.Device, Scene = context.Scene, Playlist = context.Playlist, Item = context.Item,
            Experience = ControllerOptions.Text(command.Payload, "experience"), Value = ControllerOptions.Text(command.Payload, "value"), Priority = "medium" });
        try
        {
            if (command.Transport == "arduino") await _dispatcher.SendAsync(outbound, options, BroadcastChain.Root(source), id, _shutdown.Token);
            else
            {
                var bytes = BroadcastEnvelope.Create(options with { DeviceName = source }, command.Type, command.Device!, command.Payload, id, BroadcastChain.Root(source), context, CommandPriority.Medium);
                try { await UdpBroadcastSender.SendAsync(bytes, options, _shutdown.Token); }
                finally { await ReceiveUdpAsync(BroadcastEnvelope.Parse(bytes), _shutdown.Token); } // Deduplication suppresses the UDP echo.
            }
            var description = command.Type == "event" ? ControllerOptions.Text(command.Payload, "event") : ShowEditSession.CommandSummary(command);
            Log.Info(command.Type == "event" ? "ManualEventSent" : "ManualCommandSent", $"Sent {description} → {command.Device}", new { Source = source, command.Device, command.Type, command.Payload }, id);
        }
        catch (Exception ex) { Log.Error("ManualActionFailed", ex, new { command.Device, command.Type }, id); throw; }
    }
    public async Task SaveShowAsync(ShowConfiguration expected, ShowConfiguration next)
    {
        await _configuration.SaveShowAsync(expected, next);
        Log.Info("ShowConfigurationSaved", "Saved scenes, playlists and triggers; running storyboards retain their settings", new
        { Scenes = next.Scenes.Select(s => new { s.Name, s.ActivatingPlaylist, s.AmbientPlaylist, s.Transition }).ToArray(), Playlists = next.Playlists.Select(p => new { p.Name, p.Type, Items = p.Items.Length }).ToArray(), Triggers = next.Triggers.Select(t => t.Name).ToArray() });
    }
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
    public void ClearRunningPlaylists()
    {
        Scenes.StopImmediately();
        Log.Info("RunningPlaylistsCleared", "Stopped all running playlists; existing playback continues");
    }

    public ControllerHost(string path, string? templateDirectory = null)
    {
        Telemetry.Start("Controller", path);
        var environment = BuildEnvironment.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.environment.json"));
        _configuration = new(path, environment, Log, ApplyAsync, templateDirectory);
        _options = _configuration.Initial;
        Experiences = new(Log);
        _controllers = new(() => Options, Log, () => _announcements!.PublishCatalog());
        _announcements = new(() => Options, Log, peers: () => _controllers.Definitions,
            assignedRoles: () => LivePlayback.Roles(Experiences.Snapshot()).Select(r => r.Name));
        _arduinos = new(Log, ReceiveSerialAsync);
        _dispatcher = new(_arduinos, ReceiveUdpAsync, _shutdown.Token);
        Engine = new(_options, _dispatcher, Log) { ReadExperiences = Experiences.Snapshot };
        Scenes = new(_options, Engine, Log);
        _listener = new(() => Options, ReceiveUdpAsync, device => _arduinos.HasTarget(device));
        try
        {
            _listener.Bind(_options.UdpCommandsPort, _options.UdpEventsPort);
            _arduinos.Apply(_options.Arduinos, _options.ArduinoDiscovery);
            _announcements.Start();
            _controllers.Start();
            Engine.Start();
            Scenes.Start();
            _configuration.Start();
            Log.Info("ControllerReady", $"{_options.EffectiveDeviceName}: ready ({environment.Environment})",
                new { Path = path, environment.Environment, _options.EffectiveDeviceName, _options.UdpEventsPort, _options.UdpCommandsPort });
            Telemetry.SetHealthProvider(() => new { Scene = Scenes.Snapshot(), Running = Engine.Snapshot(), LoopVideoTargets = Engine.LoopAnnouncementCount, Arduinos = ArduinoStatus });
        }
        catch
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private Task ApplyAsync(ControllerOptions options)
    {
        var pinsOnly = System.Text.Json.JsonSerializer.Serialize(Options with { PinnedActions = [] }, ControllerOptions.Json) ==
            System.Text.Json.JsonSerializer.Serialize(options with { PinnedActions = [] }, ControllerOptions.Json);
        // Personal shortcuts must not reset ambient timers or restart hardware discovery.
        if (pinsOnly) { Volatile.Write(ref _options, options); return Task.CompletedTask; }
        if (Options.UdpEventsPort != options.UdpEventsPort || Options.UdpCommandsPort != options.UdpCommandsPort)
            _listener.Bind(options.UdpCommandsPort, options.UdpEventsPort);
        _arduinos.Apply(options.Arduinos, options.ArduinoDiscovery);
        Volatile.Write(ref _options, options);
        Engine.Apply(options);
        Scenes.Apply(options);
        _announcements.Publish();
        return Task.CompletedTask;
    }

    private async Task ReceiveUdpAsync(BroadcastEnvelope message, CancellationToken token)
    {
        if (token.IsCancellationRequested || message.IsExpired(DateTimeOffset.UtcNow)) return;
        // A board is a named network destination even though USB provides its last hop.
        // Wildcard commands are not blindly forwarded to attached hardware.
        if (message.Type == "command" && message.Device != "*" && _arduinos.HasTarget(message.Device))
        {
            if (!_received.Accept(message.Source.ToUpperInvariant(), message.RequestId, Options.RequestDeduplicationSeconds)) return;
            await _arduinos.SendAsync(message.Device, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message.Body), token).ConfigureAwait(false);
            Log.Info("ArduinoNetworkCommandSent", $"{message.Source} → Arduino {message.Device}", new { message.Source, message.Device, message.Body }, message.RequestId);
            return;
        }
        if (!message.Targets(Options.EffectiveDeviceName)) return;
        if (!_received.Accept(message.Source.ToUpperInvariant(), message.RequestId, Options.RequestDeduplicationSeconds)) return;
        if (message.Event == ControllerDiscovery.ResponseEvent) { _controllers.Receive(message); return; }
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
            if (PlaybackPriorityEvent.TryDescribe(message, out var priorityActivity))
                Log.Info("PlaybackPriorityRejected", priorityActivity, message.Body, message.RequestId);
            else
                Log.Info("NetworkEventReceived", $"{message.Source}: {message.Event} · {ControllerOptions.Text(message.Body, "input")}", message.Body, message.RequestId);
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
            throw new ArgumentException("Arduino input must be an event whose source matches its registered device name.");
        ControllerOptions.RequireInput(message.Body);
        if (message.IsExpired(DateTimeOffset.UtcNow) || !_received.Accept(message.Source.ToUpperInvariant(), message.RequestId, Options.RequestDeduplicationSeconds)) return;
        Log.Info("ArduinoEventReceived", $"{device}: {message.Event} · {ControllerOptions.Text(message.Body, "input")}", message.Body, message.RequestId);
        if (message.Event == "device.status")
            Log.Info("ArduinoStatusReceived", $"{device}: serial device status", message.Body, message.RequestId);
        if (message.Targets(Options.EffectiveDeviceName)) Engine.Receive(message);
        // The Arduino announces its event to all intended LAN recipients just like an ESP32.
        try { await UdpBroadcastSender.SendAsync(bytes, Options, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("ArduinoEventBroadcastFailed", ex, new { Device = device }, message.RequestId); }
    }

    public async ValueTask DisposeAsync()
    {
        await _configuration.DisposeAsync().ConfigureAwait(false);
        await _controllers.DisposeAsync().ConfigureAwait(false);
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
