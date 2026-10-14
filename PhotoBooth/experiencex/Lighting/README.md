# LED sampling screens

Controller owns media, pixelArray and pixelMatrix perspectives. Each LED perspective selects one whole existing WLED segment and is independent of media files. Controller discovers `_wled._tcp.local` via mDNS (UDP 5353), reads `/json`, and stores the verified device MAC and segment ID with the address and dimensions. Manual hostname/IP entry is available. Definitions use the existing general revision announcement and bounded HTTP download, not a large UDP message.

## Controller configuration

In configuration mode open **Perspectives**, then **A,L** or **LED settings**. Choose a WLED device, segment and array/matrix type. Matrix dimensions come from the WLED 2D segment; array PixelCount is the number of pixels in the whole segment. Each perspective has FramesPerSecond (1–120, default 30) and SamplingThicknessPercent (>0–100, default 10; relevant only to arrays). Completed saves announce immediately. Existing LED perspectives retain their ID and type. Discovery and editing never change WLED configuration.

Example member of Controller's `Perspectives` array:

```json
{
  "Id": "92cdcc28bf5948b5a490bc62c83ad9559",
  "Name": "Stairs",
  "Type": "pixelArray",
  "Rectangle": { "X": 0, "Y": 0, "Width": 100, "Height": 100 },
  "Led": {
    "Address": "192.168.1.106",
    "DeviceMac": "aabbccddeeff",
    "DeviceName": "Stairs WLED",
    "SegmentId": 0,
    "PixelCount": 150,
    "Width": 0,
    "Height": 0,
    "FramesPerSecond": 30,
    "SamplingThicknessPercent": 10
  }
}
```

For pixelMatrix use Width and Height from WLED and PixelCount = Width × Height. Rectangle remains a media-only crop field; LED placement belongs to Experience. A new type requires a new perspective/screen.

## Experience placement and sampling

**A,L** opens a selector containing only Controller LED perspectives. The screen's Type is `fastled`; it caches its perspective and uses the existing screen IDs/names. A definition is intended for one screen, Experience instance and display; no ownership leases or exclusivity checks are added. All sampled segments on one WLED device must be driven by the same Experience instance.

Arrays have two endpoints: first and last pixel. Drag an endpoint or the visible sampling band. Total band thickness is SamplingThicknessPercent / 100 × max(display pixel width, display pixel height), centered on the line. On 1920 × 1080, 10% gives a 192-pixel geometric band. Parts outside that display are black and never sample a neighboring display.

Matrices have four rectangle corners, UL/UR/BL/BR keyboard order. Drag the body to move, a corner to resize, or the selected screen's circular rotation handle to rotate. Resizing retains orientation and the opposite corner. Arrays and matrices participate in existing point snapping/shared-point groups; matrix corners resize their coupled rectangle and a snap is accepted only when every joined shape remains valid. R resets full display placement (array: centered full-width line); Z undoes; Delete on the selected body removes; N renames. Mouse release saves atomically; Enter saves keyboard drafts. LED screens neither bind by media name nor receive playback commands.

LED feedback remains live in configuration mode. Guides render in a separate capture-excluded DirectComposition HWND, leaving media, mats, desktop/background and G-Force visible to capture. Outside configuration mode LED screens have no visible output of their own. One desktop bitmap is captured per due display and shared among its LED screens. Each screen samples at its configured cadence; the device stream runs at the highest requested rate and retains slower screens' latest colors.

Axis-aligned sampling matches Jinx Smooth: Windows GDI SetStretchBltMode(HALFTONE), StretchBlt to LED dimensions, then RGB24 extraction. No temporal smoothing, additional gamma or saturation change is applied. Rotated regions are first rectified with PlgBlt at source resolution, then downsized with HALFTONE. Rotation extends Jinx's axis-aligned behavior; no byte-identical GPU averaging claim is made.

## WLED output and recovery

RGB24 DDP uses unicast UDP 4048, 1440-byte RGB payloads, byte offsets, wrapped sequence numbers and PUSH only on the last packet of a composed device frame. Segment IDs become global logical pixel indices; matrix subrectangles are written row by row using the device matrix width. Physical wiring/LED maps remain in WLED. Unassigned/removed segments are black while another sampled segment continues. Last-screen removal, display disconnection or app shutdown stops sending; WLED resumes configured behavior after its realtime timeout. There are no acknowledgements or cross-device synchronization guarantees.

Before saving a perspective and before output, `/json/cfg` is checked: Receive UDP realtime on, Use main segment only off, Respect LED Maps on when supported, realtime offset zero, ordinary DMX address 1, and a finite positive realtime timeout below 65000 ms. Incompatible settings are reported, never changed automatically. Missing devices retry; mDNS/MAC matching recovers changed IPs without accepting different hardware. Missing/resized segments stop that screen and log a mismatch until its Controller definition is refreshed. Removed or changed-type definitions retain screen geometry but are marked unavailable and stop streaming. Offline controllers retain their cached definitions.

Logs include WledDeviceReady, WledDeviceUnavailable, WledDeviceRediscovered, LedSegmentMismatch, LedSamplingFailed, WledOutputFailed, LedOutputPerformance (capture/send counts plus display/screen/perspective/segment/FPS every ten seconds), and LedOutputStopped. Counters measure capture/sends, not confirmed WLED presentations.

## Tests

```powershell
dotnet run --project ExperienceX.Tests -p:CopyBuildOutputToOneDrive=false -- --led
dotnet run --project Controller.Tests -p:CopyBuildOutputToOneDrive=false -- --wled-discovery
dotnet run --project Controller.Tests -p:CopyBuildOutputToOneDrive=false -- --led-editor
```

Experience tests use temporary configuration and a loopback fake WLED HTTP/DDP receiver. Coverage includes definitions, malformed config/DNS, frame reassembly, segment composition, independent rates, removal/fallback, native smooth/rotated capture and exclusion, A,L creation, dragging/resizing/rotation/undo/deletion. Controller's editor check reads a physical WLED device without modifying it or saving machine config. Run desktop tests sequentially. Physical LED response and sustained performance still require installation-hardware testing. Run updated installers once to include Private/LocalSubnet mDNS UDP 5353; subsequent updates use the existing restart scripts.
