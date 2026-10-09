# Build output

Successful Camera and Experience builds automatically copy the runnable output and its dependencies/media to:

```
%OneDrive%\Home Technologies\Bin\Camera
%OneDrive%\Home Technologies\Bin\Experience
%OneDrive%\Home Technologies\Bin\ExperienceX
```

MSBuild reads OneDrive from the environment; OneDriveConsumer and OneDriveCommercial are fallbacks. No username or absolute OneDrive location is hardcoded. When no root is available, a warning is shown and the copy is skipped.

Run Camera.exe and Experience.exe in their respective folders. These are normal build outputs and require the matching .NET Desktop Runtime on another computer; the copy does not convert them to self-contained deployments.

For the current ExperienceX app, use `Restart-Experience.cmd` in its OneDrive output folder. It refreshes `%TEMP%\Experience` and launches the app there, freeing OneDrive binaries for later builds/sync updates. The CMD and accompanying PowerShell helper are included in both build and publish output. Run it again after the latest files finish syncing. Existing per-user configuration is retained. The launcher never copies `Media` and preserves any existing `%TEMP%\Experience\Media` folder; media must be installed separately.

Subdirectories are preserved and unchanged files are skipped. Logs and captured Output files are excluded. Existing files are not deleted. Build-managed files, including appsettings.json, are updated from build output; keep settings in the project source if they should survive builds. Stop apps running from these destination folders before building to avoid locked executable files. Debug and Release builds share the same destinations, with the latest successful build replacing the previous files.

To choose another destination:

```powershell
dotnet build Experience/Experience.csproj -p:PhotoBoothBinRoot="C:\My Apps"
```

To build without copying:

```powershell
dotnet build PhotoBooth.sln -p:CopyBuildOutputToOneDrive=false
```
