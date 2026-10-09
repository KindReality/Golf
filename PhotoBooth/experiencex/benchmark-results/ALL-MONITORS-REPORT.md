# Simultaneous three-monitor playback — ALPHA, 2026-10-08

Functional checks passed; smooth physical presentation is not yet verified. Each case played three independent muted instances of ping.mp4 (1920×1080, 30 fps), one per connected monitor, for approximately 32 seconds. Requests were sent together; all three playback intervals covered the entire 20-second performance capture after a seven-second warm-up. The running ExperienceX binary was not changed or rebuilt. Camera and original Experience remained running.

| Case | CPU % of PC | Working set MiB | GPU 3D % | GPU decode % | Decoder drops DISPLAY1 / DISPLAY2 / DISPLAY3 |
|---|---:|---:|---:|---:|---|
| Plain | 2.56 | 247.9 | 1.03 | 12.29 | 2 / 2 / 2 |
| BlackKey | 2.98 | 241.9 | 0.77 | 11.23 | 0 / 1 / 1 |
| BlackKeyAndKeystone | 2.88 | 238.7 | 1.11 | 12.46 | 1 / 1 / 1 |

Drops count the full playback, including startup. With black key plus keystone, all three dropped-frame counters were already one before capture and remained one through completion. Plain video had warm-up counts 2 / 1 / 2 and final counts 2 / 2 / 2. Black key had warm-up counts 0 / 0 / 0 and final counts 0 / 1 / 1. Counters are per independent video, not per desktop present.

All requests confirmed an advancing media position at zero surface opacity and completed at zero opacity. Offline checks also verified continued advancement during fade-out and a transparent-frame event before playback stopped. No request-scoped application errors were logged. Three nonzero swap-chain addresses were captured per case. Swap-chain addresses cannot be reliably mapped to monitor names from the current telemetry, so no mapping is guessed.

| Case | Capture events with unknown swap chain | P95 displayed gap range ms across known chains | Largest reported display gap ms |
|---|---:|---:|---:|
| Plain | 52 | 66.7–3517.1 | 3517.1 |
| BlackKey | 49 | 17.5–7683.3 | 7683.3 |
| BlackKeyAndKeystone | 139 | 83.9–199.8 | 450.0 |

PresentMon reported many undisplayed presents and long display gaps; the multi-monitor presentation check cannot be marked smooth. Some events had address 0x0 and are retained separately in the raw CSV/JSON rather than treated as a fourth window. PresentMon has a documented [multi-monitor display-FPS/latency tracking issue](https://github.com/GameTechDev/PresentMon/issues/108). That issue is a possible measurement limitation, not proof that these observed gaps are false. The current data cannot distinguish compositor/display stalls from trace-attribution problems. Decoder counters and approximately 60-per-second present submissions alone do not prove physical display smoothness.

The first capture aborted its summary because unattributed 0x0 events were counted as a fourth swap chain. The test harness was corrected to keep those events separate, then all three cases were rerun. Its initial CSV remains available as all-Plain-3284692ef6034d37994fc96da2946371-presents.csv; the table above uses only the completed rerun.

Raw results: [all-monitors-20261008-094150.json](all-monitors-20261008-094150.json). Per-case CSV paths and per-monitor counters are in that JSON. Run `./experiencex/Measure-AllMonitors.ps1 -SampleSeconds 20` to repeat. Runtime settings were restored to their original values afterward, including the ten-second maximum, five-second fade-in and original black-key/keystone settings.
