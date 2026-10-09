# Playback benchmark — ALPHA, October 7, 2026

GPU: NVIDIA GeForce RTX 4070 Ti (the system also reports a Meta Virtual Monitor). CPU: 32 logical processors. Production fullscreen playback of ping.mp4, 1920×1080, 30 fps. Camera was stopped to isolate Experience. Each case used roughly 10 seconds of steady-state CPU/GPU sampling after a 6.5-second warmup, including the normal five-second fade-in. Keystone used the existing 64×64 projective mesh with the two top corners inset by 10%.

| Mode | CPU, percent of entire machine | GPU 3D engine usage | GPU video decode | Working set |
| --- | ---: | ---: | ---: | ---: |
| Plain playback | 1.46% | 22.56% | 5.99% | 182 MiB |
| Black-key shader | 1.17% | 23.37% | 6.73% | 201 MiB |
| Black-key shader + keystone | 1.27% | 35.70% | 7.42% | 220 MiB |

WPF reported rendering tier 2 and pixel-shader 2.0 support. The shader adds little GPU 3D load in this short comparison; the keystone path adds roughly 12 percentage points above shader-only playback. CPU differences are small and should be treated as run-to-run variation. The sample sizes do not establish sustained thermal behavior or performance on less powerful GPUs.

WPF render-callback gap P95 was about 33.34 ms for every mode. The largest gaps were 67.24 ms for plain playback and 116.67 ms for both shader modes, with 7, 4, and 2 gaps above 50 ms respectively. These counters cover the full playback measurement interval (about 16–17 seconds), including warmup and fade-in, unlike steady-state CPU sampling. They are composition callbacks, not decoded or presented video frames: actual dropped-frame counts and visual smoothness are not established by this test. No playback errors were logged, and video position advanced at approximately wall-clock speed.

On this PC, the measured resource usage does not justify replacing the renderer solely for performance. Occasional long callback gaps warrant a presentation-level profiler or visual review if stutter is observed. Results should be repeated on the actual projection PC with its display connection and concurrent applications.

Original settings were restored: maxPlaybackLength=10, BlackKey enabled, Keystone disabled. Camera and Experience are running again.

Raw measurements: samples-20261007-210909.json. Re-run with `./Experience/Measure-Playback.ps1` from a PowerShell terminal while Camera is closed. The script temporarily changes playback configuration and restores it in finally; it requires a built Experience app, ping.mp4, and access to GPU performance counters. The added PlaybackPerformance telemetry records bounded WPF render-callback timing for future playback diagnostics.
