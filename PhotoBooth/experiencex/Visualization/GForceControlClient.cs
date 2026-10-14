using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExperienceX;

// G-Force 5.8.3's private SSMP protocol, recovered from the installed Toolbar.
// Calls go to an owned Standalone over loopback; no Toolbar, DLL ABI or keyboard input.
internal sealed class GForceControlClient : IDisposable
{
    private const int HeaderLength = 76, MaximumPayload = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TcpClient _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly TimeSpan _timeout;
    private uint _requestId;
    private bool _disposed;
    internal event Action<string>? ConsoleMessage;

    private GForceControlClient(TcpClient socket, TimeSpan timeout)
    {
        _socket = socket;
        _stream = socket.GetStream();
        _timeout = timeout;
    }

    internal static Task<GForceControlClient> ConnectAsync(int processId, CancellationToken cancellation = default) =>
        ConnectAsync(new IPEndPoint(IPAddress.Loopback, GForceControlEndpoint.FindPort(processId)), TimeSpan.FromSeconds(3), cancellation);

    internal static async Task<GForceControlClient> ConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!IPAddress.IsLoopback(endpoint.Address)) throw new ArgumentException("G-Force control must use loopback.", nameof(endpoint));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var socket = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            var client = new GForceControlClient(socket, timeout);
            try
            {
                // This is the same initial interpreter command sent by the Toolbar.
                await client.ExecuteAsync("SetClientDesc( \"ExperienceX\", 530 );", cancellation).ConfigureAwait(false);
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        catch { socket.Dispose(); throw; }
    }

    internal Task<string> GetActiveThemeAsync(CancellationToken token = default) => ExecuteAsync("print( GetActiveTheme() );", token);
    internal Task<string> GetThemesAsync(CancellationToken token = default) => ExecuteAsync("print( GetThemeList() );", token);
    internal Task<string> GetColorMapAsync(CancellationToken token = default) => ExecuteAsync("print( GetCurConfigName( \"ColorMap\" ) );", token);
    internal Task<string> ActivateThemeAsync(string name, CancellationToken token = default) => ExecuteAsync($"ActivateTheme( {Quote(name)} );", token);
    internal Task<string> SetColorMapAsync(string name, double transitionSeconds = 1, CancellationToken token = default) =>
        ExecuteAsync($"StartConfig( \"ColorMap\", {Quote(name)}, {Number(transitionSeconds, 0, 60)} );", token);
    internal Task<string> SetColorMapSlideshowAsync(bool enabled, CancellationToken token = default) =>
        ExecuteAsync($"SetSlideshowEnabled( \"ColorMap\", {(enabled ? 1 : 0)} );", token);
    internal Task<string> SetAudioSensitivityAsync(double sensitivity, CancellationToken token = default) =>
        ExecuteAsync($"SetPref( \"Audio.Response.Scale\", {Number(sensitivity, .001, 1000)} );", token);
    internal Task<string> GetAudioSensitivityAsync(CancellationToken token = default) => ExecuteAsync("printf( \"%f\", GetPref( \"Audio.Response.Scale\" ) );", token);
    internal Task<string> StartParticleAsync(string name, CancellationToken token = default) => ExecuteAsync($"StartConfig( \"Particle\", {Quote(name)}, 0 );", token);
    internal async Task<string[]> NamesAsync(string family, CancellationToken token, bool all = true)
    {
        var text = family == "Theme" ? await GetThemesAsync(token) :
            await ExecuteAsync($"print( GetConfigList( {Quote(family)}, {Literal(all ? "(All)" : "")}, 0, 0, 0 ) );", token);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')[0].TrimEnd('\r'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal Task<string> CurrentAsync(string family, CancellationToken token) => family == "Theme" ? GetActiveThemeAsync(token) :
        ExecuteAsync($"print( GetCurConfigName( {Quote(family)} ) );", token);
    internal Task<string> SelectAsync(string family, string name, double? transition, CancellationToken token) =>
        ExecuteAsync($"StartConfig( {Quote(family)}, {Quote(name)}, {Timing(transition, -1)} );", token);
    internal Task<string> SetFlowAsync(string field, string union, double? transition, double? delay, CancellationToken token) =>
        ExecuteAsync($"SetFlowField( {Quote(field)}, {Literal(union)}, {Timing(transition, -1)}, {Timing(delay, 0)}, 1 );", token);
    internal Task<string> StartEffectAsync(string family, string name, double? duration, CancellationToken token) =>
        ExecuteAsync($"{(family == "Particle" ? "StartParticle" : "StartSprite")}( {Quote(name)}, {Timing(duration, 0)} );", token);
    internal Task<string> StopEffectsAsync(string family, string? name, double? transition, CancellationToken token)
    {
        if (family is "Particle" or "Sprite")
        {
            var duration = Timing(transition, 1); // G-Force's StopParticlesAndSprites default is one second.
            var plural = family == "Particle" ? "Particles" : "Sprites";
            return string.IsNullOrEmpty(name) ? ExecuteAsync($"KillRunning{plural}( {duration} );", token) :
                ExecuteAsync($"Kill{plural}( {Quote(name)}, 0, {duration} );", token);
        }
        throw new ArgumentException("Only particles and sprites can be stopped.", nameof(family));
    }
    internal Task<string> DisableAutomaticChangesAsync(CancellationToken token) => ExecuteAsync("SetSlideshowEnabled( \"*\", 0 );", token);
    internal async Task<bool> EnterFullscreenAsync(CancellationToken token)
    {
        await ExecuteAsync("SetFullscreen( 1 );", token);
        return (await ExecuteAsync("printf( \"%d\", IsFullscreen() );", token)).Trim() == "1";
    }
    internal async Task<bool> EnsureAutomaticChangesOffAsync(CancellationToken token)
    {
        var state = await ExecuteAsync("printf( \"%d %d %d %d %d %d\", SlideshowEnabled( \"ColorMap\" ), SlideshowEnabled( \"WaveShape\" ), SlideshowEnabled( \"FlowField\" ), SlideshowEnabled( \"FlowField Union\" ), SlideshowEnabled( \"Particle\" ), SlideshowEnabled( \"Sprite\" ) );", token);
        if (state.Trim() == "0 0 0 0 0 0") return false;
        await DisableAutomaticChangesAsync(token);
        return true;
    }
    private static string Literal(string name) => name == "" ? "\"\"" : Quote(name);
    private static string Timing(double? seconds, double fallback)
    {
        VisualizationOptions.ValidateTime(seconds);
        return (seconds ?? fallback).ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Number(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
    private static string Quote(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(char.IsControl)) throw new ArgumentException("A nonempty G-Force name without control characters is required.", nameof(name));
        return "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    internal async Task<string> ExecuteAsync(string command, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (command.Contains('\0')) throw new ArgumentException("Commands cannot contain NUL.", nameof(command));
        var payload = Utf8.GetBytes(command + '\0');
        if (payload.Length > MaximumPayload) throw new ArgumentException("G-Force command exceeds the payload limit.", nameof(command));
        await _requests.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(_timeout);
            var token = deadline.Token;
            var id = ++_requestId;
            var header = new byte[HeaderLength];
            header[0] = 2; // SSMP version.
            header[1] = 1; // Execute interpreter command; replies have type 3.
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), id);
            header[8] = 1; // Request a reply even when the command prints no output.
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(64), payload.Length);
            try
            {
                await _stream.WriteAsync(header, token).ConfigureAwait(false);
                await _stream.WriteAsync(payload, token).ConfigureAwait(false);
                // Startup and setting changes can emit binary type-2 notifications between replies.
                // They use the same framing, but are not interpreter output or request completions.
                for (int notifications = 0; notifications < 256; notifications++)
                {
                    await _stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                    if (header[0] != 2 || header[1] is not (2 or 3) ||
                        (header[1] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != id))
                        throw new InvalidDataException($"Unexpected G-Force reply header {Convert.ToHexString(header.AsSpan(0, 16))}; expected version 2, type 3, request {id}.");
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(64));
                    if (length < 0 || length > MaximumPayload) throw new InvalidDataException("Invalid G-Force reply length.");
                    var reply = new byte[length];
                    await _stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
                    if (header[1] == 2)
                    {
                        if (header[2] == 41 && reply.Length > 17 && BinaryPrimitives.ReadInt32BigEndian(reply) == 99)
                        {
                            // Verified console notification layout: binary prefix, separator, UTF-8 text.
                            try { ConsoleMessage?.Invoke(Utf8.GetString(reply.AsSpan(17)).TrimEnd('\0', '\n', '\r')); }
                            catch (DecoderFallbackException) { }
                        }
                        continue;
                    }
                    var text = Utf8.GetString(reply).TrimEnd('\0');
                    // The reply is an acknowledgment/output, not a reliable interpreter error status.
                    // G-Force reports script errors through console notifications; verify settings by readback.
                    return text;
                }
                throw new InvalidDataException("G-Force sent too many notifications without a command reply.");
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                Dispose(); // A late reply must never be mistaken for the next command's result.
                throw new TimeoutException("G-Force control request timed out; reconnect before retrying.");
            }
            catch { Dispose(); throw; }
        }
        finally { _requests.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
        // Keep the semaphore alive until any pending requests have unwound.
    }
}
