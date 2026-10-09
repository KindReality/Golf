# WPF playback optimization — October 7, 2026

Both Camera and Experience were running on ALPHA (RTX 4070 Ti, 32 logical processors). Each case warmed up through the five-second fade, then sampled CPU/GPU for approximately 15 seconds. Callback measurements include opening-to-stop playback, approximately 22 seconds. These are single short runs, not a controlled statistical study.

| Case | Callback p95 ms, before → after | Maximum ms, before → after | Gaps >50 ms, before → after | CPU % of machine, before → after |
|---|---:|---:|---:|---:|
| Plain | 33.34 → 33.34 | 100.00 → 84.92 | 24 → 5 | 1.46 → 1.43 |
| Black key | 33.90 → 33.34 | 83.94 → 133.33 | 19 → 15 | 1.67 → 1.41 |
| Black key + keystone | 50.00 → 33.34 | 202.82 → 216.67 | 29 → 18 | 1.53 → 1.56 |

The changes remove avoidable UI-thread work. Counts improved in these runs, but maximum gaps worsened in two cases; this does not establish stutter-free playback or a causal performance improvement. There were no managed garbage collections during any after run; process-wide managed allocation during playback was approximately 1.6–2.1 MB. No playback errors were recorded.

## Changes

- Read configuration metadata and content off the dispatcher, avoid repeated unchanged-file reads, and parse/validate updates on a worker. Check metadata every second and verify content every ten seconds to catch timestamp-preserving edits.
- Keep the existing shader and keystone mesh when their settings and aspect ratio have not changed. Remove per-cell temporary triangle arrays during mesh construction.
- Coalesce window-order callbacks to at most ten per second, skip DWM-cloaked windows, and summarize repeated successful raises at most every five seconds. The one-second watchdog and forced playback-start raises remain.
- Preallocate callback samples and move sorting/summary computation off the playback dispatcher. Include allocation and GC counts and explicitly label omitted samples.

## Actual presentation measurement

The official portable PresentMon 2.6.0 console tool was tried against Experience's PID with display tracking enabled. Each capture started and stopped successfully but generated no present-event CSV. The tool also warned that this token lacked elevated privileges for querying some processes. This capture therefore provides **no actual displayed-frame or dropped-video-frame count**. WPF callback timestamps and MediaElement.Position cannot supply that count. Do not interpret callback samples as decoded or presented video frames.

A next investigation would use an elevated compositor/media ETW trace on the target PC, correlate it with the request IDs and playback interval, and establish whether the stalls occur in decoding, WPF, or desktop composition before selecting a different renderer. No rendering-engine rewrite was made in this pass.

Tool documentation: https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md

## Reproduction

Stop both apps before building. Run `Measure-Playback.ps1 -SampleSeconds 15 -Label comparison -PresentMonPath <portable-exe-path>` from PowerShell. The script restores runtime settings in its finally block. Present-event counts/status are included when available. The profiler is optional and is not part of the deployed application.

Raw runs: `samples-20261007-211921.json` (before) and `samples-20261007-212119.json` (after), with profiler stdout/stderr beside them. The final build additionally moves configuration parsing/validation to a worker and logs actual mesh rebuilds; these small additions were verified in the live smoke check rather than another performance comparison.
