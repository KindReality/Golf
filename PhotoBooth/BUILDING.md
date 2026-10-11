# Build output

Successful application builds automatically copy runnable output and its dependencies to:

```
%OneDrive%\Home Technologies\Bin\Camera
%OneDrive%\Home Technologies\Bin\Experience
%OneDrive%\Home Technologies\Bin\ExperienceX
%OneDrive%\Home Technologies\Bin\Controller
```

MSBuild reads OneDrive from the environment; OneDriveConsumer and OneDriveCommercial are fallbacks. No username or absolute OneDrive location is hardcoded. When no root is available, a warning is shown and the copy is skipped.

Run Camera.exe and Experience.exe in their respective folders. These are normal build outputs and require the matching .NET Desktop Runtime on another computer; the copy does not convert them to self-contained deployments.

For Controller and the current ExperienceX app, double-click these CMD files in the OneDrive output:

| Application | First installation | Subsequent updates/restarts |
|---|---|---|
| Controller | Install-Controller.cmd | Restart-Controller.cmd |
| ExperienceX | Install-Experience.cmd | Restart-Experience.cmd |

Each installer checks startup prerequisites, requests administrator approval for network setup, then calls its restart script. Controller setup creates the read/write media share and a non-administrator `SaftThings` account using the embedded media password. It grants that account NTFS Modify on the media folder while preserving other permissions. Experience setup opens inbound UDP, asks for Controller computer names (comma-separated if more than one), and stores matching credentials in the current user's Windows Credential Manager. Media uses direct `\\hostname\media` access; no mapped drive is needed. Rerun both installers once to apply this credential setup to existing installations, then use Restart for routine updates. Rules apply to Private networks and the local subnet. Restart scripts need no administrator prompt, preserve configuration/media/logs and network permissions, verify copied EXE/DLL hashes, and launch locally. Failures remain visible in the CMD window. The scripts check the x64 .NET Desktop Runtime and report what to install if it is missing; they do not download software. Controller runs from `%LOCALAPPDATA%\Home Technologies\Controller\Application`; ExperienceX runs from `%TEMP%\Experience`.

For the current ExperienceX app, use `Restart-Experience.cmd` in its OneDrive output folder. It refreshes `%TEMP%\Experience` and launches the app there, freeing OneDrive binaries for later builds/sync updates. The CMD and accompanying PowerShell helper are included in both build and publish output. Run it again after the latest files finish syncing. Existing hostname configuration in OneDrive is retained. The launcher never copies `Media` and preserves any existing `%TEMP%\Experience\Media` folder; media is downloaded from discovered Controllers into `%LOCALAPPDATA%\Home Technologies\ExperienceX\Media` or installed manually there. Playback uses local files and rejects network storage.

Both restart scripts verify copied EXE/DLL files with SHA-256 through .NET directly; they do not require PowerShell's `Get-FileHash` command or module discovery for hashing.

Controller and ExperienceX monitor their OneDrive EXE and DLL once per second while running from a local copy. A changed binary triggers an automatic restart after the build manifest and binaries settle for five seconds and all package SHA-256 hashes match. Each build generates `build-manifest.sha256`, including dependencies and scripts but excluding media, logs and working configuration. The existing restart helper runs invisibly, stages and verifies the complete package before stopping the app, then refreshes and launches the local copy. Configuration and media are retained. Failed staging leaves the existing app running; failed restart requests wait 60 seconds before retrying. Update activity is recorded in application telemetry and the runtime folder's `logs\UpdateRestart.log`. Applying an update interrupts current playback/controller runs. After installing this version, run the usual restart CMD once on each computer to enable future automatic updates. Apps launched directly from the OneDrive folder cannot update themselves; use their restart CMD.

Controller and the current Experience app (ExperienceX) ship `appsettings.defaults.json` as the shared template. Edit each project's source `appsettings.json` to change the defaults delivered by subsequent builds. Globally known settings belong in this template; ExperienceX's default display aliases, backgrounds and screen maps are empty so startup can populate them for the detected hardware.

On first launch, each app validates and copies the OneDrive template into `appsettings.<computer-name>.json` beside it: `%OneDrive%\Home Technologies\Bin\Controller` or `%OneDrive%\Home Technologies\Bin\ExperienceX`. The suffix is the Windows hostname, independent of DeviceName. OneDriveConsumer and OneDriveCommercial are root fallbacks; missing OneDrive produces a startup error rather than a user-profile fallback. Existing hostname files and backups are retained across builds and restarts. Both apps monitor their hostname file for edits, including changes synchronized from another computer. Templates only initialize missing configurations; invalid or missing templates prevent first-time initialization. Old user-profile files are left intact and ignored. Experience display inventory writes only to the hostname file. Media stays local under LocalAppData.

Subdirectories are preserved and unchanged files are skipped. Logs and captured Output files are excluded. Controller and ExperienceX also exclude live `appsettings.json`, recovery files and `Configuration` folders from the OneDrive copy. Old executable-local and user-profile appsettings.json files are ignored and left intact. Camera and the original Experience retain their existing configuration behavior. Stop apps running from these destination folders before building to avoid locked executable files. Debug and Release builds share the same destinations, with the latest successful build replacing the previous files.

To choose another destination:

```powershell
dotnet build Experience/Experience.csproj -p:PhotoBoothBinRoot="C:\My Apps"
```

To build without copying:

```powershell
dotnet build PhotoBooth.sln -p:CopyBuildOutputToOneDrive=false
```
