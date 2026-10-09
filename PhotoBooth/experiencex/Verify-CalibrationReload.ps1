$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
. (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
$path=Get-ExperienceXConfigurationPath
$app=Get-Process ExperienceX -ErrorAction SilentlyContinue
if (-not $app) {
    $app=Start-Process (Join-Path $runtime 'ExperienceX.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
}
if (@($app).Count -ne 1) {throw 'Expected one ExperienceX instance.'}
$saved=[IO.File]::ReadAllText($path)
$results=[Collections.Generic.List[object]]::new()
function Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\ExperienceX') -Filter "*-$($app.Id)-*.jsonl" | ForEach-Object {
        Get-Content $_.FullName | ForEach-Object {$_|ConvertFrom-Json}
    })
}
function Change-And-Wait($config,$label,$condition,[switch]$Replace) {
    $since=[DateTimeOffset]::UtcNow
    $script:changeStarted=$since
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $json=$config|ConvertTo-Json -Depth 16
    if ($Replace) {
        $temporary=$path+'.reload-test'
        [IO.File]::WriteAllText($temporary,$json)
        [IO.File]::Move($temporary,$path,$true)
    } else {[IO.File]::WriteAllText($path,$json)}
    do {
        Start-Sleep -Milliseconds 50
        $match=Events | Where-Object { $_.Event -eq 'ConfigurationLoaded' -and [DateTimeOffset]$_.Timestamp -ge $since -and (& $condition $_.Data) } | Select-Object -Last 1
        if ($match) {
            $results.Add([pscustomobject]@{Change=$label;ReloadMilliseconds=([DateTimeOffset]$match.Timestamp-$since).TotalMilliseconds})
            return
        }
    } while ($clock.Elapsed.TotalSeconds -lt 5)
    throw "Configuration reload timed out: $label"
}
function Wait-RemoteConfirmation($monitor,$points,$since) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $match=Events | Where-Object {
            $_.Event -eq 'KeystoneRemoteChangeApplied' -and [DateTimeOffset]$_.Timestamp -ge $since -and
            $_.Data.MonitorName -eq $monitor -and ($_.Data.Points -join ',') -eq $points
        } | Select-Object -Last 1
        if($match){return $match}
        Start-Sleep -Milliseconds 50
    } while($clock.Elapsed.TotalSeconds -lt 5)
    throw "Remote renderer confirmation timed out for $monitor, points $points"
}
try {
    Add-Type -AssemblyName System.Windows.Forms
    $config=$saved|ConvertFrom-Json
    $monitors=@([Windows.Forms.Screen]::AllScreens|ForEach-Object DeviceName)
    foreach ($monitor in $monitors) {
        if (-not $config.DisplayKeystones.PSObject.Properties[$monitor]) {throw "Missing automatic calibration: $monitor"}
    }
    $config.configurationMode=$true
    Change-And-Wait $config 'Enable configuration mode' {$args[0].ConfigurationMode -eq $true}
    $target=$monitors[0]
    $display=$config.DisplayKeystones.PSObject.Properties[$target].Value
    $display.Enabled=$true; $display.TopLeft.X=0.1; $display.TopLeft.Y=0.05
    $display.TopRight.X=1; $display.TopRight.Y=0; $display.BottomLeft.X=0; $display.BottomLeft.Y=1; $display.BottomRight.X=1; $display.BottomRight.Y=1
    Change-And-Wait $config 'Remote per-display coordinates via file replacement' { $args[0].DisplayKeystones.PSObject.Properties[$target].Value.TopLeft.X -eq 0.1 } -Replace
    $remote=Wait-RemoteConfirmation $target '1' $script:changeStarted
    if (-not $remote -or $remote.Data.MonitorName -ne $target -or ($remote.Data.Points -join ',') -ne '1') {throw 'Remote single-corner confirmation was missing or targeted the wrong corners.'}
    $config.configurationMode=$false
    Change-And-Wait $config 'Disable configuration mode' {$args[0].ConfigurationMode -eq $false}
    $since=[DateTimeOffset]::UtcNow
    $display.TopLeft.X=0.2; $display.TopRight.X=0.85
    Change-And-Wait $config 'Remote multiple-corner update with configuration mode off' { $args[0].ConfigurationMode -eq $false -and $args[0].DisplayKeystones.PSObject.Properties[$target].Value.TopLeft.X -eq 0.2 }
    $remote=Wait-RemoteConfirmation $target '1,2' $since
    if (-not $remote -or $remote.Data.MonitorName -ne $target -or ($remote.Data.Points -join ',') -ne '1,2') {throw 'Remote confirmation did not include both changed corners with configuration mode off.'}
    if (Events | Where-Object { $_.Level -eq 'Error' }) {throw 'Application logged an error during calibration reload verification.'}
    [pscustomobject]@{ProcessId=$app.Id;MonitorCount=$monitors.Count;Checks=$results.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $PSScriptRoot '..\.tools\calibration-live-results.json')
    $results | Format-Table -AutoSize
    "Passed automatic per-display initialization and live configuration/coordinate reload without restart."
} finally {
    [IO.File]::WriteAllText($path,$saved)
    Start-Sleep -Milliseconds 1200
}
