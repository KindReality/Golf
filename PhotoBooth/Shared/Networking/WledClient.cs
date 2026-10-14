using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace PhotoBooth.Networking;

public sealed record WledSegment(int Id, string Name, int Start, int Stop, int StartY, int StopY, bool Matrix)
{
    public int Width => Stop - Start;
    public int Height => Matrix ? StopY - StartY : 1;
    public int PixelCount => Width * Height;
    public override string ToString() => $"{Id}: {Name} • " + (Matrix ? $"{Width} × {Height}" : $"{PixelCount} LEDs");
    public IEnumerable<int> PixelIndices(int matrixWidth)
    {
        if (!Matrix) { for (int x = Start; x < Stop; x++) yield return x; }
        else for (int y = StartY; y < StopY; y++) for (int x = Start; x < Stop; x++) yield return y * matrixWidth + x;
    }
}
public sealed record WledDevice(string Address, string Mac, string Name, int PixelCount, int MatrixWidth, int MatrixHeight, WledSegment[] Segments)
{
    public override string ToString() => $"{Name} • {Address}";
    public LedPerspective Perspective(WledSegment segment, bool matrix, int fps = 30, double thickness = 10) => new()
    {
        Address = Address, DeviceMac = Mac, DeviceName = Name, SegmentId = segment.Id,
        PixelCount = segment.PixelCount, Width = matrix ? segment.Width : 0, Height = matrix ? segment.Height : 0,
        FramesPerSecond = fps, SamplingThicknessPercent = thickness
    };
}

public sealed class WledClient : IDisposable
{
    private readonly HttpClient _http;
    public WledClient(HttpMessageHandler? handler = null) => _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    public static Uri Endpoint(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || address != address.Trim() ||
            address.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0 || address.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate("http://" + address + "/", UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown || uri.Port is < 1 or > 65535)
            throw new ArgumentException("WLED address must be an IP address or hostname, optionally followed by an HTTP port.");
        return uri;
    }
    public async Task<WledDevice> ReadAsync(string address, CancellationToken token = default)
    {
        using var json=await ReadJsonAsync(address,"json",token).ConfigureAwait(false);
        return Parse(address,json.RootElement);
    }
    public async Task ValidateStreamingAsync(string address,CancellationToken token=default)
    {
        using var json=await ReadJsonAsync(address,"json/cfg",token).ConfigureAwait(false);
        ValidateStreaming(json.RootElement);
    }
    public static void ValidateStreaming(JsonElement configuration)
    {
        var live=configuration.GetProperty("if").GetProperty("live");
        if(!live.GetProperty("en").GetBoolean()) throw new ArgumentException("Enable Receive UDP realtime in WLED Sync settings.");
        if(live.GetProperty("mso").GetBoolean()) throw new ArgumentException("Disable Use main segment only in WLED Sync settings to stream multiple segments.");
        if(live.TryGetProperty("rlm",out var maps) && !maps.GetBoolean()) throw new ArgumentException("Enable Respect LED Maps in WLED Sync settings.");
        if(Number(live,"offset")!=0 || Number(live.GetProperty("dmx"),"addr",1)/3!=0) throw new ArgumentException("WLED realtime LED offset must be zero and DMX start address must be 1 for segment addressing.");
        if(Number(live,"timeout") is <=0 or >=650) throw new ArgumentException("Use a finite WLED realtime timeout below 65000 ms so configured behavior resumes after streaming.");
    }
    private async Task<JsonDocument> ReadJsonAsync(string address,string path,CancellationToken token)
    {
        using var response = await _http.GetAsync(new Uri(Endpoint(address), path), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new ArgumentException("WLED response exceeds 1 MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > 1024 * 1024) throw new ArgumentException("WLED response exceeds 1 MB.");
            bytes.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(bytes.ToArray());
    }
    public static WledDevice Parse(string address, JsonElement root)
    {
        Endpoint(address);
        var info = root.GetProperty("info");
        if (!info.TryGetProperty("brand", out var brand) || !string.Equals(brand.GetString(), "WLED", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected device is not WLED.");
        var mac = (info.GetProperty("mac").GetString() ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit)) throw new ArgumentException("WLED identity is invalid.");
        var leds = info.GetProperty("leds");
        int count = leds.GetProperty("count").GetInt32(), mw = 0, mh = 0;
        if (leds.TryGetProperty("matrix", out var dimensions)) { mw = Number(dimensions, "w"); mh = Number(dimensions, "h"); }
        if (count is < 1 or > 65535 || mw < 0 || mh < 0 || (long)mw * mh > count || (mw == 0) != (mh == 0))
            throw new ArgumentException("Invalid WLED dimensions.");
        var segments = new List<WledSegment>();
        foreach (var s in root.GetProperty("state").GetProperty("seg").EnumerateArray())
        {
            int start = Number(s, "start"), stop = Number(s, "stop"), sy = Number(s, "startY"), ey = Number(s, "stopY", 1);
            bool matrix = mw > 0 && start < mw && stop <= mw && ey > sy;
            var segment = new WledSegment(Number(s, "id"), s.TryGetProperty("n", out var n) ? n.GetString() ?? "Segment" : "Segment", start, stop, sy, ey, matrix);
            if (segment.Id is < 0 or > 255 || start < 0 || stop <= start || stop > (matrix ? mw : count) || matrix && (sy < 0 || ey > mh) || segment.PixelCount > 65535)
                throw new ArgumentException("Invalid WLED segment bounds.");
            if (segments.Any(v => v.Id == segment.Id)) throw new ArgumentException("Duplicate WLED segment ID.");
            segments.Add(segment);
        }
        return new(address, mac, info.GetProperty("name").GetString() ?? "WLED", count, mw, mh, segments.ToArray());
    }
    private static int Number(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    public void Dispose() => _http.Dispose();
}
