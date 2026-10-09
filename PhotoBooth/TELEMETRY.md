# Camera and Experience telemetry

Experience also records rendering tier/shader support at media opening and a PlaybackPerformance summary when playback stops or is replaced. Timing measures WPF render callbacks, not actual presented video frames or decoded-frame counts. A maximum of 200000 callback gaps is retained per experience to bound memory use.

Log filenames start with the PC hostname, for example `MY-PC-2026-10-07-1234-ab12cd34-0000.jsonl`. Every JSON entry also includes MachineName. Retention is applied separately to this PC's prefixed files, so another PC's synced logs and older unprefixed logs are preserved.

Both apps write newline-delimited JSON locally. No Event Log, registry setup, external service, or additional package is required. The common implementation is in Shared/Telemetry.cs and is compiled into both applications.

By default logs are under `logs/Camera` or `logs/Experience` relative to each executable, independent of the launch working directory. Set Directory to an absolute path to put both apps under one logs folder. That location must be writable. Files roll on UTC date changes and at 10 MiB; the newest 20 files per app are retained. Every run gets a new session ID and file, so separate app instances do not write to the same file.

```json
"Telemetry": {
  "MinimumLevel": "Information",
  "Directory": "logs",
  "MaxFileBytes": 10485760,
  "RetainedFiles": 20,
  "HealthIntervalSeconds": 60
}
```

Restart after changing telemetry settings. Levels are Debug, Information, Warning, and Error. Information includes lifecycle, configuration, networking, playback, camera state, and periodic health. Debug adds detailed Camera transitions and animation diagnostics. Errors include stack traces when available. No frames or media content are recorded.

Each entry has a UTC timestamp, level, app name, session ID, process ID, event, optional request ID, structured data, and optional exception. Camera's reusable SendExperienceAsync method accepts an experience, optional value, and source label for any trigger. It generates a requestId and includes source in the UDP packet; Experience preserves the ID through receipt, media opening, fades, and stop events, and records the source on receipt. Trigger 3 is currently one caller. Old senders without an ID or source continue to work and receive an ID at the receiver. Sending UDP successfully does not establish that the receiver got it; compare UdpRequestSent and UdpRequestReceived with the same RequestId.

Writes are queued in memory and flushed after batches. A full queue drops new entries rather than blocking playback; dropped counts appear in health/shutdown events. Shutdown drains the queue for up to three seconds. A hard kill or power loss can lose buffered entries. Logging failures do not crash the application; diagnostic fallback goes to the .NET trace output. If files are absent, check directory permissions. Health defaults to once per minute and includes memory plus application state; logging does not run on every frame.

Experience's old experience.log is no longer updated. Existing files are preserved. Camera's existing diagnostics now also run in Release builds. Fatal exceptions are logged without suppressing the original exception or claiming recovery.
