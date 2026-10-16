using System.Diagnostics;
using ExperienceX;
using Forms = System.Windows.Forms;
using PhotoBooth.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class SoftwareVideoHardwareChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = Path.Combine(Path.GetTempPath(),"ExperienceX-software-video-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var config = Path.Combine(evidence,"telemetry.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { Telemetry = new { Directory = evidence } }));
        Telemetry.Start("SoftwareVideoTests",config);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                if (ExperienceOptions.Parse("{}").SoftwareVideoDecoding || !ExperienceOptions.Parse("{\"SoftwareVideoDecoding\":true}").SoftwareVideoDecoding)
                    throw new Exception("Software decoding configuration defaults/parsing failed.");
                using var window = new Forms.Form { ShowInTaskbar = false, ClientSize = new(640,360) };
                var video = Environment.GetEnvironmentVariable("EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO") ?? Path.GetFullPath("experiencex/Media/Video.mp4");
                foreach (var software in new[] { false, true })
                {
                    var options = new ExperienceOptions { SoftwareVideoDecoding = software, TransitionInSeconds = .05,
                        DisplayScreens = new() { ["TEST"] = [new() { Id = "a", Name = "screen1" }] } };
                    VideoRenderer? target = null;
                    int visiblePixels = 0;
                    using var renderer = new VideoRenderer(window.Handle,640,360,options,"TEST",()=>{}, () =>
                    {
                        if (target is null || Volatile.Read(ref visiblePixels) != 0 || !target.Diagnostics.Screens.Any(s=>s.FramesTransferred>20)) return;
                        // Read back the actual production back buffer on its owner thread, before Present.
                        var graphics = (GraphicsResources)typeof(VideoRenderer).GetField("_graphics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
                        using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,640,360,1,1,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read));
                        using var buffer = graphics.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                        graphics.Context.CopyResource(staging,buffer);
                        var mapped = graphics.Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var row = new byte[640*4]; int count = 0;
                            for (int y=0;y<360;y++)
                            {
                                Marshal.Copy(mapped.DataPointer+y*(int)mapped.RowPitch,row,0,row.Length);
                                for (int x=0;x<640;x++) if(row[x*4+3]>0 && Math.Max(row[x*4],Math.Max(row[x*4+1],row[x*4+2]))>15) count++;
                            }
                            Volatile.Write(ref visiblePixels,count);
                        }
                        finally { graphics.Context.Unmap(staging,0); }
                    });
                    target = renderer;
                    renderer.Play(video,new() { ScreenId="a",Value=Path.GetFileName(video),RequestId="decode-"+software,Loop=true,Mute=true });
                    var clock = Stopwatch.StartNew();
                    ScreenPlaybackSnapshot[] snapshot;
                    do
                    {
                        snapshot = renderer.SnapshotAsync().GetAwaiter().GetResult();
                        if (renderer.State == RendererState.Failed || clock.Elapsed.TotalSeconds > 16)
                            throw new Exception($"Decoding software={software} failed: {renderer.Diagnostics}; {string.Join(';',snapshot.Select(s=>s.ToString()))}");
                        Thread.Sleep(25);
                    } while (snapshot.Length != 1 || snapshot[0].Frames < 30 || snapshot[0].Position < 1 || snapshot[0].Opacity < .9 || Volatile.Read(ref visiblePixels)==0);
                    if (renderer.Diagnostics.Screens.Single().SoftwareVideoDecoding != software) throw new Exception("Health did not identify the selected decoding path.");
                    Console.WriteLine($"PASS software={software}: {snapshot[0].Frames} decoded frames, position={snapshot[0].Position:F2}s, GPU readback confirms {visiblePixels} visible video pixels.");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { if (failure is null) done.TrySetResult(); else done.TrySetException(failure); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { Telemetry.Stop(); Console.WriteLine("Software decode test evidence: "+evidence); }
    }
}
