# ExperienceX â€” Direct3D 11 prototype

ExperienceX is isolated from Experience: its executable, mutex, logs, and default UDP port are distinct. Camera and ExperienceX use UDP 54242 for events and UDP 54243 for commands; ExperienceDeviceName selects the receiving computer alias or hostname. No C++ rewrite is required for this prototype.

See [ARCHITECTURE.md](ARCHITECTURE.md) for source organization, thread/resource ownership, configuration migrations, shutdown, and test suites.

Run `dotnet run --project experiencex/ExperienceX.csproj` from the repository root, or launch the built `ExperienceX.exe`. Requires the .NET 9 Windows Desktop runtime unless published self-contained. Only one instance runs per desktop session; a second launch displays a message and exits. Closing any ExperienceX window shuts down the application and its playback outputs.

To run on another PC without locking the OneDrive build output, double-click **Restart-Experience.cmd** in `%OneDrive%\Home Technologies\Bin\ExperienceX`. Builds and publishes include the CMD and its PowerShell helper. The launcher stops ExperienceX in your current Windows session, copies the contents beside the CMD, excluding `Media`, into `%TEMP%\Experience`, removes obsolete cached files, and starts `%TEMP%\Experience\ExperienceX.exe` with that folder as its working directory. Existing cached `appsettings.json` and `appsettings.json.bak` are retained; the pair is seeded only when neither exists; shipped `appsettings.defaults.json` is updated. It preserves cached `Media` and `logs` history. Media is never copied, including on first launch; place files separately in `%TEMP%\Experience\Media` or configure absolute media directories. It leaves the application's active per-user configuration and keystones untouched. Run the same OneDrive CMD again after updated files finish syncing. Do not launch the CMD from the cached TEMP copy. On failure, it shows the error and pauses; it does not start an incomplete copy. Inbox Windows PowerShell is sufficient; the app still needs its normal .NET Desktop Runtime. Make the OneDrive output available locally before using it offline.

**Ctrl+Shift+Alt+C** enables configuration mode globally, even while another application has focus. While configuration mode is enabled, use the single letters **C** to exit configuration mode, **X** to close ExperienceX, and **B** to toggle background transparency on the display under the mouse pointer. These global letter shortcuts are released as soon as configuration mode is disabled, including through a configuration-file update. Held-key repeats are ignored, and conflicts are logged independently and shown in a brief notice. Closing uses normal graceful shutdown: all display windows close, playback stops, and resources are cleaned up.

**B** makes any solid background transparent immediately on the display under the pointer. If the background is already transparent, **B** opens a persistent color prompt without changing the background: press **B** again for opaque black or **W** for opaque white. The choice applies to the display where the prompt opened, even if the mouse moves to another display. W is only captured while the prompt is open. The background remains transparent until a color is chosen; chosen changes save immediately. Leaving configuration mode, editing a screen name, starting screen creation, toggling screen chroma key, or a remote change to the visible background cancels the pending choice. Other solid colors can still be set in the configuration file. For point editing on the focused display, **R** resets and **Z** undoes. Control and Alt are not needed in configuration mode. Tab, arrows, Enter, and Escape retain their existing functions; Shift still selects larger arrow steps or reverses Tab order.

**T** toggles black chroma key on the selected video screen in configuration mode. Select the screen by clicking its interior or a point first. The notice names the screen and reports chroma key ON/OFF. This saves the screen's own `BlackKey.Enabled` immediately and applies to the next rendered frame without restarting playback. Other screens and the whole-display background remain unchanged. Without a selection, T prompts you to select a video screen; mats stay opaque black. Letter shortcuts pause while editing a screen name.

While ExperienceX has keyboard focus, the mouse pointer hides after five seconds without movement. Moving the mouse or switching focus to another application restores it within 100 ms. The countdown restarts when ExperienceX regains focus. This also applies during keystone editing; keyboard activity alone does not reset the mouse idle countdown. Shutdown restores any pointer visibility change owned by the app.

The single-instance check tests actual mutex ownership, allowing restart after a forced termination or while an old duplicate-launch message remains open. A rejected launch closes its mutex handle before showing the message. `SingleInstanceAcquired` records whether ownership was recovered after an abnormal exit. An idle transparent display can look unchanged even though the application is running; use Ctrl+Shift+Alt+C to show configuration controls.

ExperienceX creates a separate window for every connected display, transparent unless a solid background is configured. Production windows cover their display, including the taskbar, and remain topmost during playback or while displaying a solid background. Display availability is checked every second and on display changes. A disconnected display's window is immediately hidden and its video canceled; reconnecting restores its configured idle background. Newly connected displays receive their own window. Other displays and independent audio continue playing.

## UDP commands

Broadcast one UTF-8 JSON object per datagram to IPv4 UDP command port **54243**; events use **54242** (configurable), up to 1200 bytes. Every message requires `device`, `source` and a unique `requestId`. `device` is the destination computer's configured `DeviceName`, falling back to its hostname; `*` explicitly targets everyone. Matching ignores case and surrounding whitespace. Missing destinations are rejected. Commands are anonymous, without authentication or acknowledgements. Delivery is best effort. See [the shared networking guide](../NETWORKING.md) for events, configuration and migration.

Retries with the same `requestId` and logical `source` are suppressed for `RequestDeduplicationSeconds` (default 60), even when sender IP or port changes. Use a new ID for each distinct command, including commands for different displays/channels. The bounded cache holds at most 4096 IDs; restarting clears it. Requests later rejected for unavailable media/outputs require a new ID to try again. Retention must cover RequestLifetimeSeconds. Our senders make three identical attempts 75 ms apart and include a ten-second local processing budget. Default messages do not require synchronized clocks. The application sends no UDP replies.

```json
{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"video","value":"ping.mp4","displayName":null,"mute":false}
```

```json
{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"video","value":"ping.mp4","screen":"Projector/screen1","mute":true}
```

```json
{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"audio","value":"sound.mp3","audioDevice":null,"channel":1,"volume":35,"mute":false}
```

`displayName` omitted/null/empty uses `DefaultDisplayName`; an empty default selects the primary display. `audioDevice` omitted/null/empty uses `DefaultAudioDevice`; an empty default selects the system multimedia audio output. Audio devices accept an exact friendly name (case insensitive) or endpoint ID. Duplicate friendly names are rejected; use their IDs. A missing display is rejected without falling back. Missing audio outputs are logged and skipped; a request with no available outputs is rejected. Requests are not queued for reconnection.

### Coordinated audio and virtual devices

Send one audio request to any subset of outputs. Prefer the `audioDevices` array, especially when friendly names contain commas:

```json
{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"audio","value":"sound.mp3","audioDevices":["Device A","Device B","Device C"],"volume":60}
```

`audioDevice:"Device A,Device B"` also accepts a comma-separated list. A whole exact physical name is resolved before splitting, preserving names containing commas. Use either `audioDevice` or `audioDevices`, not both. The request's channel, volume and mute apply to every selected output. Duplicate endpoint IDs reached through different names/groups play only once per request. Up to 32 distinct outputs are supported.

Define virtual audio devices in the current user's local configuration:

```json
"VirtualAudioDevices": {
  "Room": ["Device A", "Device B", "Device C"],
  "Front": ["Device A", "Device B"]
},
"DefaultAudioDevice": "Room",
"AudioDeviceLatencyMilliseconds": {}
```

Replace these placeholders with physical endpoint IDs or exact friendly names from the log. Then send `{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"audio","value":"sound.mp3","audioDevice":"Room","volume":60}`. Virtual names ignore case/outer whitespace and can also appear in `audioDevices`. These are application groups, not Windows audio devices. Members must be physical names/IDs; nested groups are rejected. Names must not conflict with physical devices. Membership/default changes reload automatically and affect new requests; an existing request keeps its selected devices. No real devices are assigned to groups automatically.

All available selected endpoints are prepared before a common future media start. Multiple outputs share one decoder and bounded PCM history, with separate shared-mode WASAPI streams. Endpoint clock/QPC timestamps align playback against one timeline; band-limited resampling gently corrects clock drift. Separate grouped requests can overlap on the same speakers. Missing, disconnected, invalid-channel or failed outputs are logged and skipped while other selected outputs continue. If none remain, playback is rejected or ends with a failure. A reconnected output does not join an existing request.

Different speakers can add processing delays that Windows does not report. `AudioDeviceLatencyMilliseconds` maps a physical endpoint ID or friendly name to its measured **additional downstream latency**, from 0 to 5000 ms. IDs take precedence over names. For example, `{"Device B":25}` schedules Device B's samples 25 ms earlier relative to the other outputs. This compensation is captured when a coordinated request is prepared. Measure/test the speakers at the intended listening position; software clock alignment cannot guarantee sample-perfect acoustic alignment across unrelated hardware. Group startup includes roughly 300 ms of preparation lead, plus the largest configured downstream latency. Synchronization logs report endpoint-clock/cursor error, estimated clock drift, and underruns, rather than claiming acoustic measurements.

Use a new requestId for each intended action; projection-pc is a placeholder device alias.

```powershell
./experiencex/Send-Experience.ps1 -Experience audio -Value sound.mp3 -AudioDevices 'Device A','Device B' -Volume 60
./experiencex/Send-Experience.ps1 -Experience audio -Value sound.mp3 -AudioDevice Room -Volume 60
```

### User-managed display aliases

Edit `DisplayAliases` in the local `appsettings.json` described below. For example (assign the Windows names to match your actual layout):

```json
"DisplayAliases": {
  "Left": "\\\\.\\DISPLAY2",
  "Center": "\\\\.\\DISPLAY1",
  "Projector": "\\\\.\\DISPLAY3"
},
"DefaultDisplayName": "Center"
```

Then send `{"device":"projection-pc","source":"controller","requestId":"use-a-new-unique-id","experience":"video","value":"ping.mp4","screen":"Projector/screen1"}`. Alias names ignore case and surrounding whitespace. Raw Windows display names still work. Changes reload through the configuration file watcher; they apply to new requests and do not move existing playback. Unknown aliases and disconnected targets are logged and rejected without fallback. Aliases must have unique nonempty names and nonempty targets; aliases reference Windows display names directly, not other aliases. The supplied map uses Projector=DISPLAY2, Display One=DISPLAY1, and Display Two=DISPLAY3; verify these assignments on another PC. Windows display numbering can change after hardware/connection changes; update the map if that happens. This map does not rename displays in Windows or bind to hardware serial numbers.

Channel numbers start at **1**, in the selected device's configured Windows channel order (normally 1=left, 2=right). A specified channel receives the source channels averaged to mono; other output channels are silent. Omitted/null `channel` retains the source channel ordering; mono is duplicated to front left/right and stereo occupies the first two channels. A source with more channels than the device is rejected unless an explicit channel is supplied. Configure multichannel devices with the desired channel count in Windows. Audio is resampled to the endpoint rate when necessary.

Video playback is independent per screen; a new video replaces only the previous video on the targeted screen. Other screens on the same display continue playing. Each standalone audio request plays independently, including multiple tracks on the **same audio device and channel**. Windows mixes their shared-mode streams. Each track keeps its own volume, mute and fades. Use a distinct `requestId` for each intended track; retrying the same ID still suppresses duplicates.

Audio `volume` is a numeric percentage from **0 to 100**, including fractional values. Omitted/null defaults to **100**. It scales that track's samples before mixing and multiplies its fades; `mute:true` overrides volume. Zero volume still advances playback until the natural media end. Invalid volumes are rejected. This controls standalone audio requests; video's embedded sound remains enabled unless `mute` is true and uses the normal video audio output. `mute` defaults to false for both experience types. Neither setting changes system volume or another track's volume. Overlapping loud tracks add together, so lower individual volumes when needed to avoid distortion.

There is currently **no stop command**. Playback finishes at the file's natural end. Required `requestId` and `source` are carried into telemetry. Malformed requests, unsupported experience names, unavailable files, and invalid channels are rejected without replacing valid playback. Files must resolve inside the configured media directory; URL and rooted-path requests are rejected.

Use a new requestId for each intended action; projection-pc is a placeholder device alias.

```powershell
./experiencex/Send-Experience.ps1 -Value ping.mp4
./experiencex/Send-Experience.ps1 -Value ping.mp4 -DisplayName '\\.\DISPLAY2' -Mute
./experiencex/Send-Experience.ps1 -Experience audio -Value sound.mp3 -AudioDevice 'Speakers (USB Audio Device)' -Channel 2 -Volume 35
```

### Display backgrounds

`DisplayBackgrounds` maps a configured alias or raw Windows display name to separate `BackgroundColor` and `IsTransparent` values. `BackgroundColor` is an opaque `"#RRGGBB"` color; `IsTransparent` controls whether the background is visible. Newly detected displays are created and saved with black (`#000000`) and `IsTransparent:false`, including on startup and hot-plug. Existing settings are preserved. For example:

```json
"DisplayBackgrounds": {
  "Projector": { "BackgroundColor": "#000000", "IsTransparent": false },
  "Display One": { "BackgroundColor": "#000000", "IsTransparent": true },
  "Display Two": { "BackgroundColor": "#000000", "IsTransparent": false }
}
```

Each background fills the entire display, including outside the keystone quadrilateral and video letterboxing. It stays visible while idle, during video fades, and through black-keyed video pixels. Only the video fades; solid backgrounds remain opaque. Rendering composites video over the background on the GPU. Background transparency is independent of chroma key and video fading. Alias, color and transparency edits reload immediately without restarting or interrupting playback. Removing an entry recreates black/opaque defaults when the connected display is detected. Each display can have only one entry (do not configure both its alias and device name); invalid colors or ambiguous targets reject the edit and retain the last working settings. Unplugging a display still hides its window. Solid backgrounds remain topmost while idle; transparent idle windows retain the existing behavior.

Configuration version 2 migrates legacy background strings while preserving their appearance: `"transparent"` becomes black plus `IsTransparent:true`; a solid color becomes that color plus `IsTransparent:false`. Migration is atomic, backs up the old file, and preserves keystones and unknown settings. Legacy string edits are still accepted and converted; use objects for new configuration.

## Configuration

ExperienceX uses a local configuration file for the current user on this PC:

```text
%LOCALAPPDATA%\Home Technologies\ExperienceX\Configuration\appsettings.json
```

The root comes from the Windows local application data known folder. It follows the current user's profile without a hard-coded username or drive location. Configuration does not require OneDrive and is not synchronized between PCs. An inaccessible local folder produces a message and a logged startup failure.

On first launch, the local folder is created. Current settings are copied from the previous OneDrive configuration location if available, retaining calibration and unknown settings. Migration tries its valid main file and backup, then executable-local `appsettings.json`, its backup, shipped `appsettings.defaults.json`, and built-in defaults in that order. An existing local configuration or backup always takes precedence. The old OneDrive file is left in place and is no longer monitored. Files beside the executable are migration/default templates; edit the local application data file to change running behavior. Builds and deployment never overwrite this local file. Relative media and telemetry directories remain relative to the executable. Camera and the original Experience retain their own configuration behavior.

File changes reload after a 75 ms debounce using background file I/O, with a one-second polling fallback. Another process can update this local file to apply configuration changes. Invalid edits retain the previous valid settings. UDP port changes bind the replacement listener before releasing the old one. `DeveloperMode` and telemetry settings require restart.

| Setting | Default | Purpose |
|---|---|---|
| ConfigurationVersion | 4 | Configuration schema version; old files are upgraded atomically, unsupported newer versions are rejected |
| DefaultDisplayName | empty | Display used by commands without a display; empty means primary |
| DisplayAliases | {} | User-managed alias-to-Windows-display map; aliases work in commands and DefaultDisplayName |
| DefaultAudioDevice | empty | Audio endpoint name or ID used by commands without a device; empty means system multimedia default |
| TransitionInSeconds | 0.5 | Video opacity fade-in duration |
| TransitionOutSeconds | 3 | Video opacity fade-out duration |
| AudioTransitionInSeconds | 0.5 | Standalone audio volume fade-in duration |
| AudioTransitionOutSeconds | 3 | Standalone audio volume fade-out duration |
| VideoDirectory | Media | Local video directory, relative to the executable or absolute |
| AudioDirectory | Media | Local audio directory, relative to the executable or absolute |
| UdpEventsPort | 54242 | IPv4 UDP events destination/listen port |
| UdpCommandsPort | 54243 | IPv4 UDP commands destination/listen port |
| DeviceName | empty | Computer alias; blank uses hostname; applies live |
| UdpBroadcastAddress | 255.255.255.255 | Broadcast destination for senders |
| UdpInterfaceAddress | empty | Dedicated LAN's local IPv4 address; empty broadcasts on every active IPv4 subnet |
| UdpSendAttempts | 3 | Identical transmissions per request, 1â€“5 |
| UdpRetryDelayMilliseconds | 75 | Delay between attempts, 20â€“1000 ms |
| RequestLifetimeSeconds | 10 | Local processing budget from receipt, up to 300 seconds |
| DeveloperMode | false | true: separate 800Ã—600 ordinary windows centered on each display, without desktop transparency or topmost enforcement |
| configurationMode | false | Enable mouse and keyboard calibration immediately, without restarting |
| PointHitDiameterPercent | 5 | Point selection diameter as a percentage of the display diagonal |
| DisplayScreens | {} | Ordered named screens per physical display; newly detected displays receive screen1 |
| CalibrationGridEnabled | true | Show a test grid, display/alias label, coordinates, and keyboard help while configurationMode is on |
| RequestDeduplicationSeconds | 60 | Suppress repeated IDs per logical source; must cover request lifetime, maximum 3600 |

Fade durations range from zero (immediate) to 3600 seconds. Each video starts with shader alpha exactly zero; fade-in begins only after playback position advances. Video keeps advancing throughout fade-out. At completion the renderer presents a transparent swap-chain buffer, waits for DirectComposition commit completion and DWM consumption, and then closes the Media Engine. Fade-out starts early enough to complete before the natural end; short experiences shorten the fade. Audio fades are applied per sample, with normal device buffering latency. Video and audio settings are independent. Legacy `DisplayName` is read as `DefaultDisplayName` when the new keys are absent.

Available display names, audio device names/IDs/channel counts, selections, request IDs, playback replacement, completion and failures are logged in machine-prefixed JSONL files under `logs/ExperienceX`. No Windows Event Log is used. DirectXPlaybackPerformance reports unique transferred video timestamps, present submissions, DXGI frame statistics when available, and Media Engine rendered/dropped frame counters. These are distinct measurements; PresentMon captures actual display events for the sampling interval.

## Transparency and keystone

Screen chroma key and display background transparency are independent. A video screen can include its own `BlackKey` object (`Enabled`, `Threshold`, `Softness`); absent/null uses the application's root `BlackKey` defaults. T creates a screen override using the current threshold and softness, then toggles only its enabled state. Remote edits to screen overrides apply live. Keyed video pixels reveal underlying screens or the display background; the desktop is visible through them when the display background is also transparent. B controls the entire window background, including outside every screen's points. No shader compilation is needed when changing these settings.

`BlackKey.Enabled` defaults to true. The final video surface, including letterboxing and keystone backing, is keyed by a pixel shader. `Threshold` (default 0.015) removes colors whose brightest RGB channel is at or below that value; `Softness` (default 0.03) softens the edge. Values are normalized 0â€“1 and must total at most 1. Lower values retain more dark detail. Production mode gives a borderless transparent surface; developer mode retains normal window chrome. The same shader multiplies premultiplied color and alpha for fades while native window opacity stays at 1, keeping the media render path active even at zero visible alpha. Disabling black key bypasses color removal while retaining shader fades. The compiled Direct3D shaders are embedded; normal builds need no shader compiler. Use `Compile-Shaders.ps1` after editing HLSL.

Each `DisplayScreens` entry is an ordered array of screens, with stable `Id`, editable `Name`, `Type:"video"` or `Type:"mat"`, and shared `Points` containing `Enabled` and a clockwise `Vertices` array. Coordinates are normalized X/Y from 0 to 1 over the physical display. Video requires a convex quadrilateral; a mat can have any number of points (at least three), including inward bends, with no crossed edges. `Enabled` controls video's projective correction; a mat always follows its points. Screen names are unique per display (case insensitive), 1–80 characters, without slashes or colons. Definition order controls overlap selection and ordering within each type. All mats render over videos, with editing overlays above the mats. An empty array means zero screens and is retained. Newly detected displays receive `screen1`, full size, plus black/opaque background defaults. Version 4 migrates old `Keystone` and named corner fields to `Points.Vertices`, and `KeystoneHitDiameterPercent` to `PointHitDiameterPercent`, preserving coordinates, enabled states and unrelated settings.

In configuration mode, click inside a screen or near one of its points to select it. Overlapping interiors cycle through screen definition order; overlapping points cycle through screen order then UL, UR, BL, BR. The selected screen has a mostly transparent blue fill and its name centered in larger, darker blue Segoe UI text. Click that name to edit; Enter or Tab commits, Escape cancels, and clicking elsewhere cancels. Single-letter application shortcuts pause during text editing. New names save atomically and apply immediately without interrupting video. **A followed by V** creates and selects a video screen on the selected screen's display, or the configured default display when none is selected. The first screen fills the display; later screens are centered at 50% width and height. Screens remain attached to their display.

Every video screen has a faint blue polygon fill at 4% opacity throughout configuration mode, even while idle and with the calibration grid disabled. Selecting it increases the fill to 16% opacity. The fill follows the screen's points and disappears when configuration mode exits; it does not change the saved display background or chroma key. Unselected mats keep their normal opaque black appearance.

Press **A** to show the available screen types, then **V** for video or **M** for mat. **N** opens the same choices. **A followed by M** (or **N followed by M**) creates and selects a mat on the selected screen's display, or the default display with no selection. Its initial size and naming match new video screens: full display if it is the first screen, otherwise centered at 50% width and height. A mat stays opaque black while idle and during all video transitions, independently of background transparency and video chroma key. It receives no playback commands. Drag its points or interior, rename it, reset it with R, undo saved point edits with Z, or delete the selected mat with Delete, using the same configuration controls as video.

Double-click a mat's perimeter line (within 8 pixels) to insert a point on that edge and save immediately. Double-clicking an existing vertex does not duplicate it. Added points can be dragged into concave shapes; crossing edges or a collapsed shape are prevented. Insertion and dragging can be undone with Z. Overlapping mat edges cycle in screen definition order. Point editing remains available while no video is running. Four-point screens retain UL, UR, BL, BR keyboard/overlap order; polygons with additional points use their clockwise array order. All points use the same red/azure orbs and white save confirmation, including points beyond number 32.

Video commands use `screen:"Projector/screen1"`. Omitting screen uses the first video screen on the default display; the optional displayName selects that display's first video screen. `screen:"*"` targets all video screens on all connected displays; `Projector/*` targets all video screens on that display. `*/screen1` targets each display's video named screen1. Wildcards exclude mats and disconnected displays and do not create screens. Explicit mat and unknown targets are rejected. This version implements video and mat screens.

## Looping video

Video requests accept `"loop": true`, default false. The [Media Engine's native looping](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-setloop) repeats locally without reopening the decoder or restarting the fade-in. There is no fade between cycles. Loop-boundary smoothness still depends on the media and decoder; this does not promise gapless presentation.

```json
{"device":"*","source":"controller","requestId":"loop-playback-id","experience":"video","value":"ping.mp4","screen":"*","mute":true,"loop":true}
```

Controller automatically sends the following command when the owning scene changes. The sender must match the original playback's `source`; the stop has a fresh `requestId` and references the original in `playbackRequestId`. This stops only matching video instances across the receiving device, without depending on display/screen names that might have been edited. Audio and newer replacements continue.

```json
{"device":"*","source":"controller","requestId":"new-stop-id","experience":"video","command":"stop","playbackRequestId":"loop-playback-id"}
```

Stops use `TransitionOutSeconds` (currently three seconds), retain playback/decoding during the fade, and close only after a transparent frame is presented. Duplicate stops do not extend the fade. Stops received before their corresponding play are remembered for `RequestDeduplicationSeconds` in a bounded cache, preventing reordered requests from starting an already stopped loop within that window. Playback already replaced or stopped ignores a late stop. Use `Send-Experience.ps1 -Loop` for a standalone loop; automatic scene ownership applies to loops started by Controller playlists.

## Controller media discovery

At startup, ExperienceX broadcasts `command:"discover", service:"controller"` on the command port. Controller answers on the event port with its Windows hostname and the discovery request ID. ExperienceX copies files directly in `\\hostname\media` into its configured local video and audio directories, once per distinct destination; subfolders are skipped. Playback and rendering continue while the copy runs in the background. The share needs normal Windows file-sharing access; configure this once with Controller's `Install-Controller.cmd`.

`MediaDiscovery` defaults to `{ "Enabled": true, "Attempts": 3, "RetryIntervalSeconds": 5 }`. Valid edits apply live; enabling discovery or changing its settings or routing starts a new discovery round. Use local media directories for caching. Downloads replace files atomically, so partial copies never become playback files. Existing extra/local files remain. Changed files previously downloaded from the same Controller can be updated; conflicting files from different Controllers retain the first copy and log `MediaFilenameConflict`. This is startup synchronization, rather than a continuous remote-file watcher. Share/access failures are logged and do not stop the application.

```json
"DisplayScreens": {
  "\\\\.\\DISPLAY2": [
    {"Id":"screen1","Name":"screen1","Type":"video","Points":{"Enabled":false,"Vertices":[{"X":0,"Y":0},{"X":1,"Y":0},{"X":1,"Y":1},{"X":0,"Y":1}]}},
    {"Id":"mask1","Name":"mask","Type":"mat","Points":{"Enabled":true,"Vertices":[{"X":0.25,"Y":0.25},{"X":0.75,"Y":0.25},{"X":0.75,"Y":0.75},{"X":0.25,"Y":0.75}]}}
  ]
}
```

Press **Ctrl+Shift+Alt+C** to enable configuration mode globally while ExperienceX is running, including when Camera or another app has focus. Press **C** without modifiers to leave configuration mode. The change is merged into the local configuration and applied immediately; a brief ON/OFF notice confirms it. Held-key repeats and overlapping toggle operations are ignored. If another application has registered the shortcut, a notice and `ConfigurationHotkeyUnavailable` log entry explain that it could not be registered. The shortcut is released on shutdown. You can also set `"configurationMode": true` directly in the configuration file. Press and hold near a corner, then drag; the pointer retains its initial offset so near-corner clicks do not snap the point. Selection diameter is `PointHitDiameterPercent / 100 Ã— sqrt(widthÂ² + heightÂ²)`; at 1920Ã—1080 the default diameter is approximately 110 pixels (55 pixel radius). Overlapping hits cycle UL, UR, BL, BR on successive clicks. All displays reveal their own pulsing orbs and stationary azure perimeter lines; only the selected point is azure, with a numbered indicator. Other points are red. Overlays also work while no video is playing. Points stay inside the display and cannot cross or collapse the quadrilateral.

All point orbs and their perimeter lines stay visible on every display throughout configuration mode, before clicking and after mouse release or cancellation, even with the grid disabled. Only the selected orb is azure; unselected orbs are red. Releasing the mouse saves a changed calibration and enables its correction. A local white flash around the edited corner confirms a successful save while the orbs remain visible. A failed save produces a local red flash and a log entry. Clicks without a coordinate change do not write or flash. Tab/Shift+Tab selects UL, UR, BL, BR; arrows move one pixel, Shift+arrows ten pixels, Enter saves, and Escape cancels. Disabling configuration mode immediately cancels editing and removes the overlay.

Hold and drag inside a screen, away from its point selection areas, to move all points together without changing its shape. The screen stays inside its own display and saves on release; overlapping screens still cycle in definition order on each press. A click on the selected screen's name edits the text, while dragging from the name moves the screen. Escape cancels a drag, and Z can undo a saved move. A full-size screen must first be made smaller before it can move within its display.

With an ExperienceX window focused in configuration mode, Delete removes the selected screen and stops only that screen's video. The removal is saved immediately; deleting the last screen leaves that display with zero screens, including after restart. Delete has no screen-removal action with nothing selected or while a name is being edited. A concurrent remote change to the selected screen cancels deletion rather than overwriting the remote edit.

The test grid and its center circle work without a video and follow the active correction. Display aliases, raw Windows name, resolution, normalized coordinates, and pixel positions appear on each display. Set `CalibrationGridEnabled` to false to hide the grid/readout while retaining corner editing. R resets only the selected screen to the identity rectangle with correction disabled and saves immediately. Z first cancels a pending draft; otherwise it restores and saves the previous local calibration for that display. Up to 20 confirmed local edits are retained in memory. Remote coordinate/enabled changes clear that display's undo history; restarting clears all undo history. A reset can therefore be undone immediately.

Remote configuration changes apply live. A change to the display being edited cancels its local draft so the remote coordinates win. Saving merges only that display's known fields into the latest configuration, preserving unrelated settings and other displays. The complete validated JSON is flushed to a temporary file in the same directory, then atomically replaces the original. `appsettings.json.bak` retains the previous valid file. Startup restores a valid backup when the main file is missing/corrupt and retains corrupt content in a timestamped `.rejected-*` file. Invalid or incomplete live editsâ€”including an empty fileâ€”retain the last valid settings until corrected, without overwriting the external edit.

The backup, lock, and temporary files are beside the local configuration. For coordinated read/merge/write, acquire `appsettings.json.lock` with `FileShare.None`, re-read the latest JSON while holding it, and release after replacement. Normal in-place writers are excluded during the application's merge; changed file replacements are checked and retried before commit. An uncoordinated external replacement can still race the final check, so cooperating writers should use the same lock.

An accepted remote calibration change also produces the local white confirmation flash around each changed corner on its own display, even with `configurationMode` off. An `Enabled`-only change flashes all four corners. Initial defaults, unchanged coordinates, and unrelated configuration changes do not flash. Local saves are not flashed twice when the file watcher reads their changes. `PointsRemoteChangeApplied` logs accepted remote changes; rejected configuration does not produce a success flash.

Success flashes wait for the renderer to acknowledge applying and presenting the expected geometry and for the settings to remain current. A failed renderer, superseded revision, or failed reload cannot produce a success flash. The acknowledgement times out after three seconds; rendering failures are logged. This confirms the renderer's submitted surface, not physical projector output.

Topmost enforcement raises without taking focus when playback starts/media opens, reacts to window-order changes, and checks once per second. It ignores cloaked windows and coalesces events; repeat successes are summarized in logs. This is best effort among desktop apps and does not cover secure desktops.

## Verification

ExperienceX is isolated from Experience: its executable, mutex, logs, and default UDP port are distinct. Camera and ExperienceX use UDP 54242 for events and UDP 54243 for commands; ExperienceDeviceName selects the receiving computer alias or hostname. No C++ rewrite is required for this prototype.

Run `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj` for routing, protocol, sample isolation, fades, duration and configuration regression checks. With ExperienceX stopped, `experiencex/Verify-Outputs.ps1` exercises all currently connected displays and active audio devices with silent audio and muted video, then restores configuration and leaves ExperienceX and Camera running. It verifies endpoint initialization, not audible output quality or physical cable routing. Hardware with more than two channels still requires physical commissioning.

With ExperienceX stopped, run `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj -- --keyboard` to isolate keyboard checks. These verify registration conflicts and inject the actual entry/letter keys through the production WPF dispatcher, including switching modes and releasing the shortcuts. Keyboard checks also run in the fast suite.

Run `./ExperienceX.Tests/Scripts/RestartLauncherChecks.ps1` with ExperienceX stopped to verify the restart CMD using an isolated executable fixture. It checks process replacement, configuration/media retention, source-file unlocking, and rejection of overlapping or redirected cleanup paths. Every Media folder is excluded from Git and must be supplied separately on a fresh checkout. Project icons are optional when local media is absent.

`Measure-Playback.ps1 -Application ExperienceX` samples CPU/GPU and DirectX timing; use `-Application Experience` for the original baseline, and `-PresentMonPath` for display event capture. See `benchmark-results/DIRECTX-REPORT.md` for the comparison and limitations. Audio retains NAudio/WASAPI. Video uses Media Foundation Media Engine with a D3D11 device manager, GPU texture transfers, a pixel shader, and a premultiplied-alpha DirectComposition flip swap chain. Rendering runs on a dedicated thread per display. WPF remains only for the application dispatcher.

## Build and verification

Stop existing apps before building: `./experiencex/Stop-Applications.ps1`, then `dotnet build experiencex/ExperienceX.csproj`. Output is copied to the configured OneDrive Bin/ExperienceX folder. Run fast regression tests with `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj`, all tests including GPU readback/recovery with `dotnet run --project ExperienceX.Tests/ExperienceX.Tests.csproj -- --all`, or hardware tests alone with `-- --hardware`. Tests reference the built application assembly. Run `./experiencex/Verify-Outputs.ps1 -EnableKeystone` with ExperienceX stopped for live display, replacement, fade, audio and invalid-request checks; it restores runtime configuration afterward. Tests use muted video and silent audio.

`./experiencex/Verify-CalibrationReload.ps1` checks automatic per-display configuration, live configuration-mode changes, and remote coordinate updates through file replacement. It restores the previous runtime settings and records timings in `.tools/calibration-live-results.json`.

`./experiencex/Verify-CoordinatedAudio.ps1` requires at least three active endpoints and ExperienceX stopped. Silent live tests cover arrays/comma lists, overlapping groups, endpoint deduplication, request retry suppression, partial availability, live virtual-group/default edits, configured latency, and natural completion. It restores the configuration and records endpoint timing estimates in `.tools/coordinated-audio-verification.json`. Fast tests also simulate drifting devices at several sample rates and test bounded shared decoding/resampling.

Compiled shaders are checked in, so normal builds require no shader compiler. After changing `Shaders/Video.hlsl`, run `./experiencex/Compile-Shaders.ps1` with a Windows SDK installed, then rebuild and rerun GPU tests. Dependencies are pinned to Vortice 3.8.3 and NAudio 2.2.1.

Device initialization or device-loss failure hides the affected surface and records the error. The window automatically recreates its DirectX/DirectComposition/Media Foundation renderer, retrying after 1, 2, 5, 10, then at most once every 30 seconds until initialization succeeds. A disconnected display remains hidden. Successful recovery restores an idle window with its configured background; interrupted videos are not replayed automatically, and requests received while recovering are rejected. Send a new request ID once ready. Other displays and standalone audio continue independently. Worker shutdown waits at most five seconds per renderer, logs a timeout, and does not reuse an HWND while its old worker remains alive. This build targets x64 Windows with the .NET 9 Windows Desktop runtime and a Direct3D 11 video-capable GPU; there is no software-renderer fallback.

Renderer work retains only the newest pending configuration, geometry preview, size, playback, and overlay commands, with at most 16 command categories. This bounds queue growth and avoids opening obsolete clips during bursts. Points inverses are cached until geometry changes; `TransformComputations` in performance telemetry is cumulative for that renderer instance. Label textures update only when their text changes and at most ten times per second during dragging. `Verify-PlaybackPerformance.ps1` exercises simultaneous muted playback and records CPU/transform counts in `.tools/improvements-performance.json`. Regression tests include GPU readback, injected device removal and actual window recovery, atomic save/backup recovery, request/preview floods, persisted reset/undo, and save-confirmation races.



To benchmark video concurrently on every connected display, run `./experiencex/Measure-AllDisplays.ps1 -SampleSeconds 20`. It tests plain video, black key, and black key plus keystone, captures per-display decoder counters and per-swap-chain presentation data, and restores runtime configuration. See [all-display results](benchmark-results/ALL-DISPLAYS-REPORT.md).
