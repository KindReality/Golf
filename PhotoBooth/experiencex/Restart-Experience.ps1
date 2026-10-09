param([Parameter(Mandatory = $true)][string]$SourceDirectory)

# Runs with inbox Windows PowerShell 5.1; PowerShell 7 is not required on the target PC.
$ErrorActionPreference = 'Stop'
$mutex = $null
$ownsMutex = $false
try {
    $source = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\')
    if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'The source must be a folder.' }
    foreach ($required in @('ExperienceX.exe','ExperienceX.dll','ExperienceX.deps.json','ExperienceX.runtimeconfig.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
            throw "Missing $required. Run Restart-Experience.cmd from the complete build output folder."
        }
    }

    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($tempRoot -eq [IO.Path]::GetPathRoot($tempRoot).TrimEnd('\')) { throw 'TEMP cannot be a drive root.' }
    $destination = [IO.Path]::GetFullPath((Join-Path $tempRoot 'Experience')).TrimEnd('\')
    # Validate final absolute targets before any recursive removal or copy.
    if ([IO.Path]::GetDirectoryName($destination) -ine $tempRoot -or [IO.Path]::GetFileName($destination) -ine 'Experience') {
        throw 'The runtime folder must be the Experience folder immediately under the current TEMP directory.'
    }
    if ($source -ieq $destination -or $source.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and runtime folders overlap. Run the CMD from the OneDrive/build output copy, not from TEMP\Experience.'
    }
    # Never recursively remove a cache redirected outside TEMP by a junction or symlink.
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

    $session = (Get-Process -Id $PID).SessionId
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = New-Object Threading.Mutex($false, ('Local\HomeTechnologies.ExperienceX.Restart.'+$sid+'.'+$session))
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch {
        if ($_.Exception -is [Threading.AbandonedMutexException] -or $_.Exception.InnerException -is [Threading.AbandonedMutexException]) { $ownsMutex = $true }
        else { throw }
    }
    if (!$ownsMutex) { throw 'Another Experience restart is already in progress. Wait for it to finish.' }

    Write-Host 'Stopping the existing ExperienceX instance...'
    foreach ($process in @(Get-Process -Name ExperienceX -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (!$process.WaitForExit(5000)) { throw "ExperienceX process $($process.Id) did not exit. Copy cancelled." }
    }

    Write-Host "Refreshing $destination from $source..."
    [void][IO.Directory]::CreateDirectory($destination)
    $retainConfiguration = (Test-Path -LiteralPath (Join-Path $destination 'appsettings.json')) -or
        (Test-Path -LiteralPath (Join-Path $destination 'appsettings.json.bak'))
    foreach ($item in Get-ChildItem -LiteralPath $destination -Force) {
        # Retain local media and diagnostic history while removing obsolete binaries.
        if ($item.Name -ieq 'Media') { continue }
        if ($item.Name -ieq 'logs' -and $item.PSIsContainer) { continue }
        if ($item.Name -in @('appsettings.json','appsettings.json.bak')) { continue }
        $target = [IO.Path]::GetFullPath($item.FullName)
        if (!$target.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the runtime folder.' }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        # Media is managed separately on each computer, including the first launch.
        if ($item.Name -ieq 'Media') { continue }
        # A surviving backup also protects the local settings from a new template.
        if ($retainConfiguration -and $item.Name -in @('appsettings.json','appsettings.json.bak')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }

    $executable = Join-Path $destination 'ExperienceX.exe'
    Write-Host "Starting $executable..."
    $application = Start-Process -FilePath $executable -WorkingDirectory $destination -WindowStyle Hidden -PassThru
    if ($application.WaitForExit(1500)) {
        throw "ExperienceX exited during startup (exit code $($application.ExitCode)). Check $destination\logs\ExperienceX and the installed .NET Desktop Runtime."
    }
    Write-Host "ExperienceX is running from TEMP\Experience (PID $($application.Id)). OneDrive files are free for updates."
    exit 0
}
catch {
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
