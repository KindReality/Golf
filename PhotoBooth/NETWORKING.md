# Offline UDP broadcast protocol

Camera, ExperienceX, and Controller share `Shared/Networking`. All peers use IPv4 UDP **54242 for events** and **54243 for commands** by default, inspired by Hitchhiker's Guide's 42. Send one UTF-8 JSON object per broadcast datagram, up to 1200 bytes. Control messages use no TCP, broker, authentication or playback acknowledgements. Controller discovery has a UDP event response; media transfer uses Windows SMB. Receipt is best effort; a successful send is not proof of execution. The original Experience application still uses its previous protocol and port 21324.

## Identity and routing

`DeviceName` is a user-managed machine alias. Missing, null or whitespace uses the hostname without writing a machine-specific name into distributed defaults. Matching ignores case and surrounding whitespace. Assign unique names to separate computers. Display and audio aliases select outputs inside that computer and are independent of device names. Camera and ExperienceX can share a computer's identity and UDP ports; both receive broadcast messages, and only ExperienceX executes audio/video commands. Camera observes/logs events without adding coordinator rules.

Controller remains an ordinary peer. It subscribes to events and creates explicit storyboard commands/events without becoming a required transport hub. Other peers do not automatically forward UDP messages. Controller also bridges events from its configured USB Arduinos to UDP, preserving each original event identity.

Every message requires `device`, `source`, and `requestId` (nonempty strings, maximum 128 characters). `device:"*"` explicitly targets everyone; a missing target is rejected. `source` identifies the originating peer and cannot be `*`. Use a new globally unique request ID for each intended action. Retries preserve the same ID and original body. `type` is `command` or `event`; omitted type means command. Events also require `event`. Unknown action/data fields are preserved by the envelope for the application to interpret.

```json
{"type":"command","device":"projection-pc","source":"coordinator","requestId":"b2a59231c156488ca8dde3ffbbd06008","experience":"video","value":"ping.mp4","screen":"Projector/screen1","mute":true}
```

```json
{"type":"event","device":"*","source":"entrance-sensor","requestId":"844265ff05af4a838c54633be56420b59","input":"front-door","event":"proximity.enter","data":{"distanceCm":30}}
```

An event is observed/logged, never interpreted as a playback command just because media fields are present. Coordinator event-to-action rules are a separate application concern. Camera's trigger 3/4/5 continue to request ping.mp4 on Display One/Projector/Display Two respectively.

Controller adds optional execution metadata to its playlist commands and events:

```json
"context": { "scene": "idle", "playlist": "media-loop", "item": "Eye", "isAmbient": true }
```

The playlist/item identify the step that actually sent the command, including nested playlists. Scene activation, deactivation, ambient and manual runs retain their owning scene. `isAmbient` is true for ambient items and their nested commands; regular commands use false. Missing context or a missing flag defaults to normal priority. On Experience, normal video interrupts ambient video throughout the device; ambient video is skipped while normal video is active, including pending starts and fade-outs. Audio is unaffected by this rule. Context never changes routing. Names, when provided, must contain 1–128 characters without control characters. Retries retain the same body and request ID. Identical looping video requests with new IDs continue existing playback on each matching screen; the latest ID owns its subsequent stop.

Video/audio playback also accepts a comma-separated `roles` target instead of direct `screen`, `displayName`, `audioDevice` or `audioDevices` selectors. `device: "*"` broadcasts role playback to every Experience computer; a named device restricts it to that recipient. Each Experience locally selects outputs of the appropriate media type with any matching role, ignoring case and duplicate names. There is one playback per matching output, with no default fallback for unmatched roles. Discovery inventories include configured screen and speaker roles. Role requests retain ordinary deduplication, local-media requirements, video priority, looping and audio mixing behavior; they do not add acknowledgments.

Controller and ExperienceX JSON logs include a root `Context` object on entries with a known request ID: source/target, experience, media filename (`Value`), scene, playlist, item and correlation ID. ExperienceX adds the resolved local `Path` once media is found, so playback confirmation, fade, completion and failure entries remain traceable without looking up a separate receive entry. Telemetry retains context for the most recent 4096 requests with bounded memory. The root `RequestId` identifies the same command in both applications.

## Reliability and boundaries

Camera and ExperienceX senders repeat a packet three times, 75 ms apart by default; Controller defaults to one best-effort attempt. Receivers suppress matching logical source + request ID for `RequestDeduplicationSeconds` (60 by default), regardless of sender IP or ephemeral port. Source names are compared without case. The bounded cache holds 4096 entries and resets on restart, so suppression is not an exactly-once guarantee. Unavailable media/outputs are not queued; a deliberate new attempt needs a new ID. Ignored targets log at Debug; duplicate suppression logs at Information; accepted messages, invalid input, expired requests and send failures have separate telemetry.

`processingTimeoutSeconds` defaults to ten seconds and bounds the delay from local receipt to dispatch using a monotonic clock. It prevents commands sitting in a busy application's dispatcher queue from executing much later, without requiring synchronized clocks on the offline LAN. It cannot measure how long a packet spent before reaching the application. Send attempts are also time limited. An optional `expiresAt` ISO 8601 timestamp with an offset provides an absolute deadline for clients whose clocks are aligned; our default senders omit it. `hopCount` defaults to zero and must be 0–8. Controller preserves event ancestry and increments this count when creating derived storyboard messages, rather than automatically reflecting UDP packets.

The size cap avoids large control packets; use virtual audio group aliases when many endpoint names would exceed it. Sensors should debounce input and announce state changes instead of continuously streaming readings. Broadcast recipients must be on the same subnet with inbound UDP allowed and Wi-Fi client isolation disabled if applicable.

## Configuration

```json
{
  "DeviceName": "projection-pc",
  "UdpEventsPort": 54242,
  "UdpCommandsPort": 54243,
  "UdpBroadcastAddress": "255.255.255.255",
  "UdpInterfaceAddress": "",
  "UdpSendAttempts": 3,
  "UdpRetryDelayMilliseconds": 75,
  "RequestLifetimeSeconds": 10,
  "RequestDeduplicationSeconds": 60
}
```

The default broadcast address and empty interface broadcast on every active IPv4 subnet. On PCs with Ethernet and Wi-Fi, optionally set `UdpInterfaceAddress` to the dedicated LAN's local IPv4 address and `UdpBroadcastAddress` to that subnet's broadcast address (for example 192.168.1.255 with a 255.255.255.0 mask). All peers use the same two destination ports: UDP 54242 for events and UDP 54243 for commands. Receivers reject messages sent to the wrong port. Multiple cooperating local listeners use socket address reuse for broadcast delivery; do not depend on unicast delivery reaching every process sharing the port.

ExperienceX adds missing settings to the existing per-user configuration and applies alias/port changes live. Camera reloads its networking settings from its appsettings.json every second and before sending, retaining the last valid configuration on an invalid edit. Camera's `ExperienceDeviceName` selects the destination alias; blank targets the local hostname. Set it to the remote ExperienceX device's effective name for remote playback, or explicitly `*` for every receiver. `ExperienceIpAddress` is no longer used; the former shared `UdpPort`/`ExperienceUdpPort` settings are replaced by `UdpEventsPort` and `UdpCommandsPort`. Existing ExperienceX configuration migrates automatically.

Retries are bounded to 1–5 attempts, delays to 20–1000 ms and lifetimes to 300 seconds. Duplicate retention must cover the request lifetime and be at most 3600 seconds. Avoid duplicate aliases: Controller discovery does not resolve device-name collisions.

Video commands address a region with `screen:"displayAlias/screenName"`, for example `Projector/screen1`. Screen names are unique within each display and case insensitive. Omitting `screen` uses the first video screen on the default display; an optional `displayName` selects another display's first video screen. `screen:"*"` selects all video screens on all connected displays, `Projector/*` selects that display's videos, and `*/screen1` selects videos with that name across displays. Mats are excluded. A missing display/screen is rejected without fallback or creating hardware/configuration. Replacing video affects only the targeted screens.

## Looping video

Video commands optionally set `loop:true`; ExperienceX repeats locally, fading in once. Controller tracks scene-owned loops and sends `experience:"video", command:"stop", playbackRequestId:"original-play-id"` with the original source identity and a fresh request ID at scene exit. Stops use the receiver's configured video fade-out, match source + original playback ID, and never stop a newer replacement or audio. A bounded stop history suppresses a play arriving after its stop within the deduplication window. These commands retain ordinary best-effort UDP semantics; no playback acknowledgements are introduced.

## Controller discovery and media

ExperienceX broadcasts a normal command envelope at startup:

```json
{"type":"command","device":"*","source":"projection-pc","requestId":"discovery-id","command":"discover","service":"controller"}
```

Each Controller broadcasts an event addressed to the requester, with a fresh response ID:

```json
{"type":"event","device":"projection-pc","source":"coordinator","requestId":"response-id","event":"controller.discovered","input":"controller","hostname":"ALPHA","discoveryRequestId":"discovery-id"}
```

The hostname is the actual Windows hostname, independent of `source` aliases. Clients accept only responses correlated with their discovery requests, then access `\\ALPHA\media` using ordinary Windows file-sharing credentials. Discovery does not acknowledge playback or change its best-effort semantics. Root files are cached locally in the background; subdirectories are excluded. Controller's installer provisions the read-only share and Private/local-subnet firewall allowances.

## Command-line examples

```powershell
./experiencex/Send-Experience.ps1 -Device projection-pc -Value ping.mp4 -Screen Projector/screen1 -Mute
./experiencex/Send-Experience.ps1 -Device projection-pc -Experience audio -Value sound.mp3 -AudioDevice Room -Volume 60
./experiencex/Send-Event.ps1 -Source entrance-sensor -Input front-door -Event proximity.enter -Data @{distanceCm=30}
```

Existing clients must add device/source/requestId to their old playback bodies. There is deliberately no fallback that treats an untargeted legacy message as a command to every device.

When `UdpBroadcastAddress` is `255.255.255.255` and `UdpInterfaceAddress` is empty, senders bind each active IPv4 adapter and send to its subnet broadcast address. This includes LANs without a gateway and avoids a virtual adapter capturing all requests. Every adapter/retry uses the same request ID, so receivers execute once. Set `UdpInterfaceAddress` to restrict the sender to one local adapter; an explicit subnet broadcast destination is retained. Logs include the actual source interface and destination. A failure on one adapter is logged and does not prevent sending through the others. If every send fails, the request reports an error.

Run `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj -- --network` for focused broadcast and receive checks. These validate adapter selection with virtual and physical subnets, explicit overrides, real local UDP reception, and duplicate suppression. Local reception does not prove another PC permits inbound traffic; the receiving app needs an inbound UDP firewall allowance on the active network profile.

## Controller event chains

Controller event triggers match `source` (device alias), `input` (named sensor or switch), and `event`. `input` is required by Controller. Derived playlist commands/events use new request IDs and retain `correlationId`, `originSource`, `causationId`, `actionHistory` (device/action identities), and incremented `hopCount`. An action is suppressed if its identity already occurs in the ancestry or the chain reaches eight hops; triggers also suppress causal branches that fired previously. This bounds scene/playlist feedback loops across controllers. The size cap includes ancestry. No acknowledgements are added.

Controller accepts `command:"scene", value:"scene-name"` and `command:"playlist", value:"storyboard-playlist-name"` in ordinary command envelopes. Scene changes stop the previous scene's unsent work and run deactivation, activation, then ambient scheduling. A newer transition request takes priority; repeated requests for the active scene or current destination are ignored. Commands sent to the source Controller or `*` also apply locally if networking fails, using the same request ID to suppress UDP echoes. Local nested storyboard references are distinct from network playlist commands: only local nesting can await send completion. See [Controller](Controller/README.md) for the scene/playlist model, timing, serial framing, environments, and configuration.
