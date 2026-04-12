# AppState Machine

```mermaid
stateDiagram-v2
    [*] --> Idle : Window_Loaded

    Idle --> Countdown : bCapture_Click\nSerial LONG\nbGo_Click
    Idle --> Video : bVideo_Click\nSerial TAP\nSerial EVENT:TRIGGER\nSerial BreakBeamTriggered

    Countdown --> Countdown : _countdownTimer tick\n[counter > 1]\ndecrement counter
    Countdown --> Capture : _countdownTimer tick\n[counter == 1]

    Capture --> Preview : CaptureSnapshot()\n? _holdTimer elapsed\n? OnCaptureComplete()\n? FlashOutSmooth.Completed

    Preview --> Idle : _previewFallbackTimer elapsed\n? OnPreviewDoneMarker()\n? RestartWindow()

    Video --> Idle : videoControl_MediaEnded\n_videoTimer elapsed\nPlayLocalVideo() file not found
```

## State Descriptions

| State | Description |
|---|---|
| `Idle` | Live camera feed shown via `_previewLoop`. All UI reset. Awaiting trigger. |
| `Countdown` | `_countdownTimer` ticks every 1s, decrementing `_counter` from `CountdownSeconds` (6) down to 1. Each tick plays `CountdownNumberInOut` animation. |
| `Capture` | Single frame cloned from `_frameFull`, saved to PNG, displayed in `imageControl`. `_holdTimer` delays before flash-out. |
| `Preview` | Captured image shown with horizontal flip. `_previewFallbackTimer` counts down `_previewSeconds` before restarting. |
| `Video` | `videoControl` (MediaElement) plays `media\video.mp4`. Optional `_videoTimer` enforces `_videoDurationSeconds` cap. |

## Timer Lifecycle per State

```mermaid
stateDiagram-v2
    state Countdown {
        [*] --> counting : _countdownTimer starts\n(1s interval)
        counting --> counting : tick [counter > 1]
        counting --> [*] : tick [counter == 1]\n_countdownTimer.Stop()
    }

    state Capture {
        [*] --> holding : _holdTimer starts\n(HoldSeconds)
        holding --> [*] : elapsed ? OnCaptureComplete()\nFlashOutSmooth begins
    }

    state Preview {
        [*] --> waiting : _previewFallbackTimer starts\n(PreviewSeconds)
        waiting --> [*] : elapsed ? OnPreviewDoneMarker()\n? RestartWindow()
    }

    state Video {
        [*] --> playing : _videoTimer starts\n(VideoDurationSeconds, if > 0)
        playing --> [*] : elapsed or MediaEnded\n? SetState(Idle)
    }
```

## Flash Animation Sequence (Capture flow)

```mermaid
sequenceDiagram
    participant CT as _countdownTimer
    participant CS as CaptureSnapshot()
    participant HT as _holdTimer
    participant SB as FlashOutSmooth
    participant SS as SetState

    CT->>SS: SetState(Capture)
    SS->>CS: CaptureSnapshot()
    CS->>CS: Clone frame, save PNG\ndisplay imageControl
    CS->>HT: start (_holdSeconds)
    HT-->>CS: Tick ? OnCaptureComplete()
    CS->>SB: Begin FlashOutSmooth
    SB-->>SS: Completed ? SetState(Preview)
```

## Trigger Sources ? State Transitions

| Trigger Source | Event / Value | Resulting Transition |
|---|---|---|
| Button | `bCapture_Click` | `Idle ? Countdown` |
| Button | `bVideo_Click` | `Idle ? Video` |
| Serial | `EVENT:TRIGGER` | `Idle ? Video` |
| Serial | `DEBUG: ManualButtonEvent: TAP` | `Idle ? Video` |
| Serial | `DEBUG: ManualButtonEvent: LONG` | `Idle ? Countdown` |
| Serial | `DEBUG: BreakBeamTriggered: YES` | `Idle ? Video` |
| Internal | `_countdownTimer` reaches 1 | `Countdown ? Capture` |
| Internal | `_holdTimer` + `FlashOutSmooth` | `Capture ? Preview` |
| Internal | `_previewFallbackTimer` | `Preview ? Idle` (via RestartWindow) |
| Internal | `_videoTimer` | `Video ? Idle` |
| MediaElement | `MediaEnded` | `Video ? Idle` |

## Configuration (appsettings.json)

| Key | Default | Controls |
|---|---|---|
| `FlashInSeconds` | `1.0` | `FlashInFast` storyboard duration |
| `FlashOutSeconds` | `2.0` | `FlashOutSmooth` storyboard duration |
| `HoldSeconds` | `0.3` | `_holdTimer` interval (Capture ? flash-out delay) |
| `PreviewSeconds` | `10.0` | `_previewFallbackTimer` interval |
| `VideoDurationSeconds` | `33.0` | `_videoTimer` cap (`0` = play full file) |
