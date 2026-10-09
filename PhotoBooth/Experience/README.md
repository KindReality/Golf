# Experience

Run `dotnet run --project Experience/Experience.csproj` from the repository root, or launch the built `Experience.exe`. Requires the .NET 9 Windows Desktop runtime unless published self-contained. Only one instance runs per desktop session; a second launch displays a message and exits. Closing any Experience window shuts down the application and its playback outputs.

Experience creates a separate, initially transparent window for every connected monitor. Production windows cover their monitor, including the taskbar, and remain topmost during playback. Monitor availability is checked every second and on display changes. A disconnected monitor's window is immediately hidden and its video canceled; reconnecting restores a transparent idle window. Newly connected monitors receive their own window. Other monitors and independent audio continue playing.

## UDP commands

Send one UTF-8 JSON object per datagram to IPv4 UDP port **21324** (configurable). Commands are anonymous, without authentication or acknowledgements. Delivery is best effort. Remote senders need inbound UDP firewall access. This is custom JSON over UDP, not WLED's binary realtime protocol.

```json
{"experience":"video","value":"ping.mp4","monitorName":null,"mute":false}
```

```json
{"experience":"video","value":"ping.mp4","monitorName":"\\\\.\\DISPLAY2","mute":true}
```

```json
{"experience":"audio","value":"sound.mp3","audioDevice":null,"channel":1,"mute":false}
```

`monitorName` omitted/null/empty uses `DefaultMonitorName`; an empty default selects the primary monitor. `audioDevice` omitted/null/empty uses `DefaultAudioDevice`; an empty default selects the system multimedia audio output. Audio devices accept an exact friendly name (case insensitive) or endpoint ID. Duplicate friendly names are rejected; use their IDs. A missing requested/configured output is logged and rejected without falling back. Requests are not queued for reconnection.

### User-managed monitor aliases

Edit `MonitorAliases` in `appsettings.json` beside the executable. For example (assign the Windows names to match your actual layout):

```json
"MonitorAliases": {
  "Left": "\\\\.\\DISPLAY2",
  "Center": "\\\\.\\DISPLAY1",
  "Projector": "\\\\.\\DISPLAY3"
},
"DefaultMonitorName": "Center"
```

Then send `{"experience":"video","value":"ping.mp4","monitorName":"Projector"}`. Alias names ignore case and surrounding whitespace. Raw Windows monitor names still work. Changes reload live within the normal configuration polling interval; they apply to new requests and do not move existing playback. Unknown aliases and disconnected targets are logged and rejected without fallback. Aliases must have unique nonempty names and nonempty targets; aliases reference Windows monitor names directly, not other aliases. The default configuration leaves the map empty until you assign it. Windows display numbering can change after hardware/connection changes; update the map if that happens. This map does not rename monitors in Windows or bind to hardware serial numbers.

Channel numbers start at **1**, in the selected device's configured Windows channel order (normally 1=left, 2=right). A specified channel receives the source channels averaged to mono; other output channels are silent. Omitted/null `channel` retains the source channel ordering; mono is duplicated to front left/right and stereo occupies the first two channels. A source with more channels than the device is rejected unless an explicit channel is supplied. Configure multichannel devices with the desired channel count in Windows. Audio is resampled to the endpoint rate when necessary.

Video playback is independent per monitor. Audio playback is independent per **resolved endpoint ID and channel**. New valid requests replace playback only in the same slot, after the replacement audio is prepared. Omitted-channel audio has its own slot and can mix with numbered-channel audio. Video's embedded sound remains enabled unless `mute` is true; it uses the normal video audio output. Standalone audio runs independently of all video windows. `mute` defaults to false for both experience types and never changes system volume or other playbacks.

There is currently **no stop command**. Playback finishes at the file's end or its configured maximum, whichever comes first. Optional `requestId` and `source` are carried into telemetry. Malformed requests, unsupported experience names, unavailable files, and invalid channels are rejected without replacing valid playback. Files must resolve inside the configured media directory; URL and rooted-path requests are rejected.

```powershell
./Experience/Send-Experience.ps1 -Value ping.mp4
./Experience/Send-Experience.ps1 -Value ping.mp4 -MonitorName '\\.\DISPLAY2' -Mute
./Experience/Send-Experience.ps1 -Experience audio -Value sound.mp3 -AudioDevice 'Speakers (USB Audio Device)' -Channel 2
```

## Configuration

Edit `appsettings.json` beside the running executable. Builds copy source configuration and media into the output directory and configured OneDrive destination. Changes are checked once per second using background file I/O; content is also verified every ten seconds for timestamp/length-preserving edits. Invalid edits retain the previous valid settings. UDP port changes bind the replacement listener before releasing the old one. `DeveloperMode` and telemetry settings require restart.

| Setting | Default | Purpose |
|---|---|---|
| DefaultMonitorName | empty | Monitor used by commands without a monitor; empty means primary |
| MonitorAliases | {} | User-managed alias-to-Windows-monitor map; aliases work in commands and DefaultMonitorName |
| DefaultAudioDevice | empty | Audio endpoint name or ID used by commands without a device; empty means system multimedia default |
| MaxVideoPlaybackLength | 10 | Maximum video duration, seconds |
| MaxAudioPlaybackLength | 10 | Maximum standalone audio duration, seconds |
| TransitionInSeconds | 5 | Video opacity fade-in duration |
| TransitionOutSeconds | 1 | Video opacity fade-out duration |
| AudioTransitionInSeconds | 5 | Standalone audio volume fade-in duration |
| AudioTransitionOutSeconds | 1 | Standalone audio volume fade-out duration |
| VideoDirectory | Media | Local video directory, relative to the executable or absolute |
| AudioDirectory | Media | Local audio directory, relative to the executable or absolute |
| UdpPort | 21324 | IPv4 UDP listen port |
| DeveloperMode | false | true: separate 800×600 ordinary windows centered on each monitor, without desktop transparency or topmost enforcement |

Lengths must be greater than zero and at most 86400 seconds. Fade durations range from zero (immediate) to 3600 seconds. Each video starts with shader alpha exactly zero; fade-in begins only after playback position advances. Video keeps advancing throughout fade-out. At completion the shader emits zero alpha and the live player is retained through two distinct WPF rendering callbacks before being closed. This allows the transparent scene to be submitted; callbacks are not proof of physical display presentation. Fade-out starts early enough to complete before the natural end or maximum duration; short experiences shorten the fade. Audio fades and its duration limit are applied per sample, with normal device buffering latency. Live duration changes affect active playback. Video and audio settings are independent. Legacy `MonitorName` and `maxPlaybackLength` are read as `DefaultMonitorName` and `MaxVideoPlaybackLength` when the new keys are absent.

Available monitor names, audio device names/IDs/channel counts, selections, request IDs, playback replacement, completion and failures are logged in machine-prefixed JSONL files under `logs/Experience`. No Windows Event Log is used. PlaybackPerformance reports WPF callback timing and process allocation/GC counts; it cannot count presented or decoded video frames.

## Transparency and keystone

`BlackKey.Enabled` defaults to true. The final video surface, including letterboxing and keystone backing, is keyed by a pixel shader. `Threshold` (default 0.015) removes colors whose brightest RGB channel is at or below that value; `Softness` (default 0.03) softens the edge. Values are normalized 0–1 and must total at most 1. Lower values retain more dark detail. Production mode is required for desktop transparency. The same shader multiplies premultiplied color and alpha for fades while native window opacity stays at 1, keeping the media render path active even at zero visible alpha. Disabling black key bypasses color removal while retaining shader fades. The compiled `Shaders/BlackKey.ps` is embedded; normal builds need no shader compiler. Recompile HLSL with the Windows SDK fxc tool targeting `ps_2_0`, entry point `main`.

Set `Keystone.Enabled` to true and adjust `TopLeft`, `TopRight`, `BottomRight`, `BottomLeft`, each with X/Y fractions of the monitor. Corners must stay inside 0–1 and form a convex clockwise quadrilateral. For example, top-left X=0.15 and top-right X=0.85 move the top corners inward. A live VisualBrush on a 64×64 projective mesh provides correction. Black-key and keystone settings currently apply equally to every window. Unchanged meshes and shaders are retained across unrelated settings updates.

Topmost enforcement raises without taking focus when playback starts/media opens, reacts to window-order changes, and checks once per second. It ignores cloaked windows and coalesces events; repeat successes are summarized in logs. This is best effort among desktop apps and does not cover secure desktops.

## Verification

Run `dotnet run --project Experience.Tests` for routing, protocol, sample isolation, fades, duration and configuration regression checks. With Experience stopped, `Experience/Verify-Outputs.ps1` exercises all currently connected monitors and active audio devices with silent audio and muted video, then restores configuration and leaves both apps running. It verifies endpoint initialization, not audible output quality or physical cable routing. Hardware with more than two channels still requires physical commissioning.

`Measure-Playback.ps1` samples CPU/GPU and callback gaps, optionally with a portable PresentMon executable. See `benchmark-results/OPTIMIZATION-REPORT.md` for the earlier WPF measurements and their limitations. Audio uses [NAudio](https://github.com/naudio/NAudio/tree/v2.2.1) and WASAPI shared-mode output; video retains WPF MediaElement and Windows-installed codecs.
