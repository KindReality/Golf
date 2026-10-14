# Direct G-Force control

Verified on ALPHA on October 12, 2026, against installed G-Force Platinum 5.8.3.
`GForceControlClient` controls an owned Standalone using TCP over loopback. No Toolbar,
global keystrokes, injected code, vendor-library changes or native DLL ABI are required
by the client. VisualizationManager applies launch defaults, handles live edits and recovery,
and routes a closed set of visualization UDP commands through this connection. Media
playback commands remain independent. See the README for the command/configuration schema.

## Verified controls

- Read installed themes and the active theme; activate `Infernal` and read it back.
- Select `Clear Blue Water` with a 0.25-second transition and read back the active ColorMap.
- Disable automatic ColorMap changes and verify the chosen palette remains active.
- Set audio sensitivity to 0.75 and read it back.
- Start an `Explosion` particle and verify it appears in the running-particle list.
- Serialize concurrent calls without confusing their replies.

`GForceControlEndpoint` discovers the TCP listener by the process ID of the Standalone
that the caller owns. The port is not assumed constant and Bonjour is not needed for this
local connection. Only loopback endpoints are accepted. The caller remains responsible
for process ownership and startup retries if the listener is not yet ready.

Example for an already launched, owned Standalone:

```csharp
using var controls = await GForceControlClient.ConnectAsync(ownedProcessId, cancellation);
await controls.SetColorMapSlideshowAsync(false, cancellation);
await controls.SetColorMapAsync("Clear Blue Water", 1.0, cancellation);
var activePalette = await controls.GetColorMapAsync(cancellation);
```

## Recovered framing

Disassembly of the installed Toolbar's packet constructors established the frame layout.
Independent TCP queries and readbacks from the running Standalone verified it. Frida was
used only during investigation to observe the owned test process; it is not a runtime dependency.

Every frame consists of a 76-byte header followed by the declared payload length:

| Header offset | Meaning observed |
| --- | --- |
| 0 | Protocol version: 2 |
| 1 | Message type: 1 executes a command, 2 is a notification, 3 is a reply |
| 2 | Subtype; notification 41 includes console output |
| 4–7 | Request ID, unsigned 32-bit little-endian |
| 8 | Nonzero on commands requests a reply |
| 16–47 | Opaque command metadata; this client sends zeroes |
| 64–67 | Payload length, 32-bit big-endian |
| 68–75 | Vendor timing fields; accepted as zero in requests |

Interpreter commands use UTF-8 plus a final NUL byte, included in the length. Reply
payloads are UTF-8 output and may be empty. On connection the client executes
`SetClientDesc("ExperienceX", 530)` using the same interpreter call/version argument
found in the installed Toolbar. No password exchange was observed for local control.

Startup and preference changes can interleave unsolicited binary type-2 notifications.
These must be fully consumed before reading the expected reply; they must not be decoded
as reply text or treated as request completion. Requests are serialized, IDs are checked,
and payloads are bounded at 1 MiB. The response deadline is three seconds. A malformed,
cancelled or timed-out exchange closes the connection so a late reply cannot affect a
subsequent request. Reconnect explicitly; do not automatically retry an uncertain effect.

An empty acknowledgment does **not** prove that a setting was applied. The engine can
report interpreter errors through console notifications and still acknowledge with empty
output. The native test checks actual settings by readback. Console notifications with the verified console-message payload are logged by the manager,
with request context during commands. Other binary notifications are consumed without
being interpreted as console output. Catalog names are validated before changes, and
logs distinguish native command acceptance from a completed transition.

## Validation and compatibility

Run `dotnet run --project ExperienceX.Tests -p:CopyBuildOutputToOneDrive=false -- --gforce-control`.
The native test refuses to alter an independently running Standalone, suppresses Toolbar
autolaunch for its owned test instance, and restores the original preference files in
`finally`. An independently running Toolbar is preserved. The test closes its owned process.

Use `--gforce-control-protocol` for only the synthetic network checks: fragmented headers
and large Unicode payloads, binary notifications, concurrent requests, command-name quoting,
invalid version/ID/length, truncated connections, timeouts and cancellation. The native
test also verifies that an interpreter-error notification does not corrupt later replies.

This is a private, reverse-engineered protocol, not a supported SoundSpectrum SDK.
Only the installed version has been verified. LAN control remains on the existing UDP
command channel; the private G-Force protocol is used only on loopback. Recovery recreates
the owned process/connection and reapplies launch defaults. Fullscreen also uses the
direct SetFullscreen/IsFullscreen API. No script text or preference keys are accepted
from UDP. Automatic visual changes are not configurable and are disabled at startup,
after commands, and when the one-second check detects external re-enablement.
