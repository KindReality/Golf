# Offline UDP broadcast protocol

Camera and ExperienceX share `Shared/Networking`. All peers listen on IPv4 UDP port 21325 by default. Send one UTF-8 JSON object per broadcast datagram, up to 1200 bytes. There is no TCP, broker, authentication or UDP response. Receipt is best effort; a successful send is not proof of execution. The original Experience application still uses its previous protocol and port 21324.

## Identity and routing

`DeviceName` is a user-managed machine alias. Missing, null or whitespace uses the hostname without writing a machine-specific name into distributed defaults. Matching ignores case and surrounding whitespace. Assign unique names to separate computers. Monitor and audio aliases select outputs inside that computer and are independent of device names. Camera and ExperienceX can share a computer's identity and UDP port; both receive broadcast messages, and only ExperienceX executes audio/video commands. Camera observes/logs events without adding coordinator rules.

The coordinator remains an ordinary peer. Events announce facts, commands request actions, and peers never automatically forward received messages. A future coordinator can subscribe to events and create explicit commands without becoming a required transport hub.

Every message requires `device`, `source`, and `requestId` (nonempty strings, maximum 128 characters). `device:"*"` explicitly targets everyone; a missing target is rejected. `source` identifies the originating peer and cannot be `*`. Use a new globally unique request ID for each intended action. Retries preserve the same ID and original body. `type` is `command` or `event`; omitted type means command. Events also require `event`. Unknown action/data fields are preserved by the envelope for the application to interpret.

```json
{"type":"command","device":"projection-pc","source":"coordinator","requestId":"b2a59231c156488ca8dde3ffbbd06008","experience":"video","value":"ping.mp4","monitorName":"Projector","mute":true}
```

```json
{"type":"event","device":"*","source":"entrance-sensor","requestId":"844265ff05af4a838c54633be56420b59","event":"proximity.enter","data":{"distanceCm":30}}
```

An event is observed/logged, never interpreted as a playback command just because media fields are present. Coordinator event-to-action rules are a separate application concern. Camera's trigger 3/4/5 continue to request ping.mp4 on Display One/Projector/Display Two respectively.

## Reliability and boundaries

Senders repeat a packet three times, 75 ms apart by default. Receivers suppress matching logical source + request ID for `RequestDeduplicationSeconds` (60 by default), regardless of sender IP or ephemeral port. Source names are compared without case. The bounded cache holds 4096 entries and resets on restart, so suppression is not an exactly-once guarantee. Unavailable media/outputs are not queued; a deliberate new attempt needs a new ID. Ignored targets log at Debug; duplicate suppression logs at Information; accepted messages, invalid input, expired requests and send failures have separate telemetry.

`processingTimeoutSeconds` defaults to ten seconds and bounds the delay from local receipt to dispatch using a monotonic clock. It prevents commands sitting in a busy application's dispatcher queue from executing much later, without requiring synchronized clocks on the offline LAN. It cannot measure how long a packet spent before reaching the application. Send attempts are also time limited. An optional `expiresAt` ISO 8601 timestamp with an offset provides an absolute deadline for clients whose clocks are aligned; our default senders omit it. `hopCount` defaults to zero and must be 0–8. Peers do not relay packets, preventing transport feedback loops. Future forwarding/coordinator code must retain origin/correlation information and increment hopCount rather than endlessly reflecting messages.

The size cap avoids large control packets; use virtual audio group aliases when many endpoint names would exceed it. Sensors should debounce input and announce state changes instead of continuously streaming readings. Broadcast recipients must be on the same subnet with inbound UDP allowed and Wi-Fi client isolation disabled if applicable.

## Configuration

```json
{
  "DeviceName": "projection-pc",
  "UdpPort": 21325,
  "UdpBroadcastAddress": "255.255.255.255",
  "UdpInterfaceAddress": "",
  "UdpSendAttempts": 3,
  "UdpRetryDelayMilliseconds": 75,
  "RequestLifetimeSeconds": 10,
  "RequestDeduplicationSeconds": 60
}
```

The default broadcast address uses the operating system's selected route. On PCs with Ethernet and Wi-Fi, set `UdpInterfaceAddress` to the dedicated LAN's local IPv4 address and `UdpBroadcastAddress` to that subnet's broadcast address (for example 192.168.1.255 with a 255.255.255.0 mask). An empty interface allows automatic selection. All peers use the same port. Multiple cooperating local listeners use socket address reuse for broadcast delivery; do not depend on unicast delivery reaching every process sharing the port.

ExperienceX adds missing settings to the existing per-user configuration and applies alias/port changes live. Camera reloads its networking settings from its appsettings.json every second and before sending, retaining the last valid configuration on an invalid edit. Camera's `ExperienceDeviceName` selects the destination alias; blank targets the local hostname. Set it to the remote ExperienceX device's effective name for remote playback, or explicitly `*` for every receiver. `ExperienceIpAddress` is no longer used; the old `ExperienceUdpPort` is recognized only when `UdpPort` is absent.

Retries are bounded to 1–5 attempts, delays to 20–1000 ms and lifetimes to 300 seconds. Duplicate retention must cover the request lifetime and be at most 3600 seconds. Avoid duplicate aliases: there is no discovery service to resolve name collisions automatically.

## Command-line examples

```powershell
./experiencex/Send-Experience.ps1 -Device projection-pc -Value ping.mp4 -MonitorName Projector -Mute
./experiencex/Send-Experience.ps1 -Device projection-pc -Experience audio -Value sound.mp3 -AudioDevice Room -Volume 60
./experiencex/Send-Event.ps1 -Source entrance-sensor -Event proximity.enter -Data @{distanceCm=30}
```

Existing clients must add device/source/requestId to their old playback bodies. There is deliberately no fallback that treats an untargeted legacy message as a command to every device.

When `UdpBroadcastAddress` is `255.255.255.255` and `UdpInterfaceAddress` is empty, senders bind each active IPv4 adapter and send to its subnet broadcast address. This includes LANs without a gateway and avoids a virtual adapter capturing all requests. Every adapter/retry uses the same request ID, so receivers execute once. Set `UdpInterfaceAddress` to restrict the sender to one local adapter; an explicit subnet broadcast destination is retained. Logs include the actual source interface and destination. A failure on one adapter is logged and does not prevent sending through the others. If every send fails, the request reports an error.

Run `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj -- --network` for focused broadcast and receive checks. These validate adapter selection with virtual and physical subnets, explicit overrides, real local UDP reception, and duplicate suppression. Local reception does not prove another PC permits inbound traffic; the receiving app needs an inbound UDP firewall allowance on the active network profile.
