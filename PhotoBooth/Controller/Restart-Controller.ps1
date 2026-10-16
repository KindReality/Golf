param([Parameter(Mandatory = $true)][string]$SourceDirectory, [switch]$ValidateOnly, [switch]$AutomaticUpdate, [switch]$RestartExperience)

# Runs with inbox Windows PowerShell 5.1; PowerShell 7 is not required on the target PC.
$ErrorActionPreference = 'Stop'
function Get-FileSha256([string]$LiteralPath) {
    # Use .NET directly so verification does not depend on PowerShell module discovery.
    $stream = $null
    $algorithm = $null
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($LiteralPath)
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '')
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $algorithm) { $algorithm.Dispose() }
    }
}
$mutex = $null
$ownsMutex = $false
$stage = $null
$updateLog = $null
try {
    if ($AutomaticUpdate) {
        $logFolder = Join-Path $PSScriptRoot 'logs'
        [void][IO.Directory]::CreateDirectory($logFolder)
        $updateLog = Join-Path $logFolder 'UpdateRestart.log'
        [IO.File]::AppendAllText($updateLog, ([DateTime]::UtcNow.ToString('o') + " Automatic update requested.`r`n"))
    }
    $source = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\')
    if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'The source must be a folder.' }
    foreach ($required in @('Controller.exe','Controller.dll','Controller.deps.json','Controller.runtimeconfig.json','appsettings.defaults.json','appsettings.environment.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
            throw "Missing $required. Run Restart-Controller.cmd from the complete build output folder."
        }
    }

    $runtime = Get-Content -LiteralPath (Join-Path $source 'Controller.runtimeconfig.json') -Raw | ConvertFrom-Json
    $framework = @($runtime.runtimeOptions.frameworks) + @($runtime.runtimeOptions.framework) |
        Where-Object { $_ -and $_.name -eq 'Microsoft.WindowsDesktop.App' } | Select-Object -First 1
    if ($framework) {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        $major = ([version]$framework.version).Major
        if (!(Test-Path -LiteralPath $dotnet) -or !(& $dotnet --list-runtimes | Where-Object { $_ -match ('^Microsoft.WindowsDesktop.App '+$major+'\.') })) {
            throw "Install the x64 .NET $major Windows Desktop Runtime, then run this script again."
        }
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or ![IO.Path]::IsPathRooted($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable.' }
    $localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
    if ($localRoot -eq [IO.Path]::GetPathRoot($localRoot).TrimEnd('\')) { throw 'LOCALAPPDATA cannot be a drive root.' }
    $controllerRoot = [IO.Path]::GetFullPath((Join-Path $localRoot 'Home Technologies\Controller')).TrimEnd('\')
    $destination = [IO.Path]::GetFullPath((Join-Path $controllerRoot 'Application')).TrimEnd('\')
    # Verify the exact application subtree before any recursive cleanup or copy.
    if (!$controllerRoot.StartsWith($localRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($destination) -ine $controllerRoot -or [IO.Path]::GetFileName($destination) -ine 'Application') {
        throw 'Invalid Controller application folder.'
    }
    if ($source -ieq $destination -or $source.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and runtime folders overlap. Run the CMD from the OneDrive/build output copy, not from the installed Application folder.'
    }
    # Never refresh an application folder redirected by a junction or symlink.
    if (Test-Path -LiteralPath $destination) {
        $pending = New-Object 'Collections.Generic.Queue[string]'
        $pending.Enqueue($destination)
        while ($pending.Count -gt 0) {
            $directory = $pending.Dequeue()
            $item = Get-Item -LiteralPath $directory -Force
            if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Runtime folder contains a redirected directory: $directory. Refusing to refresh it."
            }
            foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Runtime folder contains a junction/symlink: $($child.FullName). Refusing to refresh it."
                }
                if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
            }
        }
    }

    if ($ValidateOnly) { Write-Host 'Controller build and runtime are ready.'; exit 0 }

    $session = (Get-Process -Id $PID).SessionId
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = New-Object Threading.Mutex($false, ('Local\HomeTechnologies.Controller.Restart.'+$sid+'.'+$session))
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch {
        if ($_.Exception -is [Threading.AbandonedMutexException] -or $_.Exception.InnerException -is [Threading.AbandonedMutexException]) { $ownsMutex = $true }
        else { throw }
    }
    if (!$ownsMutex) { throw 'Another Controller restart is already in progress. Wait for it to finish.' }

    $experienceSource = $null
    $experienceScript = $null
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if ($RestartExperience) {
        # Resolve before staging replaces $source. Validate Experience before stopping Controller.
        $experienceSource = Join-Path ([IO.Path]::GetDirectoryName($source)) 'ExperienceX'
        $experienceScript = Join-Path $experienceSource 'Restart-Experience.ps1'
        if (!(Test-Path -LiteralPath $experienceScript -PathType Leaf)) { throw 'Experience restart script is missing from the sibling ExperienceX build folder. Wait for OneDrive sync.' }
        Write-Host 'Checking the Experience build before restarting both applications...'
        & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $experienceScript -SourceDirectory $experienceSource -AutomaticUpdate -ValidateOnly
        if ($LASTEXITCODE -ne 0) { throw 'Experience startup checks failed; both applications were left running.' }
    }

    # Use the original OneDrive source, never the temporary automatic-update stage.
    try {
        . (Join-Path $PSScriptRoot 'StartMenuShortcut.ps1')
        New-SaftStartMenuShortcut -SourceDirectory $source -ApplicationName 'Controller' -RestartCommand 'Restart-Controller.cmd' -IconPath (Join-Path $source 'Controller.exe')
    } catch { Write-Warning ('Could not repair the Start Menu shortcut: '+$_.Exception.Message) }

    if ($AutomaticUpdate) {
        . (Join-Path $PSScriptRoot 'Stage-ApplicationUpdate.ps1')
        $stage = New-VerifiedApplicationStage -Source $source -Application 'Controller'
        $source = $stage
        [IO.File]::AppendAllText($updateLog, ([DateTime]::UtcNow.ToString('o') + " Update staged and verified; restarting.`r`n"))
    }

    Write-Host 'Stopping the existing Controller instance...'
    # WM_CLOSE now hides Controller in the notification area. Request its actual exit.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SaftControllerLifetime {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
'@
    foreach ($process in @(Get-Process -Name Controller -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })) {
        $receiver = [SaftControllerLifetime]::FindWindow([IntPtr]::Zero, 'HomeTechnologies.Configuration.Controller')
        [uint32]$receiverProcess = 0
        if ($receiver -ne [IntPtr]::Zero) { [void][SaftControllerLifetime]::GetWindowThreadProcessId($receiver, [ref]$receiverProcess) }
        if ($receiverProcess -eq $process.Id) {
            [void][SaftControllerLifetime]::PostMessage($receiver, [SaftControllerLifetime]::RegisterWindowMessage('HomeTechnologies.CloseApplication.v1'), [IntPtr]::Zero, [IntPtr]::Zero)
        } else { [void]$process.CloseMainWindow() }
        if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        if (!$process.WaitForExit(5000)) { throw "Controller process $($process.Id) did not exit. Copy cancelled." }
    }

    Write-Host "Refreshing $destination from $source..."
    [void][IO.Directory]::CreateDirectory($destination)
    foreach ($item in Get-ChildItem -LiteralPath $destination -Force) {
        # Retain local media and diagnostic history while removing obsolete binaries.
        if ($item.Name -ieq 'Media') { continue }
        if ($item.Name -ieq 'logs' -and $item.PSIsContainer) { continue }
        if ($item.Name -ieq 'Configuration' -or $item.Name -ieq 'appsettings.json' -or
            ($item.Name -ilike 'appsettings.*' -and $item.Name -notin @('appsettings.defaults.json','appsettings.environment.json'))) { continue }
        $target = [IO.Path]::GetFullPath($item.FullName)
        if (!$target.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the runtime folder.' }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        # Hostname configuration stays beside the OneDrive template; never deploy working files.
        if ($item.Name -in @('Media','Configuration','logs') -or $item.Name -ieq 'appsettings.json' -or
            ($item.Name -ilike 'appsettings.*' -and $item.Name -notin @('appsettings.defaults.json','appsettings.environment.json'))) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }

    foreach ($name in @('Controller.exe','Controller.dll')) {
        if ((Get-FileSha256 -LiteralPath (Join-Path $source $name)) -ne
            (Get-FileSha256 -LiteralPath (Join-Path $destination $name))) { throw "Copy verification failed for $name. Wait for OneDrive sync and retry." }
    }
    $experienceRestartFailed = $false
    if ($RestartExperience) {
        # Controller (including its watchdog) is stopped; restart Experience before relaunching it.
        Write-Host 'Restarting Experience...'
        & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $experienceScript -SourceDirectory $experienceSource -AutomaticUpdate
        $experienceRestartFailed = $LASTEXITCODE -ne 0
        if ($experienceRestartFailed) { Write-Warning 'Experience restart failed. Controller will still be relaunched; see Experience logs/UpdateRestart.log.' }
    }
    $executable = Join-Path $destination 'Controller.exe'
    Write-Host "Starting $executable..."
    $application = Start-Process -FilePath $executable -WorkingDirectory $destination -WindowStyle Normal -PassThru
    if ($application.WaitForExit(1500)) {
        throw "Controller exited during startup (exit code $($application.ExitCode)). Check $destination\logs\Controller and the installed .NET Desktop Runtime."
    }
    Write-Host "Controller is running from $destination (PID $($application.Id)). OneDrive files are free for updates."
    if ($updateLog) { [IO.File]::AppendAllText($updateLog, ([DateTime]::UtcNow.ToString('o') + " Started Controller PID $($application.Id).`r`n")) }
    if ($experienceRestartFailed) { throw 'Controller is running again, but Experience restart failed. Check Experience logs/UpdateRestart.log.' }
    exit 0
}
catch {
    if ($updateLog) { [IO.File]::AppendAllText($updateLog, ([DateTime]::UtcNow.ToString('o') + ' ERROR: ' + $_.Exception.Message + "`r`n")) }
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $stage -and [IO.Path]::GetDirectoryName($stage) -ieq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -and
        [IO.Path]::GetFileName($stage) -match '^HomeTechnologies-Update-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
