# Controller

Looping video playlist commands are sent at their scheduled step and reannounced every **15 seconds** with fresh request IDs. Scene-owned loops remain announced until that scene changes or is stopped; manual/triggered playlist loops stop being announced when their run completes or is cancelled. Resends retain the original target, random role choice, playback speed and other settings. Only the newest loop for an identical target remains registered. Resends neither restart a playlist nor send playback stops. Experience keeps an unchanged loop playing and can catch up when it starts late. Periodic sends are recorded as `VideoLoopCommandReannounced`, with the retained target count in Controller health.

Role choices combine assigned screen/speaker roles with names referenced by every playlist, including nested, disabled and unreferenced playlists, plus roles shared by peer Controllers. The playlist editor offers these names alongside manual comma-separated rules; Live shows them with current eligible output counts, including zero. Role suggestions are shared through the existing definitions catalog and its revision announcements, without assigning roles automatically. Exclusions contribute their bare name (`^Subwoofer` → `Subwoofer`); wildcard rules are not role names.

Video commands have a **Playback speed** multiplier, default 1.0 and range 0.5–2.0, in the storyboard/Live action editor. Live media controls also offer **Video speed ×**, used for selected screens and roles. Pinned actions retain it. Video-duration estimates account for speed; configured delays, playlist bounds and fades remain in real seconds. Repeating the same video with a different speed changes its rate without restarting. Standalone audio playback is unchanged.

The playlist/Live command picker includes **Fade in** and **Fade out** with a target screen and duration in seconds. These change Experience video-screen opacity while playback continues; the display background or lower layers show through a faded-out screen. Item roles can target groups. Fades follow playback priorities, require no media filename, and hold their final opacity until another fade command. They do not automatically delay the next storyboard command; use the item's ordinary delay when sequencing fades. See [video screen fades](../NETWORKING.md#video-screen-fades).

The optional **AudioAmbient** playlist provides audio cues: each shared WAV has an independent random timer (1–3 minutes per file), broadcasts to all Experience computers, and uses `audioDevice:"*"` for every active audio endpoint. Assign this playlist to a scene to use it. Items have duration-based cooldowns; different files may overlap. Volume is configured per item (100 percent by default).

Controller receives sensor events and runs playlists. Its monitoring window shows the current scene, serial hardware, active playlists and recent activity. **Activate scene**, **Run playlist**, **Cancel selected** and **Open configuration** provide manual control.

## Run and configuration

```powershell
dotnet run --project Controller/Controller.csproj
dotnet build Controller/Controller.csproj -c Release
```

Debug builds use **Development**, with a default trigger cooldown of **1000 ms**. Release builds use **Production**, with **10000 ms**. MSBuild copies the corresponding environment transform to `appsettings.environment.json`; these are the only environments. User configuration does not select the build environment.

Build output is copied to `%OneDrive%\Home Technologies\Bin\Controller`, with the solution's existing root fallbacks. Another PC needs the .NET 9 Desktop Runtime. Run from a separate local folder so OneDrive can update the binaries.

Controller uses a borderless full-screen workspace on the selected display. Set `DefaultDisplayName` to a Windows display name such as `\\.\DISPLAY2`, or an alias defined in `DisplayAliases`, using the same notation as Experience. In JSON, for example: `"DefaultDisplayName": "Presentation", "DisplayAliases": { "Presentation": "\\\\.\\DISPLAY2" }`. An empty selection uses the primary display. If an assigned display disconnects, Controller logs the failure and falls back to the primary display so its controls remain accessible; it checks every second and returns when that display reconnects. Display configuration edits apply without restarting. Startup adds these fields to existing hostname configurations when they are missing, preserving other settings.

For first installation, double-click **Install-Controller.cmd** in the OneDrive build folder. It validates the build/runtime, requests administrator approval for media sharing and firewall setup, then calls **Restart-Controller.ps1**. For subsequent updates or restarts, double-click **Restart-Controller.cmd** after OneDrive finishes syncing. Restart closes the old instance, refreshes `%LOCALAPPDATA%\Home Technologies\Controller\Application`, verifies the copied EXE/DLL hashes and launches Controller locally. It retains hostname configuration in OneDrive, existing media and logs; source media, working settings and logs are never deployed. Routine restarts do not change network permissions or require administrator approval. Both CMD files pause on failure so the error can be read. A missing x64 .NET Desktop Runtime is reported before stopping an existing instance.

Install and Restart both create or repair **Saft Technologies > Controller** in the current user's Start Menu. The shortcut invokes the OneDrive **Restart-Controller.cmd** minimized, so it refreshes the local application copy before launching. It uses the source EXE icon. Install creates the shortcut before prerequisite checks or elevated machine setup; Restart repairs it before launching. Run these CMD files as your normal Windows user (accept only the installer's separate administrator prompt). They do not automatically pin an icon to Start; look under All apps or search for the application name. The script prints the exact shortcut path.

Controller checks once per second for ExperienceX in the current Windows session. If it is absent, Controller runs the sibling OneDrive `ExperienceX/Restart-Experience.ps1` with package verification, so playback still runs from the local TEMP copy. An existing instance or an ongoing restart is left alone. Failures are logged and retried after 30 seconds. `ExperienceSupervisionEnabled` defaults to `true`; setting it to `false` disables automatic starts immediately.

When ConfigurationMode is false, Controller starts minimized to the taskbar while networking, scenes and Experience supervision continue running. With ConfigurationMode true, it starts with its workspace visible. Restoring it from the taskbar keeps it open; display polling does not restore a minimized window. Verify startup behavior with `Controller.Tests --window-startup`.

The Azure **minimize icon** or **Alt+F4** minimizes Controller to the taskbar while scheduling and the Experience watchdog continue running. Click its taskbar entry to restore the full-screen window on its configured display. Controller does not create a notification-area icon. The toolbar's **power icon** restarts both Controller and Experience on this computer; its right-hand **chevron** opens Restart / Shutdown with the same behavior. Restart verifies both builds before stopping Controller, restarts Experience from the sibling OneDrive ExperienceX package, then launches Controller again. This ordering suspends the Experience watchdog during the restart. Configuration and media are retained. Automatic build updates still restart only the application whose binaries changed. Shutdown stops the watchdog and closes both applications on this computer. The **play icon** leaves configuration mode; the sliders icon enters it. Tooltips and accessible names identify the icon actions; **Ctrl+Shift+C** remains the keyboard toggle.

Initial setup creates the read/write `\\hostname\media` share and inbound UDP 54242/54243 and TCP 445 firewall rules, restricted to the local subnet on Public and Private networks. It creates or updates the dedicated non-administrator `SaftThings` account with the embedded media password, enables it, and grants NTFS Modify on the media folder while preserving other permissions. An existing administrator account with that name is rejected. Share access allows changes; NTFS remains the final access limit. Place distributable files directly in `%LOCALAPPDATA%\Home Technologies\Controller\Media`; category subdirectories are distributed recursively. An existing `media` share pointing elsewhere is retained and reported as an installation error. Rerun Install once on existing machines for credential setup; use Restart afterward. Controller prepares matching credentials for discovered peer hostnames, so UNC access requires no mapped drive. Experience only copies the local Controller library. Canceling setup prevents the restart.

First launch copies the validated OneDrive `appsettings.defaults.json` to the hostname file beside it:

```text
%OneDrive%\Home Technologies\Bin\Controller\appsettings.<computer-name>.json
```

The Windows hostname determines the filename, independently of DeviceName. OneDriveConsumer and OneDriveCommercial are root fallbacks. Missing OneDrive reports a startup error. Existing hostname files/backups are preserved; template updates apply only to a missing configuration. Old user-profile and executable-local settings are left intact and ignored. Startup populates missing required objects only in the hostname file. Live edits, including OneDrive changes from another PC, are monitored; invalid edits retain the last valid settings. Builds and installers never deploy or overwrite hostname files. `Controller.exe --configuration C:\path\appsettings.json` selects an isolated file.

New configurations start in **idle**, followed by **active**. Idle has an activating storyboard and ambient playlist (`idle-activating`, `idle-ambient`). Active has `active-activating`, then automatically broadcasts a scene command returning to Idle, using the activation playlist’s duration limits and estimated non-looping media end. The shipped template loops `Zombies/TerrifyingZombieHorde.mp4` when Idle activates and plays `Skulls/SkullStrobe.mp4` once when Active activates, both muted on all video screens. The Idle ambient playlist is empty. Missing-scene startup population creates these three playlists empty with the same Active-to-Idle transition. Existing playlist contents are preserved. Media remains local at `%LOCALAPPDATA%\Home Technologies\Controller\Media` and is shared as `\\hostname\media`.

## Scenes and playlists

Scenes have an optional activating storyboard and exactly one following behavior: an ambient playlist or a timed transition to another scene:

```json
"Scenes": [
  { "Name": "idle", "AmbientPlaylist": "idle-ambient", "ActivatingPlaylist": "enter-idle" },
  { "Name": "welcome", "ActivatingPlaylist": "welcome",
    "Transition": { "Scene": "idle", "Device": "*" } }
]
```

A scene change immediately selects the destination, cancels the previous scene's unsent work, and starts the destination's activation followed by ambient scheduling. Activation never blocks a newer scene request. Old callbacks cannot mark a newer scene active. Cancellation sends no playback stops: already-delivered video and loops continue until Experience accepts a replacement of equal or higher priority, and audio continues mixing. Unrelated manual and trigger playlists continue. Requests for the already-selected scene do not restart it, even during activation. Activation sends its scheduled cues without waiting for remote media playback or the storyboard's standalone duration hold. Failed activation is reported as Failed rather than Active.

**After activation** chooses **Run ambient playlist** or **Go to scene**. The latter shows the next scene, destination device (default `*`) and the current calculated duration. Its activation storyboard owns the timing: **Minimum duration (seconds)** defaults to **5**, and **Maximum duration (seconds)** is blank for no maximum. Automatic transition time is `min(maximum, max(minimum, latest estimated non-looping media end))`, measured from scene entry. Setting both limits to 3 gives a fixed three-second scene. Loops and disabled items do not extend the calculation. Empty/loop-only activation uses the playlist minimum; no activation playlist uses a five-second default. Media metadata is read off the UI/scheduler threads and cannot delay a configured maximum. Item delays accumulate in execution order, including nested storyboards; overlapping media use the latest end rather than summed durations. Unknown durations are indicated, with known durations and bounds still applied. Editing the activation playlist’s items or bounds refreshes the estimate on return to the scene. The estimate is calculated rather than stored; the scene has no separate duration fields.

Only automatic transitions use this countdown. A trigger or explicit scene command immediately selects a different scene and cancels the old countdown and unsent cues, regardless of its activation playlist’s minimum, maximum or estimated duration. No media stops or playback confirmations are involved. At the deadline Controller sends the existing UDP `command:"scene"` envelope, preserving context, correlation and loop protection. Broadcast applies locally and remotely; a named destination changes only that device. Repeated requests for the already-selected scene remain no-ops. Scene/playlist definitions are not automatically distributed by this command: each addressed Controller needs the relevant definitions. Running activations/countdowns keep their configuration snapshot; changing scene mode, activation or transition timing applies the next time that scene is entered. Ambient edits retain their existing live-update behavior.


Defaults contain **idle**, **active**, and three independent **active-zone-1**, **active-zone-2**, **active-zone-3** storyboard playlists. Each zone starts by requesting Active. Add its media/commands in Scenes → Playlists and assign sensors to these playlists. They initially contain only the scene command; the default Active scene controls its timed return to Idle. Triggers are enabled by default, including the entrance trigger demonstrating zone 1. When Active uses an ambient playlist, overlapping root trigger/manual/network playlists keep it active until the last one completes, then return to Idle. An explicitly configured next-scene transition owns its automatic destination instead. Custom scene names retain explicit control. Existing hostname configurations are preserved.

**Storyboard duration limits** are edited in seconds with millisecond precision and stored as `MinimumDurationMilliseconds` (default 5000) and optional `MaximumDurationMilliseconds`. A standalone storyboard completes at the later of its minimum and estimated non-looping media end, capped by its maximum. At maximum it stops sending unsent commands; playback already sent continues. Only a scene configured with **Go to scene** sends the next-scene command at that deadline; replacement media follows Experience's existing priority rules. Matching minimum/maximum gives a fixed duration. The maximum must not be below the minimum. A loop-only storyboard uses its minimum and does not wait for the loop to finish. Scene activation sends scheduled cues without waiting for media completion; its scene countdown is calculated separately from the same playlist bounds. Nested storyboards retain cancellation and their own maximum. Explicit scene changes always interrupt immediately.

**Ambient scheduling duration** retains `DurationMilliseconds`, edited in milliseconds. Blank schedules indefinitely; a value stops new random scheduling after that time without stopping already-sent playback. Ambient playlists do not use storyboard minimum/maximum limits. Startup moves an old storyboard `DurationMilliseconds` into its minimum and removes the old scene minimum. An explicitly configured playlist minimum takes precedence; otherwise the prior scene minimum is moved to its activation playlist. Playlist contents and ambient scheduling durations are preserved.

Commands carry `priority: "low"`, `"medium"` or `"high"`. Scene activation and ambient commands, including nested storyboards, use Low. Trigger/manual/network storyboards use Medium; looping video always uses Low. High is supported by the protocol but has no built-in producer. Experience arbitrates video per display/screen: lower cannot replace higher, equal can replace a different file, and the same file never restarts. The latest background loop resumes after foreground completion; a scene change does not clear or stop playback already sent. Audio always mixes across priorities.

Playlist **Audio outputs** (`AudioOutputMode`) defaults to `Configured`. `Random` chooses one audio output independently for each UDP audio command across usable Experience inventories. Offline or ambiguously named peers and peers lacking that media locally are excluded. Role targets restrict candidates; for example, `Roles: "^subwoofer,*"` excludes subwoofer outputs. An explicit Device scopes the computer. The wire command contains the selected device alias and one endpoint ID, preserving volume/mute/channel and priority. Repeated selection of the same speaker is allowed. Missing candidates log a command failure without broadcast/default-output fallback. Discovery updates eligibility; no endpoint list or role exclusion is hardcoded. Random mode accepts omitted or wildcard audioDevice and rejects an explicit audioDevices array or fixed endpoint. A bass speaker without the subwoofer role remains eligible.

The new whisper defaults are applied to new profiles, and ALPHA's hostname configuration was updated. Other computers' existing hostname files are preserved; they need their own playlist update and the categorized media downloaded before using these paths.

There are two playlist types. **Storyboard** items run in order. `DelayMilliseconds` pauses after the previous item finishes sending its commands. Commands within each item run in definition order:

```json
{
  "Name": "welcome",
  "Type": "storyboard",
  "Items": [
    {
      "Name": "greeting", "DelayMilliseconds": 0, "CooldownMilliseconds": 2000,
      "Commands": [
        { "Device": "projection-pc", "Payload": { "experience": "video", "value": "ping.mp4", "screen": "Projector/screen1", "mute": true } },
        { "Device": "projection-pc", "Payload": { "experience": "audio", "value": "welcome.mp3", "volume": 60 } }
      ]
    },
    {
      "Name": "announce", "DelayMilliseconds": 1000,
      "Commands": [
        { "Type": "event", "Device": "*", "Payload": { "input": "welcome", "event": "playlist.welcome" } }
      ]
    }
  ]
}
```

Controller playlists can send Experience visualization commands through the same generic payload mechanism. For example, `{ "Device": "*", "Payload": { "experience": "visualization", "command": "select", "visualType": "colorMap", "value": "Clear Blue Water", "screen": "Projector/gforce", "transitionSeconds": 1 } }`. Supported actions are select, previous/next and particle/sprite start/stop; see [Experience controls](../experiencex/README.md#direct-g-force-control). Scene/storyboard delays schedule sending; optional motion-field `delaySeconds` schedules a change inside G-Force. No preset, script, overlay or screenshot actions are exposed.

**Ambient** items each have an independent random interval; different items may overlap. Intervals are selected uniformly using integer milliseconds:

```json
{
  "Name": "idle-ambient", "Type": "ambient",
  "Items": [
    {
      "Name": "birds", "MinIntervalMilliseconds": 20000, "MaxIntervalMilliseconds": 60000,
      "CooldownMilliseconds": 45000,
      "Commands": [ { "Playlist": "bird-sounds" } ]
    }
  ]
}
```

Here the initial interval is 20–60 seconds; subsequent intervals are 45–60 seconds. Cooldown raises the random lower bound rather than adding another delay. Cooldown cannot exceed the maximum interval. If a scene re-enters while the item is cooling, its remaining cooldown constrains scheduling. Item identity is **Controller device + playlist + item**; cooldown persists across scene changes and configuration reloads, but resets when Controller restarts. Different items remain independent. A storyboard item due during cooldown waits rather than being skipped. Cooldown defaults to zero and begins when the item's final send attempt completes; partially sent cancelled items also start cooldown.

An item may invoke a local nested **storyboard** using `{ "Playlist": "name" }`. The parent waits for its duration and expected non-looping media end times. Local references cannot invoke ambient playlists or include transport/payload fields. Ambient playlists belong to scenes. Cyclic references are rejected.

Ordinary commands use `Transport: "udp"` (default) or `"arduino"`, `Type: "command"` (default) or `"event"`, a required `Device`, and an object `Payload`. Arduino commands target a named board, which can be discovered later; their payload requires `command`. Discovered boards execute only advertised commands. Payload actions belong to the receiving application or firmware. ExperienceX ambient rendering and external Jinx/WLED/Art-Net/G-Force management remain separate work.

Failures are logged and execution continues. There are no required-command flags or Arduino retries; Controller defaults to one best-effort UDP send. Completion waits for sending, configured duration and estimated non-looping media end times; it is not a remote playback acknowledgment. Cancellation removes unsent work without undoing delivered commands. There are no media playback limits.

### Looping video

Add `"loop": true` to a video command's payload (omitted/false plays once):

```json
{ "Device": "*", "Payload": { "experience": "video", "value": "ping.mp4", "screen": "*", "mute": true, "loop": true } }
```

ExperienceX repeats locally, fades in once, and stays visible between repetitions. The playlist can send later commands alongside the video. Completing or cancelling a playlist, switching scenes, or clearing running playlists never stops already-delivered playback. Only unsent commands belonging to the cancelled run are suppressed; unrelated manual and trigger runs continue. Looping video always has Low priority. Equal or higher priority can replace a different file; requesting the same file preserves its position and keeps looping enabled. No existing playlist is automatically changed to loop.

## Triggers and timing

An event trigger matches **SourceDevice + Input + Event**, case insensitively:

```json
{ "Name": "entrance", "SourceDevice": "entrance-sensor", "Input": "front-door", "Event": "proximity.enter", "Playlist": "welcome", "CooldownMilliseconds": 2000 }
```

Timed triggers use `IntervalMilliseconds` instead of event fields:

```json
{ "Name": "heartbeat", "IntervalMilliseconds": 30000, "Playlist": "welcome" }
```

The first tick occurs one interval after enabling. Busy/cooling ticks are skipped, never queued. Missed ticks during suspension are discarded. Unchanged intervals retain their anchors across reload. A trigger cannot overlap itself; different triggers may overlap. Trigger cooldown begins when its playlist finishes. Omitted values use the build environment default; zero disables it. Manual runs bypass trigger cooldown, but still obey item cooldown.

The deadline scheduler uses monotonic timestamps and a high-resolution one-shot wait without changing global timer resolution. Ordered commands await their local send attempts; independent runs share the scheduler and can overlap while I/O is pending. Logs record run/correlation IDs and local send latency. Windows scheduling and UDP delivery remain best effort, with no hard real-time guarantee or guaranteed remote arrival order. The [Windows timer API](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) falls back to ordinary waits when unavailable, with a warning.

## Networking and Arduino

UDP **54242** carries events; **54243** carries commands. All messages use the shared 1200-byte UTF-8 envelope and source/request-ID deduplication. Empty `DeviceName` uses the hostname. `device: "*"` addresses every peer. Controller does not acknowledge playback commands. It answers `command:"discover", service:"controller"` with a broadcast `controller.announced` event containing the actual Windows hostname, permanent controller ID, shared definitions revision and HTTP port. The same announcement is sent at startup, after shared definition changes, and every 10 seconds. This lets ExperienceX access `\\hostname\media` independently of the Controller's device alias.

```json
{ "type": "event", "device": "*", "source": "entrance-sensor", "requestId": "unique-event-id", "input": "front-door", "event": "proximity.enter", "data": { "distanceCm": 30 } }
```

Playlists may change scenes over the network:

```json
{ "type": "command", "device": "controller-pc", "source": "another-peer", "requestId": "unique-command-id", "command": "scene", "value": "welcome" }
```

`command: "playlist"` with a storyboard name in `value` starts a storyboard on the target Controller. This network command does not wait for remote completion. A scene/playlist command sent by Controller is also delivered locally when it targets the source device or `*`, including when network sending fails. The local copy and UDP echo share an ID and execute once within the deduplication window. Once its broadcast starts, a scene command completes that send independently of cancellation caused by its own scene change.

Derived messages use fresh request IDs and retain `correlationId`, `originSource`, `causationId`, `actionHistory` and increasing `hopCount`. Ancestry identifies device/trigger, device/playlist and device/scene actions and prevents returning loops. Trigger branches also have a bounded local causal cache. Chains have at most eight hops. The packet size cap includes ancestry, so long aliases or payloads can fail to send. See [the shared protocol](../NETWORKING.md).

```json
"ArduinoDiscovery": { "Enabled": true, "BaudRate": 115200 },
"Arduinos": []
```

Controller checks USB-backed serial ports every second, including those present at startup. It opens candidates using 8N1 and waits up to five seconds for a valid `saft.serial` version 1 `device.announced` event. The announcement provides a stable device ID, source alias, named inputs/events and supported commands. Registration is runtime state: no board name or COM port needs to be configured or persisted. Unknown USB ports are released and retried after 30 seconds; Bluetooth/PCI ports are not automatically opened. Connected devices appear in hardware status and their names are suggested in the command editor. DTR may reset a board; firmware must suppress startup sensor triggers. Existing hostname configurations inherit discovery enabled at 115200 baud when the new setting is omitted.

Serial input remains newline-delimited UTF-8 JSON using the shared 1200-byte envelope. Registered events must match the source and advertised input/events; transport announcements are consumed locally. Sensor events and status responses are handled locally when targeted and broadcast with their original identity. Named UDP commands can target the board directly and are bridged to its serial connection; wildcard network commands are not forwarded to attached hardware. Serial commands are routed by registered alias, not COM number, and must be advertised by the board. Duplicate names or IDs block both devices. Unplug/reconnect may change the COM port without changing playlist targets. Oversized lines are discarded through the next newline. Writes time out after 250 ms; failed commands are not replayed and queued writes do not redirect to a replacement connection.

Explicit `Arduinos` entries remain manual overrides (`Name`, `Port`, `BaudRate`, `DtrEnable`, `Enabled`); configured ports, including disabled entries, are excluded from discovery. A manual override can deliberately use a non-USB port. See the sketch documentation for the announcement schema, built-in `discover`/`status` commands and commissioning steps. Run `Controller.Tests --serial-discovery` for focused fake-device and read-only native inventory checks.

The repository includes a [three-input Arduino Mega break-beam sketch](../microcontroller/Arduino%20mega/README.md) using Bounce2 for firmware-only sensor debouncing. Upload it separately; Controller does not flash firmware or debounce sensor events again. Other Arduino/ESP32 firmware must implement its own event reporting and command actions.

## Live edits and diagnostics

Controller discovers other Controllers using UDP 54243 requests and UDP 54242 announcements (startup, changes and every ten seconds). `PeerDiscovery.Enabled` defaults to true. It downloads each peer's original perspectives from `/definitions` on its announced TCP port (default 54244), checks identity/revision/size, and offers the combined list at `/catalog` for its local Experience. Combined catalogs are never fetched from peers, preventing recursive relay loops. Offline peers stop appearing in the live catalog after thirty seconds; Experience retains cached perspectives for offline use.

Controllers refresh their local media metadata and announce its cached `mediaRevision` every ten seconds. The revision fingerprints a sorted inventory of relative paths, sizes and UTC modification times; it never hashes media contents. A first discovery or changed revision fetches the bounded `/media` inventory over the existing definitions TCP port. Unchanged revisions do not cause repeated remote enumeration or inventory downloads. Failed copies retry at `PeerDiscovery.MediaSyncIntervalSeconds` (default ten), reusing the downloaded inventory. The last successfully synchronized revision is remembered for the running session; restart checks peers again.

Peer files are copied from `\\hostname\media` to `%LOCALAPPDATA%\Home Technologies\Controller\Media`. Missing files copy; a newer UTC modification time replaces an older copy regardless of its originating Controller. Older remote files never overwrite newer local files. Equal timestamps with different sizes log a conflict and retain the local copy; matching timestamps and sizes are assumed identical. File dates are preserved, partial downloads remain hidden until replacement, and no automatic deletions occur. Renaming or deleting a source does not remove other copies, and another Controller may restore a deleted file. This policy assumes synchronized clocks and edits that update file metadata.

Controller prepares the existing dedicated SaftThings credentials for each discovered hostname in the current user's Windows Credential Manager using `MediaCredentials.ps1`; no mapped drive or administrator prompt is needed for this step. Controller's installer must already have created the remote account/share. The local Experience copies this library into its own local media cache using the same newer-file policy. Experience connects only to loopback, never directly to remote Controllers or SMB shares. Each Experience computer therefore needs a local Controller running under the same Windows user. Network playback commands are unchanged.

The initial background media inventory checks all playlist audio/video playback references, including disabled items. Missing local paths produce `PlaylistMediaMissingAtStartup` warnings in the activity feed and log, with playlist, item and filename. Warnings do not block startup or playback; a missing file may arrive from a peer later. Nested playlist references, scene commands and stop commands are not treated as media filenames.

On the dashboard, **Clear** beside Activity Feed clears its in-memory history;
diagnostic log files remain available. **Clear** beside Running Playlists cancels
all current runs, pending scene transitions and the current ambient schedule
immediately. It does not automatically return to Idle or send playback stops. Commands already delivered retain
their effects on receiving devices. The scene shows **Stopped** until a later
explicit scene request; new triggers and manual playlists remain available.
Configuration reload alone does not resume the stopped scene.

In the perspective editor, **Ctrl+Z** cancels an unfinished rectangle movement or restores the most recent saved movement. It retains any later layer-order changes. The editor keeps one movement undo; remote perspective updates invalidate it. **[** moves the selected perspective to the bottom of the preview order and **]** moves it to the top, saving and announcing the new definition order. These keys operate in configuration mode and leave ordinary text-field editing shortcuts alone. This preview ordering does not change the layer order of screens on Experience devices.

Every dispatched playlist command/event carries `context` containing its scene, sending playlist and item. Nested playlists identify their actual sending step; activation and manual runs retain their original scene. Controller and ExperienceX logs attach this context, media filename, source/target and correlation ID to request-related entries. Match the root `RequestId` across both applications. ExperienceX's playback entries also include the resolved local file path. The Controller activity list includes the requested media filename when available.

Commands originating from ambient items carry `context.isAmbient: true`, including nested storyboards. Regular storyboards carry `false`; explicit `priority` determines per-screen arbitration. Lower-priority video cannot interrupt higher-priority video; equal priority can replace a different file. Blocked loops are remembered and can resume after foreground completion. Audio mixes independently. Every rejected video command emits `playback.priorityRejected` per blocked screen, with media, output and requested/active priorities. Controller shows one concise entry in its Events feed. Duplicate UDP retries of the same command do not create duplicate entries.

### Role targets

Each playlist item has a comma-separated `Roles` string, empty by default, and `BroadcastMode` (default `Broadcast`). In `Broadcast` mode a nonempty list targets every output allowed by its ordered role rules. In `Random` mode the item chooses one distinct listed role uniformly each time it executes, after its cooldown, and retains that choice for all its commands and inherited nested storyboards. Repeats of a role on successive executions are allowed. Random selection does not depend on currently online recipients. Matching ignores case and surrounding whitespace.

Targets are ordered rules, evaluated left to right for each output. The first matching positive rule includes it; the first matching `^name` rule excludes it. `*` matches every output, including those with no roles; no match means no selection. Thus `^subwoofer,*` excludes subwoofers, while `*,^subwoofer` includes everyone. `Front,^subwoofer,*` includes Front speakers even if also subwoofers, then rejects other subwoofers, then includes the rest. `^*` denies everyone reaching it. In Random role mode, only positive terms are choices, and retained negative rules keep their original positions relative to the selected positive term. Random role mode needs a positive rule or `*`. Assigned screen/speaker roles remain ordinary names and cannot contain target operators. AudioOutputMode Random chooses one discovered endpoint from the resulting eligible set for each file.

Subwoofer is an ordinary role: use `Subwoofer` for subwoofers only, `Front,Subwoofer` for both roles, `*` for every output, or `^Subwoofer,*` for all outputs except subwoofers. The same rules apply to video and audio. Nested storyboards inherit role rules; an explicit child role list overrides them. Empty roles retain direct targeting. Experience initially assigns `Subwoofer` to a newly detected Windows default audio endpoint; existing role assignments remain editable and are preserved.

Video commands select video screens; audio commands select speakers. Each output plays once even if several roles match. Experience ignores unavailable outputs, missing media, and name-bound screens; there is no fallback to a default output when a role is unmatched. Frequency timers and cooldowns stay independent per item, regardless of which role was selected.

```json
{
  "Name": "scare",
  "Roles": "Entry,Scary",
  "BroadcastMode": "Random",
  "Commands": [
    { "Payload": { "experience": "video", "value": "Eye.mp4", "mute": true } },
    { "Payload": { "experience": "audio", "value": "Bell.wav", "volume": 35 } }
  ]
}
```

Omitting `Device` on a role playback command broadcasts to all Experience devices (`device: "*"`). An explicit `Device` limits role matching to that computer. Item roles replace playback `screen`, `displayName`, `audioDevice` and `audioDevices` selectors; other commands/events retain their existing routing. Nested storyboards inherit item roles, unless a descendant item supplies its own nonempty list. Individual playback payloads can also specify `roles` directly, with no direct output selectors. All existing mute, volume, channel, loop and ambient-priority behavior remains applicable. Roles and matched outputs appear in request logs; Experience discovery inventories include screen and speaker roles. Adding role properties does not assign roles to existing outputs automatically.

File watching with a one-second fallback poll applies valid edits live. Invalid edits retain the last valid configuration. Running playlists retain their settings; new runs use updated settings. Ambient schedules are replaced on reload without replaying activation; in-flight items finish using their snapshots. Removing the active scene selects the new first scene. Disabling a trigger leaves its active run alone. Telemetry settings take effect on restart.

Duplicate names/fields, typos, missing references, local cycles, and invalid timing/transport values reject the edit. Remote writers should replace files atomically. There is no legacy conversion. Logs have machine-name prefixes under `logs\Controller`. The window retains 250 recent entries, and the engine permits 256 concurrent runs. Second launches display a message and exit; force-closing the original does not leave a stale lock.

## Tests

```powershell
dotnet run --project Controller.Tests/Controller.Tests.csproj -p:CopyBuildOutputToOneDrive=false
dotnet run --project Controller.Tests/Controller.Tests.csproj --no-build -- --host
dotnet run --project Controller.Tests/Controller.Tests.csproj --no-build -- --window
```

These cover startup defaults, validation, ancestry, serial framing, ordered/nested execution, cooldowns, snapshots, trigger anchors, scene interruption, independent ambient activity, actual UDP/local dispatch, offline scene commands, hot reload and native window controls. Physical serial behavior and delivery between PCs still require hardware/network testing.

## Shared perspectives and configuration editor

LED perspectives are available through **LED settings** or **A,L** in the perspective editor. Discover/select WLED and a whole segment; dimensions come from WLED. Each perspective has its own FPS and arrays have percentage thickness. The same button edits existing selections. Definitions reach Experience through general announcements/downloads. See [LED setup and configuration](../experiencex/Lighting/README.md). Run the updated installer once to include Public and Private/LocalSubnet mDNS UDP 5353.

Run **Install-Controller.cmd** once after this update to add the Public and Private/LocalSubnet TCP firewall rule for **54244**. Subsequent updates use Restart-Controller.cmd. UDP events/commands remain on 54242/54243. The bounded TCP endpoint requires no HTTP.sys URL reservation. GET `/definitions` returns a complete JSON snapshot of shared definitions, with an ETag; the announcement stays small regardless of perspective count. The snapshot contains only shared definitions, not private application settings or media credentials. DefinitionsPort defaults to 54244; a custom port requires a matching firewall rule. Changes to this port rebind the endpoint live. A bind failure is logged and does not prevent the Controller window or scheduling from starting.

**Ctrl+Shift+C** toggles configuration mode in both applications on the same Windows desktop. The same chord exits. While configuring, **E** in Controller focuses Experience; **C** in Experience focuses Controller. Letters do not switch applications while typing. Both apps must use the same Windows user and integrity level. Experience yields topmost enforcement while Controller is focused. The Configuration/Exit configuration button applies the same shared mode change.

In Controller configuration mode, click **Perspectives**. Media cards scroll horizontally above the preview; clicking one switches preview media without changing the perspective. Videos start looping muted, with Play/Pause and seeking. Images are also usable preview references. Only local Controller media is read. Preview media is never stored in a perspective.

A perspective stores a permanent ID, editable name and Rectangle { X, Y, Width, Height }, all percentages of source media. Its rectangle must remain inside 0–100%. A then P or Add perspective creates one; the first fills the media and later rectangles start centered at 50% width/height. Select by corner or interior; overlapping candidates cycle in definition order, with corner order UL, UR, BL, BR. Drag corners to resize a rectangle or its interior to move it. Drag an edge to move both of its endpoints perpendicular to that edge, preserving its angle and the rectangular shape; the opposite edge stays fixed. Corners take priority over edges, then interiors. Edge changes save on release and support Ctrl+Z. PointHitDiameterPercent uses the preview diagonal and defaults to a 5% diameter. N/click the centered name renames; Delete removes; R resets to full media; Z undoes the last save or cancels a keyboard draft. With the preview focused, Tab/Shift+Tab cycles corners, arrows adjust one source pixel, Shift+arrows ten; Enter saves and Escape cancels. Field edits commit with Enter/Tab or Apply and cancel with Escape. Mouse release saves a changed rectangle. Saved edits announce a new revision immediately; unchanged announcements repeat every ten seconds. Blue translucent fills, pulsing azure/red points and a white save flash match Experience's editing style.

Definitions are independent of filenames and apply to any video/image chosen later by Experience. Their permanent IDs survive renaming; identical perspective names on different controllers remain distinct. ControllerId is generated and saved in the hostname configuration at first startup; do not copy one machine's ID into another machine's working file. Local edits use an atomic merge and reject stale perspective snapshots instead of overwriting remote edits.


## Live playback

The **Live** page combines pinned actions, a manual Command/Event sender, media controls and the activity feed in the main window. Select a command type, destination and parameters, then **Send now**. Events include an input and event name; optional **Source device** simulates a sensor's configured alias. Matching events also reach this Controller's triggers once. Each execution has a fresh request ID and uses the existing best-effort UDP command/event ports; sends do not confirm playback.

Give the action a name and choose **Pin action**. Pins appear across the top: the play icon executes immediately, while the pencil loads the saved action for editing. **Save pin** updates it; **New pin** creates another shortcut from the current form. Pins persist in `PinnedActions` in the hostname-specific configuration. Saving pins preserves scene scheduling and rejects conflicting remote edits. Commands support video, audio, scenes, local/network storyboards, Arduino and Advanced JSON. Manual video loops continue until replaced under Experience's existing priority rules; scene changes send no playback stops. **All / Commands / Events** filters the activity feed; **Clear** clears the visible history without deleting log files or pins.

Click **Live** to operate without entering configuration mode. Choose **Video**, **Audio**, or **Roles** in the horizontal target row. Video shows screens and video files; Audio shows speakers and audio files. Roles shows the case-insensitive union reported by Experience devices, with counts of available screens/speakers. Select one or more tiles, then click media in the horizontal carousel to send one-shot playback. In Roles mode, video targets matching video screens and audio targets matching speakers across discovered devices; an output with multiple selected roles plays once. Role lists refresh when Experience inventory changes; offline-only roles are disabled. Selections are remembered separately for each target mode and hidden selections are not sent.

Audio outputs on each Experience share one coordinated request; separate PCs remain best effort. Audio volume is 0–100%; videos are muted by default. Locked media-bound and unavailable screens are disabled. Selections survive renames and navigation. Offline devices and duplicate aliases are marked; missing local media is skipped. Status reports sends, not playback confirmation.

Live, Scenes and Perspectives use identical-size buttons in a horizontal navigation row alongside the configuration, minimize and power controls. Dashboard and editors use the same window, including LED settings. The turning return-arrow icon or **Backspace** returns to Dashboard; Backspace retains its normal editing behavior inside input fields. Leaving the Scenes page resets its library tab to **Scenes** and clears its internal Back history, retaining valid unsaved edits in the draft. Icons are drawn as DPI-scaled vectors in the Azure theme. Verify the split power actions and rendering with `Controller.Tests --toolbar`, and navigation with `--window`.

### Scene and playlist editor

In configuration mode open **Scenes**. The left library switches between Scenes, Playlists and Triggers and filters by name. New scenes receive an empty ambient playlist; the first scene is the startup scene (**Startup** changes it). Scenes link an optional activation storyboard, then choose an ambient playlist or a next-scene command. Playlist choices have **Edit** and **New** actions; scene transitions show their destination and the automatic estimate from the activation playlist’s minimum/maximum limits. New creates the correct playlist type, assigns it to that scene phase and opens its editor; Save persists the playlist and assignment together. Back returns to the scene with that selection intact. Existing playlists can still be selected from the phase dropdowns. Playlists also exist independently of scenes. Shared playlists show their references.

Storyboard items are ordered cues with a millisecond wait before each item, enabled state and cooldown. The dispatch overview shows minimum cumulative waits, not media durations: commands are sent in order and nested playlist execution can delay later cues. Reorder items using arrows or drag/drop; copy and delete affect the draft. Ambient items instead show independent minimum/maximum random intervals and cooldown. Changing one item's interval does not define the frequency of other items. Role targeting supports Broadcast and Random with ordered inclusion/exclusion rules.

The inspector separates **Item** settings from **Commands**. Typed command forms cover video, audio, scene commands, local/network storyboard playlists, network events and Arduino payloads. Destinations, output names, roles and media offer discovered/local suggestions and allow manually entered values for offline devices. Video supports looping; audio supports volume, mute and channel. Advanced JSON retains custom commands and coordinated multi-device audio arrays. Triggers match a source device/input/event or use a timer; their cooldown can inherit Development/Production defaults.

Edits remain in a draft until **Save** or **Ctrl+S**. **Undo edit** restores draft operations; **Reload** requires an explicit second click before discarding unsaved edits. Selecting a scene/playlist never activates it. **Activate scene** and **Run playlist** are explicit actions available after saving; Cancel run cancels the editor's latest run. Saving updates ambient scheduling while in-flight storyboards retain their original settings. Renaming/deleting the active scene requires a second Save acknowledgement because the engine switches to the startup scene. Referenced playlists cannot be deleted; playlist renames update scene, trigger and local nested-playlist references. Network commands retain their literal names. Atomic graph saves preserve machine/network/perspective settings and reject stale concurrent edits. The editor stays inside the main window and retains drafts when switching pages or leaving configuration mode. It does not implement automatic playlist updates from Experience.

Verify with `Controller.Tests --show-editor` (graph and native UI checks), `--window` and the normal fast suite.

Experience broadcasts a small inventory revision on startup, change, discovery and every 10 seconds. Controller retrieves complete bounded inventory over TCP 54245 (`/inventory`), including screens, speakers and locally available filenames. No playback acknowledgments are added. Run the updated **Install-Experience.cmd** once on each Experience PC for the Public and Private/LocalSubnet TCP rule. `InventoryPort` can be customized, with a matching firewall rule. Devices are marked offline after 30 seconds without announcements.


Experience's **F** command creates a reusable perspective from width and height percentages only. `perspective.create` includes the target Controller ID, new perspective ID, screen-derived name, `widthPercent` and `heightPercent`; it includes no position or media. Controller centers the new rectangle initially, adds a unique name suffix when needed, and saves it atomically even outside Controller configuration mode. Repeated IDs are idempotent. The standard definitions announcement confirms the result; no playback acknowledgment protocol is added. Its position remains editable in the existing perspective editor with any preview media.

Network setup uses fixed local executable paths and checks existing rules before requesting elevation again. Updating the binaries at those paths does not require another firewall change. Install again once to migrate older rules; use Restart for routine updates. `-RepairMachine` forces machine setup when needed. Windows-generated prompt rules for the exact runtime executable are replaced; unrelated rules and administrator blocks are preserved. A different network still must allow peer traffic (guest Wi-Fi/client isolation can prevent it).


Media can be organized in category subfolders (for example `Ghosts/` and `Witches/`) without separating audio and video into different folders. Live playback and perspective previews read these folders recursively and show relative paths. Experience downloads the same hierarchy locally; commands and announced inventory identify files by relative path, such as `Ghosts/Hauntings/Eye.mp4`. Identical filenames in different categories remain distinct. Existing flat files continue to work.
