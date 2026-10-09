# DirectX prototype measurements — ALPHA, 2026-10-08

ExperienceX retains C# control/audio code and uses Media Foundation, Direct3D 11 and DirectComposition for video. The corrected frame-latency wait reduced CPU usage in this test. These are short sequential runs on one PC, not a guarantee of performance on other hardware.

RTX 4070 Ti, 32 logical processors, three connected 1920×1080 monitors; one monitor playing muted ping.mp4 (1080p, 30 fps). Debug builds, 6.5-second warm-up and approximately 10-second CPU/GPU/PresentMon sampling per case. Other desktop applications remained running. GPU engine utilization counters measure different engines and must not be added as a total GPU score.

| Effects | WPF CPU % | DirectX CPU % | WPF 3D % | DirectX 3D % | WPF decode % | DirectX decode % | WPF RAM MiB | DirectX RAM MiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Plain | 3.14 | 1.72 | 11.32 | 1.12 | 3.95 | 8.28 | 212.1 | 217.3 |
| BlackKey | 3.13 | 2.02 | 11.18 | 0.85 | 3.96 | 9.06 | 257.1 | 221.3 |
| BlackKeyAndKeystone | 3.21 | 1.87 | 13.37 | 1.05 | 3.94 | 11.94 | 273.1 | 225.1 |

Black key plus keystone: CPU was about 42% lower, 3D-engine utilization about 92% lower, and working set about 18% lower. Video-decode engine utilization increased. Plain-video working set was slightly higher. This supports retaining C# for this prototype; it does not identify a C++ rewrite as a needed optimization.

| DirectX case | Present events | P95 displayed gap ms | Max displayed gap ms | Displayed gaps >50 ms | Undisplayed presents | Media Engine dropped video frames |
|---|---:|---:|---:|---:|---:|---:|
| Plain | 594 | 16.93 | 33.18 | 0 | 0 | 0 |
| BlackKey | 594 | 16.93 | 33.40 | 0 | 0 | 0 |
| BlackKeyAndKeystone | 595 | 16.97 | 34.01 | 0 | 4 | 0 |

PresentMon captured Composed: Flip display events for DirectX. Four presents in the final keystone sample were not displayed; these cannot be equated with four lost video frames because the display presents at about 60 Hz while the video advances at 30 fps. Media Engine reported zero dropped video frames across the final single-monitor playbacks. Present submission gaps still reached 40–60 ms outside or within the full playback envelope; the table describes only the warmed-up capture interval.

The original WPF app produced no PresentMon CSV. Its maximum rendering-callback gaps were 101–119 ms, but WPF callbacks are not display or decoded-frame measurements, so they cannot be compared directly to the DirectX display-gap table.

Live checks passed concurrent video on all three monitors, replacement, unavailable defaults/outputs, aliases, live configuration, transparent-start playback and continued playback through fade-out. Independent silent audio passed on seven active stereo endpoints and numbered channels. Multi-monitor startup/replacement runs recorded some decoder drops; long-duration and sustained simultaneous-monitor testing is still needed. Pure output-selection and mutex semantics have regression coverage; physical cable disconnect, the duplicate-instance dialog, and GPU-device-loss recovery were not commissioned.

GPU readback verified actual compiled shader output for black-key transparency, premultiplied fades, zero-alpha clear and keystone clipping. Inverse homography, protocol/configuration, routing and sample-accurate audio tests passed. Native visibility was logged for each full-screen surface. Desktop screenshot capture was unavailable because its approval timed out; no visual-inspection claim is made.

Video deadlines have frame/compositor scheduling granularity; audio limits are applied per sample. GPU failure hides the affected surface and requires restart. Builds copy to OneDrive/Home Technologies/Bin/ExperienceX. Original Camera/Experience remain on UDP 21324; ExperienceX defaults to 21325 with a distinct executable, mutex and log directory.

Raw baseline: [samples-20261008-090238.json](samples-20261008-090238.json). Final DirectX: [samples-20261008-091027.json](samples-20261008-091027.json). The JSON records link the corresponding PresentMon CSVs. Earlier samples-20261008-090352.json records the initial, superseded frame-wait implementation.

Native API references: [DXGI frame-latency synchronization](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject), [Media Engine frame statistics](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/ne-mfmediaengine-mf_media_engine_statistic), [GPU video frame transfer](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-transfervideoframe).


Follow-up: [simultaneous three-monitor results](ALL-MONITORS-REPORT.md). Multi-monitor functional playback passed, but display presentation validation remains inconclusive.
