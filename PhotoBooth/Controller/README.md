# Controller

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

The Azure **minimize icon** or **Alt+F4** minimizes Controller to the taskbar while scheduling and the Experience watchdog continue running. Click its taskbar entry to restore the full-screen window on its configured display. Controller does not create a notification-area icon. The toolbar's **power icon** restarts both Controller and Experience on this computer; its right-hand **chevron** opens Restart / Shutdown with the same behavior. Restart verifies both builds before stopping Controller, restarts Experience from the sibling OneDrive ExperienceX package, then launches Controller again. This ordering suspends the Experience watchdog during the restart. Configuration and media are retained. Automatic build updates still restart only the application whose binaries changed. Shutdown stops the watchdog and closes both applications on this computer. The **play icon** leaves configuration mode; the sliders icon enters it. Tooltips and accessible names identify the icon actions; **Ctrl+Shift+C** remains the keyboard toggle.

Initial setup creates the read/write `\\hostname\media` share and inbound UDP 54242/54243 and TCP 445 firewall rules, restricted to the local subnet on Public and Private networks. It creates or updates the dedicated non-administrator `SaftThings` account with the embedded media password, enables it, and grants NTFS Modify on the media folder while preserving other permissions. An existing administrator account with that name is rejected. Share access allows changes; NTFS remains the final access limit. Place distributable files directly in `%LOCALAPPDATA%\Home Technologies\Controller\Media`; subdirectories are not distributed. An existing `media` share pointing elsewhere is retained and reported as an installation error. Rerun Install once on existing machines for credential setup; use Restart afterward. Experience's installer stores matching credentials for each Controller hostname, so UNC access requires no mapped drive. Canceling setup prevents the restart.

First launch copies the validated OneDrive `appsettings.defaults.json` to the hostname file beside it:

```text
%OneDrive%\Home Technologies\Bin\Controller\appsettings.<computer-name>.json
```

The Windows hostname determines the filename, independently of DeviceName. OneDriveConsumer and OneDriveCommercial are root fallbacks. Missing OneDrive reports a startup error. Existing hostname files/backups are preserved; template updates apply only to a missing configuration. Old user-profile and executable-local settings are left intact and ignored. Startup populates missing required objects only in the hostname file. Live edits, including OneDrive changes from another PC, are monitored; invalid edits retain the last valid settings. Builds and installers never deploy or overwrite hostname files. `Controller.exe --configuration C:\path\appsettings.json` selects an isolated file.

If no scenes exist, startup saves idle/active scenes, empty ambient playlists and three active-zone storyboards. The supplied defaults run media-loop from default-ambient: five consecutive plays of Eye.mp4, targeting every device and video screen with muted audio. The default-video playlist contains the same five video commands. Cues wait 11,034 ms between sends; ambient cycles wait another interval after completion. Media remains local at `%LOCALAPPDATA%\Home Technologies\Controller\Media` and is shared as `\\hostname\media`.

## Scenes and playlists

Scenes have a required ambient playlist and optional activating/deactivating storyboard playlists:

```json
"Scenes": [
  { "Name": "idle", "AmbientPlaylist": "idle-ambient", "DeactivatingPlaylist": "leave-idle" },
  { "Name": "welcome", "AmbientPlaylist": "welcome-ambient", "ActivatingPlaylist": "welcome" }
]
```

A scene change cancels unsent work owned by the previous scene, runs its deactivation, runs the destination's activation, then starts the destination ambient playlist. Already-sent non-looping audio/video finishes naturally. Unrelated manual or trigger playlists continue. A newer request can replace pending transition/deactivation work. Activation always finishes first; the latest queued scene starts afterward, without starting obsolete ambient scheduling. Commands already sent are not repeated. Requests for the active scene or current destination do not restart actions or timers.

Defaults contain **idle**, **active**, and three independent **active-zone-1**, **active-zone-2**, **active-zone-3** storyboard playlists. Each zone starts by requesting Active. Add its media/commands in Scenes → Playlists and assign sensors to these playlists. They initially contain only the scene command, so return immediately to Idle until content or duration is added. The disabled entrance trigger demonstrates zone 1. Overlapping root trigger/manual/network playlists keep Active until the last one completes, then return to Idle. Custom scene names retain explicit control. Existing hostname configurations are preserved.

**Duration (ms)** (`DurationMilliseconds`) is optional. For storyboards, blank means automatic: finish after all cues and the expected end of the latest non-looping video/audio. A configured value is a minimum lifetime, never a media cutoff. Duration uses local Controller metadata and dispatch timestamps, so network/decode latency and same-file continuation can make remote completion differ. Unknown metadata is logged and needs an explicit duration; metadata workers are bounded and time out. Loops do not contribute an end time, so a triggered loop-only storyboard needs explicit duration. Scene-owned loops may continue until scene exit. For ambient playlists, blank schedules indefinitely; a value stops new random scheduling after that time and lets dispatched media finish. The editor accepts 0–86400000 milliseconds.

Commands carry `priority: "low"`, `"medium"` or `"high"`. Scene activation, ambient and deactivation commands, including nested storyboards, use Low. Trigger/manual/network storyboards use Medium; looping video always uses Low. High is supported by the protocol but has no built-in producer. Experience arbitrates video per display/screen: lower cannot replace higher, equal can replace a different file, and the same file never restarts. The latest background loop resumes after foreground completion; scene exit clears that scene's loop ownership. Audio always mixes across priorities.

Idle also invokes **whispers-sequence**, independently of its existing video item. Its six Whispers files play in alphabetical order. Each next cue waits the previous file's measured duration plus 250 ms; the playlist's 161163 ms duration includes the last file, so an ambient repeat cannot start early. This is duration-based scheduling, not a remote playback acknowledgment. If these files are replaced with recordings of different lengths, update the cue delays and total duration. A scene change cancels unsent whispers; already dispatched audio finishes naturally.

Playlist **Audio outputs** (`AudioOutputMode`) defaults to `Configured`. `Random` chooses one audio output independently for each UDP audio command across usable Experience inventories. Offline or ambiguously named peers and peers lacking that media locally are excluded. Role targets restrict candidates; the whisper items use `Roles: "^subwoofer,*"` to exclude subwoofer outputs. An explicit Device scopes the computer. The wire command contains the selected device alias and one endpoint ID, preserving volume/mute/channel and priority. Repeated selection of the same speaker is allowed. Missing candidates log a command failure without broadcast/default-output fallback. Discovery updates eligibility; no endpoint list or role exclusion is hardcoded. Random mode accepts omitted or wildcard audioDevice and rejects an explicit audioDevices array or fixed endpoint. A bass speaker without the subwoofer role remains eligible for whispers.

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

ExperienceX repeats locally, fades in once, and stays visible between repetitions. The playlist continues to its next command/item after sending, so audio and lighting can run alongside the video. Every loop belongs to the scene activation in which its playlist began, including nested, trigger, and manual runs. Completing or canceling a playlist does not end its already-sent loop. On scene change, Controller stops those playbacks by their original request IDs and sender identity, then runs deactivation/activation as usual. ExperienceX uses its configured video fade-out and keeps playback running through the fade. A replacement on the same screen takes priority; a delayed stop for the old request cannot stop it. Unsent loops from the previous scene are suppressed even in otherwise independent manual runs. Non-looping media and audio retain their existing behavior.

Stops wait for in-flight local sends, not remote playback/fade completion, and remain best effort over UDP. Other Controllers' loops are independent. Scene cleanup preserves the original networking snapshot if configuration/device identity changes. Looping commands need an active scene and are suppressed in an exiting scene's deactivation playlist. No existing playlist is automatically changed to loop.

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

On the dashboard, **Clear** beside Activity Feed clears its in-memory history;
diagnostic log files remain available. **Clear** beside Running Playlists cancels
all current runs, pending scene transitions and the current ambient schedule
immediately. It does not run deactivation playlists, automatically return to
Idle, or send playback cleanup commands. Commands already delivered retain
their effects on receiving devices. The scene shows **Stopped** until a later
explicit scene request; new triggers and manual playlists remain available.
Configuration reload alone does not resume the stopped scene.

In the perspective editor, **Ctrl+Z** cancels an unfinished rectangle movement or restores the most recent saved movement. It retains any later layer-order changes. The editor keeps one movement undo; remote perspective updates invalidate it. **[** moves the selected perspective to the bottom of the preview order and **]** moves it to the top, saving and announcing the new definition order. These keys operate in configuration mode and leave ordinary text-field editing shortcuts alone. This preview ordering does not change the layer order of screens on Experience devices.

Every dispatched playlist command/event carries `context` containing its scene, sending playlist and item. Nested playlists identify their actual sending step; activation/deactivation and manual runs retain their original scene. Controller and ExperienceX logs attach this context, media filename, source/target and correlation ID to request-related entries. Match the root `RequestId` across both applications. ExperienceX's playback entries also include the resolved local file path. The Controller activity list includes the requested media filename when available.

Commands originating from ambient items carry `context.isAmbient: true`, including commands from storyboards invoked by those items. Regular storyboards carry `false`. Experience applies this priority to video only: a normal video request interrupts ambient video across that device; ambient video is skipped while any normal video is pending or playing. Audio tracks continue independently and are never interrupted by this priority rule. Once normal video finishes, the next ambient request can play; skipped requests are not queued. Repeated requests for an identical looping video on the same screen continue its existing decoder and fade state, with the latest request ID owning subsequent scene cleanup.

### Role targets

Each playlist item has a comma-separated `Roles` string, empty by default, `BroadcastMode` (default `Broadcast`), and `IncludeSubwoofer` (default `false`). In `Broadcast` mode a nonempty list targets every output with **any** matching role. In `Random` mode the item chooses one distinct listed role uniformly each time it executes, after its cooldown, and retains that choice for all its commands and inherited nested storyboards. Repeats of a role on successive executions are allowed. Random selection does not depend on currently online recipients. Matching ignores case and surrounding whitespace.

Targets are ordered rules, evaluated left to right for each output. The first matching positive rule includes it; the first matching `^name` rule excludes it. `*` matches every output, including those with no roles; no match means no selection. Thus `^subwoofer,*` excludes subwoofers, while `*,^subwoofer` includes everyone. `Front,^subwoofer,*` includes Front speakers even if also subwoofers, then rejects other subwoofers, then includes the rest. `^*` denies everyone reaching it. IncludeSubwoofer appends a positive rule; earlier rules retain precedence. In Random role mode, only positive terms are choices, and retained negative rules keep their original positions relative to the selected positive term. Random role mode needs a positive rule or `*`. Assigned screen/speaker roles remain ordinary names and cannot contain target operators. AudioOutputMode Random chooses one discovered endpoint from the resulting eligible set for each file.

`IncludeSubwoofer: true` adds the `subwoofer` role to audio playback commands in either mode, without duplicates. Video commands retain their selected roles. Nested storyboards inherit the option; an explicit child role list overrides the normal roles while retaining inherited subwoofer inclusion. Random targeting and subwoofer inclusion require a nonempty item `Roles` list. Empty roles with the default settings retain direct targeting.

Video commands select video screens; audio commands select speakers. Each output plays once even if several roles match. Experience ignores unavailable outputs, missing media, and name-bound screens; there is no fallback to a default output when a role is unmatched. Frequency timers and cooldowns stay independent per item, regardless of which role was selected.

```json
{
  "Name": "scare",
  "Roles": "Entry,Scary",
  "BroadcastMode": "Random",
  "IncludeSubwoofer": true,
  "Commands": [
    { "Payload": { "experience": "video", "value": "Eye.mp4", "mute": true } },
    { "Payload": { "experience": "audio", "value": "Bell.wav", "volume": 35 } }
  ]
}
```

Omitting `Device` on a role playback command broadcasts to all Experience devices (`device: "*"`). An explicit `Device` limits role matching to that computer. Item roles replace playback `screen`, `displayName`, `audioDevice` and `audioDevices` selectors; other commands/events retain their existing routing. Nested storyboards inherit item roles, unless a descendant item supplies its own nonempty list. Individual playback payloads can also specify `roles` directly, with no direct output selectors. All existing mute, volume, channel, loop, scene cleanup and ambient-priority behavior remains applicable. Roles and matched outputs appear in request logs; Experience discovery inventories include screen and speaker roles. Adding role properties does not assign roles to existing outputs automatically.

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

Click **Live** to operate without entering configuration mode. Choose **Video**, **Audio**, or **Roles** in the horizontal target row. Video shows screens and video files; Audio shows speakers and audio files. Roles shows the case-insensitive union reported by Experience devices, with counts of available screens/speakers. Select one or more tiles, then click media in the horizontal carousel to send one-shot playback. In Roles mode, video targets matching video screens and audio targets matching speakers across discovered devices; an output with multiple selected roles plays once. Role lists refresh when Experience inventory changes; offline-only roles are disabled. Selections are remembered separately for each target mode and hidden selections are not sent.

Audio outputs on each Experience share one coordinated request; separate PCs remain best effort. Audio volume is 0–100%; videos are muted by default. Locked media-bound and unavailable screens are disabled. Selections survive renames and navigation. Offline devices and duplicate aliases are marked; missing local media is skipped. Status reports sends, not playback confirmation.

Live, Scenes and Perspectives use identical-size buttons in a horizontal navigation row alongside the configuration, minimize and power controls. Dashboard and editors use the same window, including LED settings. The turning return-arrow icon returns to Dashboard. Icons are drawn as DPI-scaled vectors in the Azure theme. Verify the split power actions and rendering with `Controller.Tests --toolbar`, and navigation with `--window`.

### Scene and playlist editor

In configuration mode open **Scenes**. The left library switches between Scenes, Playlists and Triggers and filters by name. New scenes receive an empty ambient playlist; the first scene is the startup scene (**Startup** changes it). Scenes link an optional activation storyboard, a required ambient playlist and an optional deactivation storyboard. Each phase has **Edit** and **New** actions. New creates the correct playlist type, assigns it to that scene phase and opens its editor; Save persists the playlist and assignment together. Back returns to the scene with that selection intact. Existing playlists can still be selected from the phase dropdowns. Playlists also exist independently of scenes. Shared playlists show their references.

Storyboard items are ordered cues with a millisecond wait before each item, enabled state and cooldown. The dispatch overview shows minimum cumulative waits, not media durations: commands are sent in order and nested playlist execution can delay later cues. Reorder items using arrows or drag/drop; copy and delete affect the draft. Ambient items instead show independent minimum/maximum random intervals and cooldown. Changing one item's interval does not define the frequency of other items. Role targeting supports Broadcast, Random and Include subwoofer.

The inspector separates **Item** settings from **Commands**. Typed command forms cover video, audio, scene commands, local/network storyboard playlists, network events and Arduino payloads. Destinations, output names, roles and media offer discovered/local suggestions and allow manually entered values for offline devices. Video supports looping; audio supports volume, mute and channel. Advanced JSON retains custom commands and coordinated multi-device audio arrays. Triggers match a source device/input/event or use a timer; their cooldown can inherit Development/Production defaults.

Edits remain in a draft until **Save** or **Ctrl+S**. **Undo edit** restores draft operations; **Reload** requires an explicit second click before discarding unsaved edits. Selecting a scene/playlist never activates it. **Activate scene** and **Run playlist** are explicit actions available after saving; Cancel run cancels the editor's latest run. Saving updates ambient scheduling while in-flight storyboards retain their original settings. Renaming/deleting the active scene requires a second Save acknowledgement because the engine switches to the startup scene. Referenced playlists cannot be deleted; playlist renames update scene, trigger and local nested-playlist references. Network commands retain their literal names. Atomic graph saves preserve machine/network/perspective settings and reject stale concurrent edits. The editor stays inside the main window and retains drafts when switching pages or leaving configuration mode. It does not implement automatic playlist updates from Experience.

Verify with `Controller.Tests --show-editor` (graph and native UI checks), `--window` and the normal fast suite.

Experience broadcasts a small inventory revision on startup, change, discovery and every 10 seconds. Controller retrieves complete bounded inventory over TCP 54245 (`/inventory`), including screens, speakers and locally available filenames. No playback acknowledgments are added. Run the updated **Install-Experience.cmd** once on each Experience PC for the Public and Private/LocalSubnet TCP rule. `InventoryPort` can be customized, with a matching firewall rule. Devices are marked offline after 30 seconds without announcements.


Experience's **F** command creates a reusable perspective from width and height percentages only. `perspective.create` includes the target Controller ID, new perspective ID, screen-derived name, `widthPercent` and `heightPercent`; it includes no position or media. Controller centers the new rectangle initially, adds a unique name suffix when needed, and saves it atomically even outside Controller configuration mode. Repeated IDs are idempotent. The standard definitions announcement confirms the result; no playback acknowledgment protocol is added. Its position remains editable in the existing perspective editor with any preview media.

Network setup uses fixed local executable paths and checks existing rules before requesting elevation again. Updating the binaries at those paths does not require another firewall change. Install again once to migrate older rules; use Restart for routine updates. `-RepairMachine` forces machine setup when needed. Windows-generated prompt rules for the exact runtime executable are replaced; unrelated rules and administrator blocks are preserved. A different network still must allow peer traffic (guest Wi-Fi/client isolation can prevent it).


Media can be organized in category subfolders (for example `Ghosts/` and `Witches/`) without separating audio and video into different folders. Live playback and perspective previews read these folders recursively and show relative paths. Experience downloads the same hierarchy locally; commands and announced inventory identify files by relative path, such as `Ghosts/Hauntings/Eye.mp4`. Identical filenames in different categories remain distinct. Existing flat files continue to work.
