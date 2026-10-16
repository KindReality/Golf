# Software video decoding comparison

Purpose: isolate Experience's Media Foundation acceleration/device-sharing path from the rest of its GPU renderer while investigating a persistent Intel-driver SRW lock wait during `ClearRenderTargetView`. The lock owner and any circular dependency remain unidentified. This option is a diagnostic comparison, not an established fix.

## Setting

```json
"SoftwareVideoDecoding": true
```

Add this top-level property to the **affected computer's** `appsettings.<COMPUTERNAME>.json`, beside `RendererRecovery`, in `%OneDrive%\Home Technologies\Bin\ExperienceX`. Edit only that hostname's file, keep valid JSON, and preserve the rest of its configuration. The default is `false`.

**Restart Experience after changing this setting.** A live edit logs `VideoDecodingRestartRequired` and keeps the effective mode until restart; unrelated live settings still apply. This avoids leaving different open decoders in different modes within a comparison run. Restore `false` and restart to revert. Do not change driver versions, graphics settings, scene configuration, or media at the same time.

## What changes

- `false`: existing behavior. Media Foundation receives the shared DXGI device manager, permitting hardware decode and video processing. Actual acceleration depends on the codec/system; this setting does not prove that a hardware decoder was selected.
- `true`: Media Foundation receives **no DXGI manager**, selecting its software decode/processing path. It transfers each frame to a cached WIC BGRA bitmap. The renderer uploads those pixels into its normal D3D11 texture, then uses the same shaders, chroma key, perspectives, mats, overlays, fades, DirectComposition and GPU presentation.

Software decoding/video processing increases CPU work and introduces CPU-to-GPU frame uploads. The bitmap is reused rather than allocating a full managed pixel array every frame. This removes the decoder's access through our shared graphics-device manager; it does not remove Intel or Direct3D from the display path. A stable software run would implicate the accelerated integration path, not by itself prove a decoder or driver defect. Software mode also changes video processing and transfer, so the comparison cannot isolate decoding alone.

Microsoft documents this behavior for [MF_MEDIA_ENGINE_DXGI_MANAGER](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-dxgi-manager) and supports [TransferVideoFrame to a WIC bitmap](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-transfervideoframe). [UpdateSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource) copies the pixel data before the WIC read lock is released.

## Controlled test on the affected computer

1. Preserve current JSONL logs, dump/JSON pairs, recovery history, hostname configuration, and the matching runtime EXE/DLL/PDB before updating. The automatic updater replaces binaries; new symbols will not resolve old application builds. Do not erase the recovery history or existing evidence.
2. Verify that the OneDrive package matches its manifest and use `Restart-Experience.cmd` to refresh the runtime. Confirm the running executable is `%TEMP%\Experience\ExperienceX.exe`, not the OneDrive executable. Leave Controller's configuration unchanged.
3. Use the same display, single looping `TRex` clip, media settings, driver, other running applications and recovery settings for both runs. First record the normal `false` baseline if needed; then set `true` and restart. Record exact start/end times and PIDs. Run long enough to exceed the earlier typical time to failure several times. A one-minute test cannot establish long-term stability.
4. Confirm `ConfigurationLoaded.Data.SoftwareVideoDecoding` and `VideoOpening.Data.SoftwareVideoDecoding` are `true`. `VideoOpening.Data.FrameTransfer` must be `SoftwareWicUpload`; the normal mode is `DxgiSurface`. Performance records and per-screen health/dump JSON also identify the effective setting. Save these records with the result.
5. Observe video position, transferred frames and presentations, not just `Playing`. If selection appears frozen, check click/drag logs and the UI thread stack as well. Record CPU, GPU Video Decode/3D and memory usage. Do not infer decoder use solely from the configured mode.
6. On a freeze, preserve the new dump/JSON/logs plus matching module files. Check the exact operation (`TransferSoftwareVideoBitmap`, `UploadSoftwareVideoTexture`, `ClearDisplayTarget`, etc.) and whether the same Intel/SRW stack recurs. Capture all thread stacks, especially Media Foundation, rendering and UI threads. Do not label a wait a proven deadlock without identifying the dependency.
7. Recovery remains enabled by default: diagnostics after about 3 seconds, restart requested after 5 seconds without expected playback progress, with **no attempt limit in development or production**. One-second checks and external restart work add time before playback resumes. Cold initialization/opening has a separate grace period. Explicit hostname timeouts override the default; set `RendererRecovery.StallTimeoutSeconds` to 5 after updating if it was previously 30. Read `RendererStallRestartRequested`, `RendererStallRecoveryFailed`, and restart-script logs to verify each recovery. Failed script launches back off for sixty seconds and keep retrying; prior recovery-history files are no longer consulted. Software mode is reapplied from the hostname configuration after each restart.

Report mode, build hashes, hardware/driver, duration, loops/frame progress, edits if any, CPU/GPU measurements, stall operations, restart events and evidence paths. If both modes stall, compare all thread stacks before considering a separate driver-version or rendering-device experiment.

## Local regression commands

Build from `PhotoBooth` with deployed applications stopped:

```powershell
dotnet build ExperienceX.Tests\ExperienceX.Tests.csproj -c Release -p:CopyBuildOutputToOneDrive=false
$env:EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO = Join-Path $env:LOCALAPPDATA 'Home Technologies\ExperienceX\Media\Eye.mp4'
$env:EXPERIENCEX_LOOP_TEST_VIDEO = $env:EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO
dotnet ExperienceX.Tests\bin\Release\net9.0-windows\ExperienceX.Tests.dll --software-video
dotnet ExperienceX.Tests\bin\Release\net9.0-windows\ExperienceX.Tests.dll --software-video-loop
dotnet ExperienceX.Tests\bin\Release\net9.0-windows\ExperienceX.Tests.dll --software-video-soak
```

Supply another existing local MP4 if needed; use nonblack video for the pixel-readback check, and a short clip for loop tests. These checks use isolated windows/configuration and leave deployed hostname files/media unchanged. `--software-video` covers parsing, restart-only mode application, and actual visible GPU pixel readback for both paths. The other checks cover native loop/fade/stop behavior and a one-minute three-video editing stress test. They are regression checks, not reproduction of the remote machine's driver failure.
