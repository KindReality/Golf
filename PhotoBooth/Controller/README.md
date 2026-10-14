# Controller

The **Audio** scene runs the **AudioAmbient** playlist: every current shared WAV file has an independent random timer (1–3 minutes per file), broadcasts an audio command to all Experience computers, and uses `audioDevice:"*"` for every active audio endpoint. Items have cooldowns based on their file durations to avoid repeating the same sound while it is playing; different files may overlap. Activate **Audio** from the scene selector to start it. Volume is configured per item (100 percent by default). The complete CanYouSeeMe recording and its three separate clips are included.

Controller receives sensor events and runs playlists. Its monitoring window shows the current scene, serial hardware, active playlists and recent activity. **Activate scene**, **Run playlist**, **Cancel selected** and **Open configuration** provide manual control.

## Run and configuration

```powershell
dotnet run --project Controller/Controller.csproj
dotnet build Controller/Controller.csproj -c Release
```

Debug builds use **Development**, with a default trigger cooldown of **1000 ms**. Release builds use **Production**, with **10000 ms**. MSBuild copies the corresponding environment transform to `appsettings.environment.json`; these are the only environments. User configuration does not select the build environment.

Build output is copied to `%OneDrive%\Home Technologies\Bin\Controller`, with the solution's existing root fallbacks. Another PC needs the .NET 9 Desktop Runtime. Run from a separate local folder so OneDrive can update the binaries.

The window starts in Normal state and fills the selected display's working area, leaving the taskbar available. Set `DefaultDisplayName` to a Windows display name such as `\\.\DISPLAY2`, or an alias defined in `DisplayAliases`, using the same notation as Experience. In JSON, for example: `"DefaultDisplayName": "Presentation", "DisplayAliases": { "Presentation": "\\\\.\\DISPLAY2" }`. An empty selection uses the primary display. If an assigned display disconnects, Controller logs the failure and falls back to the primary display so its controls remain accessible; it checks every second and returns when that display reconnects. Display configuration edits apply without restarting. Startup adds these fields to existing hostname configurations when they are missing, preserving other settings.

For first installation, double-click **Install-Controller.cmd** in the OneDrive build folder. It validates the build/runtime, requests administrator approval for media sharing and firewall setup, then calls **Restart-Controller.ps1**. For subsequent updates or restarts, double-click **Restart-Controller.cmd** after OneDrive finishes syncing. Restart closes the old instance, refreshes `%LOCALAPPDATA%\Home Technologies\Controller\Application`, verifies the copied EXE/DLL hashes and launches Controller locally. It retains hostname configuration in OneDrive, existing media and logs; source media, working settings and logs are never deployed. Routine restarts do not change network permissions or require administrator approval. Both CMD files pause on failure so the error can be read. A missing x64 .NET Desktop Runtime is reported before stopping an existing instance.

Install also creates **Saft Technologies > Controller** in the current user's Start Menu. The shortcut invokes the OneDrive **Restart-Controller.cmd** minimized, so it refreshes the local application copy before launching. It uses the source EXE icon and is created after a successful restart, outside elevated machine setup.

Initial setup creates the read/write `\\hostname\media` share and inbound UDP 54242/54243 and TCP 445 firewall rules, restricted to the Private network profile and local subnet. It creates or updates the dedicated non-administrator `SaftThings` account with the embedded media password, enables it, and grants NTFS Modify on the media folder while preserving other permissions. An existing administrator account with that name is rejected. Share access allows changes; NTFS remains the final access limit. Place distributable files directly in `%LOCALAPPDATA%\Home Technologies\Controller\Media`; subdirectories are not distributed. An existing `media` share pointing elsewhere is retained and reported as an installation error. Rerun Install once on existing machines for credential setup; use Restart afterward. Experience's installer stores matching credentials for each Controller hostname, so UNC access requires no mapped drive. Canceling setup prevents the restart.

First launch copies the validated OneDrive `appsettings.defaults.json` to the hostname file beside it:

```text
%OneDrive%\Home Technologies\Bin\Controller\appsettings.<computer-name>.json
```

The Windows hostname determines the filename, independently of DeviceName. OneDriveConsumer and OneDriveCommercial are root fallbacks. Missing OneDrive reports a startup error. Existing hostname files/backups are preserved; template updates apply only to a missing configuration. Old user-profile and executable-local settings are left intact and ignored. Startup populates missing required objects only in the hostname file. Live edits, including OneDrive changes from another PC, are monitored; invalid edits retain the last valid settings. Builds and installers never deploy or overwrite hostname files. `Controller.exe --configuration C:\path\appsettings.json` selects an isolated file.

If no scenes exist, startup saves an idle scene and empty default-ambient playlist. The supplied defaults run media-loop from default-ambient: five consecutive plays of Eye.mp4, targeting every device and video screen with muted audio. The default-video playlist contains the same five video commands; neither storyboard sends other commands or events. Plays are spaced 11,034 ms apart, including between ambient cycles. Media remains local at `%LOCALAPPDATA%\Home Technologies\Controller\Media` and is shared as `\\hostname\media`.

## Scenes and playlists

Scenes have a required ambient playlist and optional activating/deactivating storyboard playlists:

```json
"Scenes": [
  { "Name": "idle", "AmbientPlaylist": "idle-ambient", "DeactivatingPlaylist": "leave-idle" },
  { "Name": "welcome", "AmbientPlaylist": "welcome-ambient", "ActivatingPlaylist": "welcome" }
]
```

A scene change cancels unsent work owned by the previous scene, runs its deactivation, runs the destination's activation, then starts the destination ambient playlist. Already-sent audio/video continues naturally. Unrelated manual or trigger playlists continue. A newer scene request cancels remaining transition work and takes priority. Commands already sent are not repeated. Interrupting activation deactivates that partially activated scene before proceeding. Requests for the active scene or current destination do not restart actions or timers.

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

An item may invoke a local nested **storyboard** using `{ "Playlist": "name" }`. The parent waits until that nested playlist's final send attempt completes. Local references cannot invoke ambient playlists or include transport/payload fields. Ambient playlists belong to scenes. Cyclic references are rejected.

Ordinary commands use `Transport: "udp"` (default) or `"arduino"`, `Type: "command"` (default) or `"event"`, a required `Device`, and an object `Payload`. Arduino commands must target a configured board. Payload actions belong to the receiving application or firmware. ExperienceX ambient rendering and external Jinx/WLED/Art-Net/G-Force management remain separate work.

Failures are logged and execution continues. There are no required-command flags or Arduino retries; Controller defaults to one best-effort UDP send. Completion means the send attempts completed, not that remote media finished. Cancellation removes unsent work without undoing delivered commands. There are no media playback limits.

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
"Arduinos": [ { "Name": "lighting-board", "Port": "COM4", "BaudRate": 115200, "DtrEnable": true, "Enabled": true } ]
```

Discovered ports appear in the window but are not opened automatically. Arduino serial input is newline-delimited UTF-8 JSON using the same envelope, 8 data bits, no parity, one stop bit. Event `source` must match the configured board name. Serial events are handled locally when targeted and broadcast with their original identity. Oversized lines are discarded through the next newline. Controller owns assigned ports and reconnects every second. Writes time out after 250 ms; failed commands are not replayed. Running snapshots do not redirect commands to a newly assigned port.

Arduino/ESP32 firmware must implement its event reporting and command actions; this project does not program that firmware.

## Live edits and diagnostics

In the perspective editor, **Ctrl+Z** cancels an unfinished rectangle movement or restores the most recent saved movement. It retains any later layer-order changes. The editor keeps one movement undo; remote perspective updates invalidate it. **[** moves the selected perspective to the bottom of the preview order and **]** moves it to the top, saving and announcing the new definition order. These keys operate in configuration mode and leave ordinary text-field editing shortcuts alone. This preview ordering does not change the layer order of screens on Experience devices.

Every dispatched playlist command/event carries `context` containing its scene, sending playlist and item. Nested playlists identify their actual sending step; activation/deactivation and manual runs retain their original scene. Controller and ExperienceX logs attach this context, media filename, source/target and correlation ID to request-related entries. Match the root `RequestId` across both applications. ExperienceX's playback entries also include the resolved local file path. The Controller activity list includes the requested media filename when available.

Commands originating from ambient items carry `context.isAmbient: true`, including commands from storyboards invoked by those items. Regular storyboards carry `false`. Experience applies this priority to video only: a normal video request interrupts ambient video across that device; ambient video is skipped while any normal video is pending or playing. Audio tracks continue independently and are never interrupted by this priority rule. Once normal video finishes, the next ambient request can play; skipped requests are not queued. Repeated requests for an identical looping video on the same screen continue its existing decoder and fade state, with the latest request ID owning subsequent scene cleanup.

### Role targets

Each playlist item has a comma-separated `Roles` string, empty by default, `BroadcastMode` (default `Broadcast`), and `IncludeSubwoofer` (default `false`). In `Broadcast` mode a nonempty list targets every output with **any** matching role. In `Random` mode the item chooses one distinct listed role uniformly each time it executes, after its cooldown, and retains that choice for all its commands and inherited nested storyboards. Repeats of a role on successive executions are allowed. Random selection does not depend on currently online recipients. Matching ignores case and surrounding whitespace.

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

LED perspectives are available through **LED settings** or **A,L** in the perspective editor. Discover/select WLED and a whole segment; dimensions come from WLED. Each perspective has its own FPS and arrays have percentage thickness. The same button edits existing selections. Definitions reach Experience through general announcements/downloads. See [LED setup and configuration](../experiencex/Lighting/README.md). Run the updated installer once to include Private/LocalSubnet mDNS UDP 5353.

Run **Install-Controller.cmd** once after this update to add the Private/LocalSubnet TCP firewall rule for **54244**. Subsequent updates use Restart-Controller.cmd. UDP events/commands remain on 54242/54243. The bounded TCP endpoint requires no HTTP.sys URL reservation. GET `/definitions` returns a complete JSON snapshot of shared definitions, with an ETag; the announcement stays small regardless of perspective count. The snapshot contains only shared definitions, not private application settings or media credentials. DefinitionsPort defaults to 54244; a custom port requires a matching firewall rule. Changes to this port rebind the endpoint live. A bind failure is logged and does not prevent the Controller window or scheduling from starting.

**Ctrl+Shift+Alt+C** toggles configuration mode in both applications on the same Windows desktop. The same chord exits. While configuring, **E** in Controller focuses Experience; **C** in Experience focuses Controller. Letters do not switch applications while typing. Both apps must use the same Windows user and integrity level. Experience yields topmost enforcement while Controller is focused. The Configuration/Exit configuration button applies the same shared mode change.

In Controller configuration mode, click **Perspectives**. Media cards scroll horizontally above the preview; clicking one switches preview media without changing the perspective. Videos start looping muted, with Play/Pause and seeking. Images are also usable preview references. Only local Controller media is read. Preview media is never stored in a perspective.

A perspective stores a permanent ID, editable name and Rectangle { X, Y, Width, Height }, all percentages of source media. Its rectangle must remain inside 0–100%. A then P or Add perspective creates one; the first fills the media and later rectangles start centered at 50% width/height. Select by corner or interior; overlapping candidates cycle in definition order, with corner order UL, UR, BL, BR. Drag corners to resize a rectangle or its interior to move it. Drag an edge to move both of its endpoints perpendicular to that edge, preserving its angle and the rectangular shape; the opposite edge stays fixed. Corners take priority over edges, then interiors. Edge changes save on release and support Ctrl+Z. PointHitDiameterPercent uses the preview diagonal and defaults to a 5% diameter. N/click the centered name renames; Delete removes; R resets to full media; Z undoes the last save or cancels a keyboard draft. With the preview focused, Tab/Shift+Tab cycles corners, arrows adjust one source pixel, Shift+arrows ten; Enter saves and Escape cancels. Field edits commit with Enter/Tab or Apply and cancel with Escape. Mouse release saves a changed rectangle. Saved edits announce a new revision immediately; unchanged announcements repeat every ten seconds. Blue translucent fills, pulsing azure/red points and a white save flash match Experience's editing style.

Definitions are independent of filenames and apply to any video/image chosen later by Experience. Their permanent IDs survive renaming; identical perspective names on different controllers remain distinct. ControllerId is generated and saved in the hostname configuration at first startup; do not copy one machine's ID into another machine's working file. Local edits use an atomic merge and reject stale perspective snapshots instead of overwriting remote edits.


## Live playback

Click **Live** to operate without entering configuration mode. Choose **Video**, **Audio**, or **Roles** in the horizontal target row. Video shows screens and video files; Audio shows speakers and audio files. Roles shows the case-insensitive union reported by Experience devices, with counts of available screens/speakers. Select one or more tiles, then click media in the horizontal carousel to send one-shot playback. In Roles mode, video targets matching video screens and audio targets matching speakers across discovered devices; an output with multiple selected roles plays once. Role lists refresh when Experience inventory changes; offline-only roles are disabled. Selections are remembered separately for each target mode and hidden selections are not sent.

Audio outputs on each Experience share one coordinated request; separate PCs remain best effort. Audio volume is 0–100%; videos are muted by default. Locked media-bound and unavailable screens are disabled. Selections survive renames and navigation. Offline devices and duplicate aliases are marked; missing local media is skipped. Status reports sends, not playback confirmation.

Live, Perspectives and Configuration/Exit configuration share a horizontal navigation row. Dashboard and editors use the same window, including LED settings. Back returns to Dashboard.

Experience broadcasts a small inventory revision on startup, change, discovery and every 10 seconds. Controller retrieves complete bounded inventory over TCP 54245 (`/inventory`), including screens, speakers and locally available filenames. No playback acknowledgments are added. Run the updated **Install-Experience.cmd** once on each Experience PC for the Private/LocalSubnet TCP rule. `InventoryPort` can be customized, with a matching firewall rule. Devices are marked offline after 30 seconds without announcements.


Experience's **F** command creates a reusable perspective from width and height percentages only. `perspective.create` includes the target Controller ID, new perspective ID, screen-derived name, `widthPercent` and `heightPercent`; it includes no position or media. Controller centers the new rectangle initially, adds a unique name suffix when needed, and saves it atomically even outside Controller configuration mode. Repeated IDs are idempotent. The standard definitions announcement confirms the result; no playback acknowledgment protocol is added. Its position remains editable in the existing perspective editor with any preview media.
