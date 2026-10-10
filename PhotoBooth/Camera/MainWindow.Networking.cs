using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace SaftApp;

public sealed partial class MainWindow
{
    private volatile BroadcastOptions _networkOptions = new();
    private string _experienceDeviceName = Environment.MachineName;
    private string? _networkConfiguration, _networkFailure;
    private UdpBroadcastListener? _networkListener;
    private readonly CancellationTokenSource _networkCancellation = new();
    private readonly DispatcherTimer _networkPoll = new() { Interval = TimeSpan.FromSeconds(1) };

    private void StartNetwork()
    {
        _networkListener = new(() => _networkOptions, async (message, token) =>
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !message.Targets(_networkOptions.EffectiveDeviceName) || message.IsExpired(DateTimeOffset.UtcNow)) return;
                // Camera observes events; coordinator rules are separate from capture UI.
                Telemetry.Info(message.Type == "event" ? "UdpEventReceived" : "UdpCommandUnsupported",
                    new { message.Source, message.Device, message.Event, message.Type }, message.RequestId);
            }, DispatcherPriority.Normal, token);
        });
        ReloadNetworkConfiguration();
        _networkPoll.Tick += (_, _) => ReloadNetworkConfiguration();
        _networkPoll.Start();
    }

    private void ReloadNetworkConfiguration()
    {
        try
        {
            var path = ResolveProjectRelativePath("appsettings.json") ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var text = File.ReadAllText(path);
            if (text == _networkConfiguration) return;
            var next = JsonSerializer.Deserialize<BroadcastOptions>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("Configuration must be an object.");
            using var document = JsonDocument.Parse(text);
            next = next with { UdpPort = null }; // The former shared port is replaced by the two configured ports.
            var target = document.RootElement.TryGetProperty("ExperienceDeviceName", out var device) ? device.GetString() : null;
            target = string.IsNullOrWhiteSpace(target) ? Environment.MachineName : target.Trim();
            BroadcastOptions.ValidateName(target, "ExperienceDeviceName", true);
            next.ValidateNetwork();
            if (_networkListener!.Port == 0 || _networkOptions.UdpCommandsPort != next.UdpCommandsPort || _networkOptions.UdpEventsPort != next.UdpEventsPort)
                _networkListener.Bind(next.UdpCommandsPort, next.UdpEventsPort);
            _networkOptions = next;
            _experienceDeviceName = target;
            _networkConfiguration = text;
            _networkFailure = null;
            devNetworkStatus.Text = target == "*" ? "All devices • broadcast" : target;
            devNetworkStatus.ToolTip = $"{next.EffectiveDeviceName} → {target}\nEvents: {next.UdpEventsPort} · Commands: {next.UdpCommandsPort}";
            Telemetry.Info("NetworkConfigurationLoaded", new
            {
                Path = path,
                next.DeviceName,
                next.EffectiveDeviceName,
                next.UdpCommandsPort,
                next.UdpEventsPort,
                next.UdpBroadcastAddress,
                next.UdpInterfaceAddress,
                ExperienceDeviceName = target
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or SocketException or InvalidOperationException)
        {
            if (_networkFailure != ex.Message) Telemetry.Error("NetworkConfigurationRejected", ex);
            _networkFailure = ex.Message;
            devNetworkStatus.Text = "Configuration needs attention";
            devNetworkStatus.ToolTip = ex.Message;
        }
    }

    // All trigger buttons share this pipeline; retries retain the same identity and payload.
    private async Task SendExperienceAsync(string experience, string? value, string trigger, string? displayName = null)
    {
        try
        {
            ReloadNetworkConfiguration();
            if (_networkConfiguration is null) throw new InvalidOperationException("No valid network configuration has been loaded. Check appsettings.json and the log.");
            var options = _networkOptions;
            var screen = displayName is null ? null : displayName + "/screen1";
            var payload = BroadcastEnvelope.Create(options, "command", _experienceDeviceName, new { experience, value, screen, trigger });
            var request = BroadcastEnvelope.Parse(payload);
            Telemetry.Info("ExperienceRequested", new { request.Device, request.Source, Trigger = trigger, Experience = experience, Value = value, Screen = screen }, request.RequestId);
            await UdpBroadcastSender.SendAsync(payload, options, _networkCancellation.Token);
        }
        catch (OperationCanceledException) when (_networkCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or InvalidOperationException)
        {
            Telemetry.Error("UdpSendFailed", ex);
            MessageBox.Show(this, $"Unable to send the Experience request: {ex.Message}", "Experience request", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopNetworkAsync()
    {
        _networkPoll.Stop();
        _networkCancellation.Cancel();
        if (_networkListener is not null) await _networkListener.DisposeAsync();
    }
}
