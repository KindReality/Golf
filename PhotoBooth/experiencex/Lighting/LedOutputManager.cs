using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

namespace ExperienceX;

internal sealed record LedOutputScreen(string Display, Rectangle Bounds, ScreenOptions Screen);

internal sealed class LedOutputManager : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string,WledDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private LedOutputScreen[] _screens=[];
    private readonly Task _resolver, _output;
    private readonly ConcurrentDictionary<string,string> _failures=new();
    private long _frames, _captureCount;
    internal long FramesSent => Interlocked.Read(ref _frames);
    internal long Captures => Interlocked.Read(ref _captureCount);
    internal LedOutputManager()
    {
        _resolver=Task.Run(ResolveAsync); _output=Task.Run(OutputAsync);
    }
    internal void Update(IEnumerable<LedOutputScreen> screens) => Volatile.Write(ref _screens,screens.ToArray());
    private async Task ResolveAsync()
    {
        using var http=new WledClient(); var token=_shutdown.Token;
        try
        {
            while(!token.IsCancellationRequested)
            {
                var targets=Volatile.Read(ref _screens).Select(s=>s.Screen.Perspective!.Led!).DistinctBy(l=>l.DeviceMac).ToArray();
                foreach(var removed in _devices.Keys.Where(mac=>!targets.Any(t=>t.DeviceMac.Equals(mac,StringComparison.OrdinalIgnoreCase)))) _devices.TryRemove(removed,out _);
                foreach(var target in targets)
                {
                    try
                    {
                        var address=_devices.TryGetValue(target.DeviceMac,out var previous) ? previous.Address : target.Address;
                        var device=await http.ReadAsync(address,token).ConfigureAwait(false);
                        if(!device.Mac.Equals(target.DeviceMac,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("WLED address belongs to a different device.");
                        await http.ValidateStreamingAsync(address,token).ConfigureAwait(false);
                        var changed=previous is null || previous.Address!=device.Address || !previous.Segments.SequenceEqual(device.Segments);
                        _devices[target.DeviceMac]=device;
                        if(changed || _failures.TryRemove(target.DeviceMac,out _))
                            Telemetry.Info("WledDeviceReady",new { device.Address,device.Name,device.Mac,device.PixelCount,Segments=device.Segments });
                    }
                    catch(Exception ex) when (!token.IsCancellationRequested)
                    {
                        _devices.TryRemove(target.DeviceMac,out _);
                        Warn(target.DeviceMac,"WledDeviceUnavailable",ex.Message);
                        // A DHCP change must not bind a perspective to the wrong hardware.
                        try
                        {
                            var found=(await WledDiscovery.FindAsync(token).ConfigureAwait(false)).FirstOrDefault(d=>d.Mac.Equals(target.DeviceMac,StringComparison.OrdinalIgnoreCase));
                            if(found is not null)
                            {
                                await http.ValidateStreamingAsync(found.Address,token).ConfigureAwait(false);
                                _devices[target.DeviceMac]=found; Telemetry.Info("WledDeviceRediscovered",new {found.Name,found.Mac,found.Address});
                            }
                        }
                        catch(Exception discoveryError) when (!token.IsCancellationRequested) { Warn(target.DeviceMac,"WledDiscoveryFailed",discoveryError.Message); }
                    }
                }
                await Task.Delay(targets.Length==0 ? 250 : 5000,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
    }
    private async Task OutputAsync()
    {
        var token=_shutdown.Token;
        var captures=new Dictionary<string,LedCapture>();
        var sampled=new Dictionary<string,(ScreenOptions Screen,Rectangle Bounds,byte[] Colors,long Next)>();
        var nextDevice=new Dictionary<string,long>(); var sequences=new Dictionary<string,int>();
        using var udp=new UdpClient(AddressFamily.InterNetwork);
        long reportAt=Stopwatch.GetTimestamp();
        try
        {
            while(!token.IsCancellationRequested)
            {
                long now=Stopwatch.GetTimestamp();
                var screens=Volatile.Read(ref _screens);
                var captured=new HashSet<string>();
                foreach(var group in screens.GroupBy(s=>s.Screen.Perspective!.Led!.DeviceMac,StringComparer.OrdinalIgnoreCase))
                {
                    if(!_devices.TryGetValue(group.Key,out var device)) continue;
                    int fps=group.Max(s=>s.Screen.Perspective!.Led!.FramesPerSecond);
                    if(nextDevice.GetValueOrDefault(group.Key)>now) continue;
                    long next=nextDevice.GetValueOrDefault(group.Key)+Stopwatch.Frequency/fps;
                    nextDevice[group.Key]=next>now ? next : now+Stopwatch.Frequency/fps;
                    byte[] rgb=new byte[device.PixelCount*3];
                    bool any=false;
                    foreach(var item in group)
                    {
                        var led=item.Screen.Perspective!.Led!;
                        string key=item.Display+"/"+item.Screen.Id;
                        var segment=device.Segments.FirstOrDefault(s=>s.Id==led.SegmentId);
                        if(segment is null || segment.PixelCount!=led.PixelCount || !item.Screen.IsLedArray && (!segment.Matrix || segment.Width!=led.Width || segment.Height!=led.Height))
                        { Warn(key,"LedSegmentMismatch","WLED segment missing or changed size. Refresh its perspective in Controller."); continue; }
                        try
                        {
                            if(!sampled.TryGetValue(key,out var cache) || cache.Next<=now || cache.Screen!=item.Screen || cache.Bounds!=item.Bounds)
                            {
                                if(!captures.TryGetValue(item.Display,out var capture)) captures[item.Display]=capture=new();
                                if(captured.Add(item.Display)) { capture.Capture(item.Bounds); Interlocked.Increment(ref _captureCount); }
                                var region=item.Screen.IsLedArray ? LedGeometry.Band(item.Screen.Points,led.SamplingThicknessPercent,item.Bounds.Width,item.Bounds.Height) : item.Screen.Points;
                                var colors=capture.Sample(region,item.Screen.IsLedArray ? led.PixelCount : led.Width,item.Screen.IsLedArray ? 1 : led.Height);
                                long nextSample=cache.Next+Stopwatch.Frequency/led.FramesPerSecond;
                                cache=(item.Screen,item.Bounds,colors,nextSample>now ? nextSample : now+Stopwatch.Frequency/led.FramesPerSecond); sampled[key]=cache;
                            }
                            int source=0;
                            foreach(int index in segment.PixelIndices(device.MatrixWidth))
                            {
                                if(index<0 || index>=device.PixelCount) throw new ArgumentException("Segment exceeds the device's pixel buffer.");
                                rgb[index*3]=cache.Colors[source++]; rgb[index*3+1]=cache.Colors[source++]; rgb[index*3+2]=cache.Colors[source++];
                            }
                            any=true; _failures.TryRemove(key,out _);
                        }
                        catch(Exception ex) { Warn(key,"LedSamplingFailed",ex.Message); }
                    }
                    if(!any) continue; // Let normal WLED behavior resume after its realtime timeout.
                    try
                    {
                        var host=WledClient.Endpoint(device.Address).Host;
                        var ip=IPAddress.TryParse(host,out var literal) && literal.AddressFamily==AddressFamily.InterNetwork ? literal :
                            (await Dns.GetHostAddressesAsync(host,token).ConfigureAwait(false)).First(a=>a.AddressFamily==AddressFamily.InterNetwork);
                        var endpoint=new IPEndPoint(ip,DdpFrames.Port);
                        int sequence=sequences.GetValueOrDefault(group.Key)%15+1;sequences[group.Key]=sequence;
                        foreach(var packet in DdpFrames.Packets(rgb,sequence)) await udp.SendAsync(packet,endpoint,token).ConfigureAwait(false);
                        Interlocked.Increment(ref _frames);
                    }
                    catch(Exception ex) when(!token.IsCancellationRequested) { Warn(group.Key+"/output","WledOutputFailed",ex.Message); }
                }
                foreach(var unused in captures.Keys.Where(d=>!screens.Any(s=>s.Display==d)).ToArray()) { captures[unused].Dispose(); captures.Remove(unused); }
                foreach(var unused in sampled.Keys.Where(k=>!screens.Any(s=>s.Display+"/"+s.Screen.Id==k)).ToArray()) sampled.Remove(unused);
                if(now-reportAt>=Stopwatch.Frequency*10 && screens.Length>0)
                {
                    Telemetry.Info("LedOutputPerformance",new { FramesSent,DisplayCaptures=Captures,Screens=screens.Select(s=>new {s.Display,s.Screen.Name,Perspective=s.Screen.Perspective!.Name,s.Screen.Perspective.Led!.SegmentId,s.Screen.Perspective.Led.FramesPerSecond}).ToArray() }); reportAt=now;
                }
                await Task.Delay(4,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex) { Telemetry.Error("LedOutputWorkerFailed",ex); }
        finally { foreach(var capture in captures.Values) capture.Dispose(); }
    }
    private void Warn(string key,string name,string reason)
    {
        if(_failures.TryGetValue(key,out var previous) && previous==reason) return;
        _failures[key]=reason; Telemetry.Warning(name,new { Target=key,Reason=reason });
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel(); await Task.WhenAll(_resolver,_output); _shutdown.Dispose();
        Telemetry.Info("LedOutputStopped",new {FramesSent,DisplayCaptures=Captures,ReturnToWledBehavior=true});
    }
}
