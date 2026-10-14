# Development contribution handoff

Prepared on ALPHA, October 13, 2026.

## Authority and scope

ALPHA remains the primary development and release machine. Set up this other Windows PC to contribute when requested. Use a local checkout outside OneDrive, a contribution branch, and normal production hardware discovery. Do not replace ALPHA's source, push directly to main, publish into the shared OneDrive Bin, or change production defaults just to accommodate this PC. Return focused commits for review and integration on ALPHA.

Do not delete or overwrite hostname configurations, media, output folders, or existing work. Media stays out of Git. Do not leave test application instances running after validation; keep operational instances running only when requested.

## Source: a clone alone is currently insufficient

- Repository: https://github.com/KindReality/Golf
- Repository root contains `PhotoBooth/`; open that folder in Codex.
- Primary checkout: `C:\Source\Repos\Golf\PhotoBooth` on ALPHA.
- Recorded base commit: `a246ce765881fa040540f81beb0d9dc52adbab23`, branch `main`.
- There are substantial uncommitted changes on ALPHA. The accompanying `PhotoBooth-Source-ALPHA.zip` captures the current project source, including required new files. It excludes media and generated output.
- `SourceSnapshot.json` records the base, excluded/deleted files, and each archived file's SHA-256. `SHA256SUMS.txt` verifies the handoff package itself. This is a dated transfer snapshot, not a second authoritative repository.

If ALPHA has since committed these changes, use the newer agreed commit instead of the snapshot. Otherwise:

1. Authenticate to GitHub if needed. Clone into a new local folder, such as `C:\Source\Repos\Golf`. Never overlay an existing dirty checkout.
2. Fetch and create a local snapshot branch at the recorded base commit.
3. Verify the package hashes; inspect the archive before extraction. Its entries start with `PhotoBooth/`. Extract over the fresh repository root. Remove only the tracked deleted paths listed in `SourceSnapshot.json`, after checking they resolve inside this fresh checkout.
4. Verify all extracted source hashes against the manifest. Review `git status` and `git diff`.
5. Record the imported snapshot in a **local baseline commit**, without pushing it. Then create a separate contribution branch from that baseline. Return only subsequent contribution commits to ALPHA for cherry-picking; the imported baseline is already present in ALPHA's working tree and must not be reapplied as a feature change.

Example initial Git commands, executed outside the project folder:

```powershell
git clone https://github.com/KindReality/Golf C:\Source\Repos\Golf
Set-Location C:\Source\Repos\Golf
git switch -c contribution/snapshot-alpha-20261013 a246ce765881fa040540f81beb0d9dc52adbab23
```

Do not push, commit ALPHA's pending work, or deploy a contribution without the user's instruction. The secondary PC may create its local baseline and contribution commits as part of the requested contribution workflow.

## Install development prerequisites

Inspect what is installed first, then install missing prerequisites:

- Windows 11 x64 with working GPU drivers.
- Git and GitHub access to the repository.
- An x64 .NET SDK capable of building `net9.0-windows`; .NET 9 SDK is the direct match. Also verify the **x64 .NET 9 Windows Desktop Runtime** is installed. A newer SDK alone does not establish that the required runtime exists. Use Microsoft's official installers or verified package identifiers.
- OneDrive signed into the intended account, with `Home Technologies` available locally. Verify synchronization before using shared templates/installers.
- Codex with the local `PhotoBooth` folder open. VS Code/C# tooling is optional.

Check `dotnet --list-sdks`, `dotnet --list-runtimes`, and `git --version`. NuGet restore installs project dependencies, including NAudio, Vortice, OpenCvSharp for Camera, and System.IO.Ports. No C++ conversion or workload is required. Compiled shader assets are in source; install a Windows SDK with `fxc.exe` only if changing HLSL, then use `experiencex/Compile-Shaders.ps1`.

SoundSpectrum G-Force Standalone is optional for visualization tests and requires the user's licensed installation. Arduino, ESP32, WLED, and Jinx are optional hardware/integrations, not prerequisites for a basic build. Do not add substitutes or pretend hardware is present.

## Build without deploying from the contribution PC

Run from the local `PhotoBooth` folder:

```powershell
dotnet restore Controller/Controller.csproj
dotnet restore experiencex/ExperienceX.csproj
dotnet build Controller/Controller.csproj -c Release -p:CopyBuildOutputToOneDrive=false
dotnet build experiencex/ExperienceX.csproj -c Release -p:CopyBuildOutputToOneDrive=false
```

Current Experience is **ExperienceX**, source folder `experiencex`. `Experience/` is the older implementation. Camera is optional: `dotnet build Camera/Camera.csproj -c Release -p:CopyBuildOutputToOneDrive=false`. Build these projects individually rather than assuming every legacy solution project is needed.

Release uses Production; Debug uses Development. These are the only environments. Controller's default trigger cooldown is 10 seconds in Production and 1 second in Development. Use Release for production-representative startup checks.

**Important:** normal builds automatically copy to the shared OneDrive Bin, and running computers watch those binaries for automatic updates. Always pass `CopyBuildOutputToOneDrive=false` on the contribution machine. Verify the flag on IDE build tasks too. Shared publication remains ALPHA's responsibility. Publish only manifest-listed package files, with `build-manifest.sha256` last, preserving configuration/media/logs. Do not mirror or purge output directories.

## Get operational apps running through existing scripts

Use the current, synchronized ALPHA build in OneDrive to install operational applications on this PC. This is separate from building a contribution locally.

Resolve the OneDrive root through `OneDrive`, then `OneDriveConsumer`, then `OneDriveCommercial`; never hardcode the username.

| Application | Folder under OneDrive | First installation | Subsequent launch/update |
|---|---|---|---|
| Controller | `Home Technologies\Bin\Controller` | `Install-Controller.cmd` | `Restart-Controller.cmd` |
| Experience | `Home Technologies\Bin\ExperienceX` | `Install-Experience.cmd` | `Restart-Experience.cmd` |

Install invokes Restart. Installers check runtime prerequisites and request elevation for machine network setup; they do not install .NET. Controller creates the approved dedicated SMB account, read/write media share, NTFS permissions, and firewall rules. Experience asks for Controller hostnames and stores SMB credentials for the current user. Direct UNC access needs no mapped drive. Both create current-user Start Menu shortcuts under `Saft Technologies` that invoke Restart.

Scripts stage and verify files, stop the previous instance, and launch a local copy, retaining configuration/media. Do not run EXEs directly in OneDrive. Local executable paths:

- Controller: `%LOCALAPPDATA%\Home Technologies\Controller\Application\Controller.exe`
- Experience: `%TEMP%\Experience\ExperienceX.exe`

For testing a changed local build, use the corresponding build-output installer/restart helper only after inspecting its `-Source` handling and configuration source. Keep operational shared binaries untouched. Close operational instances during tests to avoid duplicate listeners, broadcasts, or exclusive hardware. Do not disable normal inventory or invent display/audio entries to make tests pass.

## Production configuration, media, and network

- Shared defaults: `appsettings.defaults.json` beside the OneDrive app package, built from each project's source `appsettings.json`.
- Working config: `appsettings.<WINDOWS-HOSTNAME>.json` beside that default. On first startup, defaults initialize the missing file and real detected hardware populates machine-specific settings. Do not copy ALPHA's hostname configuration to the new machine. Existing configurations survive restarts and builds; defaults do not reset them. Edits are monitored live.
- A blank device alias uses this PC's hostname. Hardware identities, aliases, screens, backgrounds, and audio routing should reflect this PC's actual devices.
- Controller media: `%LOCALAPPDATA%\Home Technologies\Controller\Media`, shared as `\\<controller-hostname>\media`.
- Experience media: `%LOCALAPPDATA%\Home Technologies\ExperienceX\Media`. Controller discovery copies root media files here; playback must use this local cache, never UNC media directly.
- Source archive media: `%OneDrive%\Home Technologies\SourceMedia`. Media is transferred separately, not in the source ZIP or Git. Verify playback compatibility before using archived originals; known compatible videos use H.264 8-bit YUV420p MP4.

Use the intended LAN subnet and Windows Private network profile. Installers establish applicable local-subnet firewall rules. Ports: UDP **54242 events**, UDP **54243 commands**, TCP **54244 Controller definitions**, TCP **54245 Experience inventory**, TCP **445 SMB media**, UDP **4048 WLED DDP**. Broadcast device `*` targets all recipients. Startup, periodic, requested, and change announcements drive discovery; larger definitions use HTTP snapshots. Normal operation needs no internet connection.

Default Controller playlists can broadcast to existing production devices. Coordinate startup tests with the user rather than altering defaults or letting test Controllers run unattended.

## Verify, then report

1. Confirm the current package hashes/manifest, runtime, and hostname config path. Confirm source snapshot hashes separately from executable package hashes.
2. Install and start the required operational app(s) using the synchronized scripts. Check startup logs, real display/audio inventory, Controller discovery, completed media synchronization, and successful playback from a local path. A running process alone is not playback proof: look for confirmed playback and nonzero rendered frames.
3. Build focused tests with copying disabled. These are executable test harnesses, not necessarily `dotnet test` suites:

```powershell
dotnet build Controller.Tests/Controller.Tests.csproj -c Release -p:CopyBuildOutputToOneDrive=false
dotnet build ExperienceX.Tests/ExperienceX.Tests.csproj -c Release -p:CopyBuildOutputToOneDrive=false
dotnet Controller.Tests/bin/Release/net9.0-windows/Controller.Tests.dll
dotnet Controller.Tests/bin/Release/net9.0-windows/Controller.Tests.dll --roles
dotnet ExperienceX.Tests/bin/Release/net9.0-windows/ExperienceX.Tests.dll --roles
dotnet ExperienceX.Tests/bin/Release/net9.0-windows/ExperienceX.Tests.dll --audio
dotnet ExperienceX.Tests/bin/Release/net9.0-windows/ExperienceX.Tests.dll --snapping
```

Read each suite's prerequisites before additional hardware tests. Real GPU/audio/multiple-display/G-Force/WLED checks need those resources. Missing ignored media fixtures are not source failures; use a compatible local fixture where supported. Do not change application behavior merely to satisfy an unavailable-hardware test.

Logs: Controller local application folder `logs\Controller`; Experience local runtime folder `logs\ExperienceX`; restart diagnostics `logs\UpdateRestart.log`. JSONL filenames include the hostname. Report versions/hashes, paths, tests, actual detected devices, unresolved issues, and which processes remain running.

## Code map and pending discussion

Read `BUILDING.md`, `NETWORKING.md`, `Controller/README.md`, `experiencex/README.md`, `experiencex/ARCHITECTURE.md`, and `TELEMETRY.md` first.

- Controller: `Application/ControllerHost.cs`; scheduling in `Scheduling/`; playlist/scene configuration in `Configuration/`; embedded UI in `Windows/`; announcements and dispatch in `Networking/`.
- Experience: `Application/ExperienceHost.cs`; display/configuration UI in `Windows/` and `Calibration/`; playback in `Video/` and `Audio/`; local media discovery/cache in `MediaDistribution/`; lighting and visualization in their respective folders.
- Shared: `Networking/`, `Configuration/`, `Updates/`, `Windows/`, and `Telemetry.cs`. Shared source is linked into applications; preserve relative project paths.

The snapshot includes roles targeting, ambient video priority, overlapping independent audio, identical-loop request suppression, full-display new screens, one-step point undo, and persisted media/mat layer ordering above G-Force. These changes are not all on GitHub yet.

The user was discussing a future redesign of random ambient item targeting. No new redesign is approved. Currently a scene references an ambient playlist; each enabled item has an independent random timer, commands use fixed targets or all matching roles, and cooldown belongs to Controller + playlist + item, not individual output. Ask design questions one at a time before implementing that change. No application code was changed for this handoff.
