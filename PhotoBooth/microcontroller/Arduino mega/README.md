# Arduino Mega break-beam inputs

Open `SaftMega/SaftMega.ino`. This sketch reports three independent sensors to
Controller over USB serial; Controller broadcasts their events to the network.
Controller discovers it automatically on any USB serial port. COM10 is needed
only to select the upload port on SAFTTHING1, not for normal operation.

## Wiring and configuration

| Input | Receiver white signal wire |
| --- | --- |
| `beam1` | D22 |
| `beam2` | D24 |
| `beam3` | D26 |

These pins occupy consecutive positions in one row of the Mega's D22–D53 header.
The pictured receiver labels red as +5 V and black as GND. Power each matching
transmitter according to its own specification and share ground with the Mega.

Settings are together at the top of the sketch:

- `deviceName`: `mega1`; the announced alias used by commands and triggers.
- `deviceId`: `saft-mega-001`; a stable identifier unique to this physical board.
- `serialBaudRate`: 115200, 8N1.
- `debounceMilliseconds`: **20**, independently applied to every input.
- `announcementIntervalMilliseconds`: **2000**, independent of sensor activity.
- `inputMode`: `INPUT_PULLUP`, for an output compatible with a pull-up to 5 V.
- `beams`: input name, pin and electrical level that means broken.

The photograph identifies the output as NPN but does not establish its active
polarity or full electrical specification. LOW initially means broken. Confirm
this with a real sensor before enabling triggers; change that sensor's
`brokenState` to HIGH if the two events are reversed. Confirm the receiver output
supports the selected pull-up. Leave unused inputs unconnected with pull-ups.

All sensor debouncing is inside this sketch using
[Bounce2 2.71](https://github.com/thomasfredericks/Bounce2/tree/v2.71), with its
default **stable interval** algorithm. Do not enable Bounce2 lock-out or prompt
detection algorithms. No custom replacement debounce algorithm is used.
Changing settings requires uploading the sketch again.

## Behavior

At startup each input establishes a stable initial state without reporting a
sensor event, including when its beam is already broken. The board announces its
identity and capabilities at startup, then every two seconds, independently of
whether the sensor baselines have stabilized yet. It reports one
`beam.broken` or `beam.restored` event per debounced transition. A held beam does
not repeat; another input continues to operate independently. Noise or a pulse
shorter than the stable interval is filtered in firmware.

The loop has no deliberate delays. UART transmission and command reception are
nonblocking and bounded so announcements do not pause sensor sampling. Up to 16
sensor transitions wait for transmission; if sustained traffic exhausts that
queue, `device.status` reports a cumulative `droppedEvents` count. The 20 ms
setting is a minimum stability requirement, not a hard real-time delivery
guarantee. There are no event retries or playback acknowledgments.

Output is one UTF-8 JSON object per line, with no serial debug text:

```json
{"type":"event","device":"*","source":"mega1","requestId":"mega-1-1","input":"beam1","event":"beam.broken","data":{"pin":22,"broken":true}}
```

The boot counter and event sequence prevent ordinary resets from reusing request
IDs during Controller's duplicate-retention window. EEPROM bytes **0–3** are
reserved for the boot counter; they are updated only on startup or sequence
rollover, not per event. Clearing/replacing EEPROM resets this identity history.
Give each additional board a distinct `deviceName` and `deviceId`. Duplicate
names or IDs block routing on both conflicting ports until the conflict clears.

## Compile and upload

Controller builds also ship these source files under
`%OneDrive%\Home Technologies\Bin\Controller\Microcontroller\ArduinoMega`.
The sketch still requires a separate upload on the computer attached to the Mega;
restarting Controller does not upload or change board firmware.

In Arduino IDE install **Arduino AVR Boards**, select **Arduino Mega or Mega
2560 / ATmega2560**, and install **Bounce2 by Thomas Ouellet Fredericks** through
Library Manager (2.71), plus **ArduinoJson by Benoit Blanchon** (7.4.3). Select COM10 on the computer
where the Mega is connected. Close Controller/Serial Monitor before uploading;
only one process can own the serial port.

Arduino CLI can use the checked-in profile to obtain the pinned dependencies:

```powershell
arduino-cli compile --profile mega ".\SaftMega"
arduino-cli upload --profile mega --port COM10 ".\SaftMega"
```

For commissioning, first use Serial Monitor at 115200 baud, then restart the
board. It should announce at startup and every two seconds,
without sending any startup `beam.broken`/`beam.restored` events. Block and restore each
beam; verify its input name, event polarity, and that holding a beam emits no
repeats. Close Serial Monitor before Controller connects.

## Controller connection

No per-board entry is required. Controller uses these defaults, including when
`ArduinoDiscovery` is omitted from an existing hostname configuration:

```json
"ArduinoDiscovery": { "Enabled": true, "BaudRate": 115200 },
"Arduinos": []
```

Controller inventories present **USB-backed** serial ports every second,
including ports already present at startup. It follows PnP ancestors to include
USB bridges without probing Bluetooth/PCI serial ports. It opens a candidate at
the configured baud rate, and registers it only after a `saft.serial` version 1
announcement. A candidate without a compatible announcement is released after
five seconds and not retried for 30 seconds unless unplugged/replaced. Busy
ports are left to their current owner. Opening with DTR may reset the Mega;
startup suppression applies again. `ArduinoDiscovered` confirms registration.
Registrations are runtime state, never hostname-configuration rewrites.

Explicit `Arduinos` entries remain available as manual overrides; their ports
are excluded from automatic discovery, including disabled entries. They can
deliberately select a non-USB port, while automatic discovery remains USB-only.

The announcement contains the ordinary event envelope plus:

```json
{
  "input": "device",
  "event": "device.announced",
  "protocol": "saft.serial",
  "protocolVersion": 1,
  "deviceId": "saft-mega-001",
  "inputs": [
    { "name": "beam1", "kind": "breakBeam", "events": ["beam.broken", "beam.restored"] },
    { "name": "beam2", "kind": "breakBeam", "events": ["beam.broken", "beam.restored"] },
    { "name": "beam3", "kind": "breakBeam", "events": ["beam.broken", "beam.restored"] }
  ],
  "commands": ["discover", "status"]
}
```

Supported read-only commands are `discover` (send an announcement) and `status`
(send `device.status` with debounced input states, uptime, debounce interval,
and dropped-event count). There are no relay/output-control actions yet.
Commands require the normal command envelope and target `mega1` by name.
They can come from a playlist with `Transport: "arduino"`, or a named UDP
command bridged by the attached Controller. A network wildcard command is not
forwarded to every serial device. Announcements are consumed locally for
registration; status responses and sensor events are bridged to UDP unchanged.
Unsupported commands and unadvertised sensor events are rejected.

Example playlist command (valid even while the board is disconnected):

```json
{ "Transport": "arduino", "Device": "mega1", "Payload": { "command": "status" } }
```

Controller does not debounce these sensor events again. Network request-ID
deduplication prevents an echoed copy of the same event from executing twice;
that is distinct from sensor debounce. Existing trigger busy rules and playlist
cooldowns still apply. Set an event trigger's `CooldownMilliseconds` explicitly
to **0** if no post-playlist cooldown is wanted; omitting it inherits the
Development/Production cooldown. A running trigger cannot overlap itself.

Triggers match `SourceDevice: "mega1"`, `Input: "beam1"` (or beam2/beam3), and
`Event: "beam.broken"` or `"beam.restored"`, then run a configured playlist.
No sensor-to-playlist assignments are inferred from the announcement; configure
those explicitly in the existing trigger editor. Automatic hardware registration
does not automatically fire a trigger or add/edit playlists.
