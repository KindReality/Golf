param([ValidateRange(3,30)][int]$PlaybackSeconds = 6, [string]$DeviceNamePattern = '*')
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
. (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$configPath = Get-ExperienceXConfigurationPath
$saved = Get-Content -LiteralPath $configPath -Raw
$prefix = 'coordinated-' + [Guid]::NewGuid().ToString('N')
$tone = Join-Path $runtime ('Media\' + $prefix + '.wav')
$client = [Net.Sockets.UdpClient]::new()
$client.EnableBroadcast=$true
function Send-Request($request) {
    $bytes=ConvertTo-ExperienceBroadcastBytes -Request $request -Configuration ($saved|ConvertFrom-Json)
    [void]$client.Send($bytes,$bytes.Length,'255.255.255.255',($saved|ConvertFrom-Json).UdpPort)
}
function Read-Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\ExperienceX') -Filter ("*-$($app.Id)-*.jsonl") | ForEach-Object {
        Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    })
}
function Wait-Event($name, $id) {
    $deadline = [DateTime]::UtcNow.AddSeconds($PlaybackSeconds + 20)
    do {
        if (Read-Events | Where-Object { $_.Event -eq $name -and $_.RequestId -eq $id }) { return }
        [Threading.Thread]::Sleep(100)
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $name for $id"
}
try {
    if (Get-Process ExperienceX -ErrorAction SilentlyContinue) { throw 'Stop ExperienceX before this verification.' }
    $config = $saved | ConvertFrom-Json
    $config.MaxAudioPlaybackLength = $PlaybackSeconds
    $config.AudioTransitionInSeconds = 0.25
    $config.AudioTransitionOutSeconds = 0.5
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath
    # Silent audio reaches actual WASAPI devices without audible test noise.
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($tone))
    try {
        $data = [byte[]]::new(48000 * 2 * 2 * ($PlaybackSeconds + 2))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $data.Length))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([int16]1); $writer.Write([int16]2); $writer.Write([int]48000)
        $writer.Write([int]192000); $writer.Write([int16]4); $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$data.Length); $writer.Write($data)
    } finally { $writer.Dispose() }
    $app = Start-Process (Join-Path $runtime 'ExperienceX.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru
    Wait-Event 'AudioOutputsAvailable' $null
    $devices = @((Read-Events | Where-Object Event -eq 'AudioOutputsAvailable' | Select-Object -Last 1).Data.Devices)
    if ($devices.Count -lt 3) { throw 'Three active audio endpoints are required for this hardware verification.' }
    $ids = @($devices | Where-Object Name -like $DeviceNamePattern | Select-Object -First 3 | ForEach-Object Id)
    if ($ids.Count -lt 3) { throw 'Three endpoints must match DeviceNamePattern.' }
    $fileName = [IO.Path]::GetFileName($tone)
    $arrayRequest = @{experience='audio'; value=$fileName; audioDevices=@($ids + $ids[0]); channel=1; volume=35; requestId=($prefix+'-array')}
    Send-Request $arrayRequest
    Wait-Event 'AudioGroupStarted' ($prefix+'-array')
    Send-Request $arrayRequest
    Send-Request @{experience='audio';value=$fileName;audioDevice=($ids[0..1] -join ',');volume=70;mute=$true;requestId=($prefix+'-comma')}
    $config | Add-Member -Force NoteProperty VirtualAudioDevices @{ VerificationRoom=@($ids + 'MISSING-VERIFICATION-DEVICE') }
    $config | Add-Member -Force NoteProperty AudioDeviceLatencyMilliseconds @{ $ids[0]=12 }
    $config.DefaultAudioDevice='VerificationRoom'
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath
    [Threading.Thread]::Sleep(1500)
    Send-Request @{experience='audio';value=$fileName;volume=50;requestId=($prefix+'-virtual')}
    Wait-Event 'AudioGroupStarted' ($prefix+'-virtual')
    # Live group edits affect the next request, while existing membership stays fixed.
    $config.VirtualAudioDevices.VerificationRoom=@($ids[1..2])
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath
    [Threading.Thread]::Sleep(1500)
    Send-Request @{experience='audio';value=$fileName;audioDevice='VerificationRoom';volume=0;requestId=($prefix+'-reloaded')}
    foreach ($suffix in @('array','comma','virtual','reloaded')) { Wait-Event 'AudioGroupCompleted' ($prefix+'-'+$suffix) }
    $events=Read-Events
    $counts=@{array=3;comma=2;virtual=3;reloaded=2}
    foreach ($suffix in $counts.Keys) {
        $id=$prefix+'-'+$suffix
        $started=@($events | Where-Object { $_.Event -eq 'AudioPlaybackStarted' -and $_.RequestId -eq $id })
        $completed=@($events | Where-Object { $_.Event -eq 'AudioPlaybackCompleted' -and $_.RequestId -eq $id })
        if ($started.Count -ne $counts[$suffix] -or $completed.Count -ne $counts[$suffix]) { throw "Wrong playback/completion count for $suffix : $($started.Count)/$($completed.Count)" }
        if ($completed | Where-Object { $_.Data.PositionSeconds -gt ($PlaybackSeconds + 0.01) -or $_.Data.Reason -ne 'MaximumPlaybackLength' }) { throw 'Coordinated playback exceeded its deadline.' }
    }
    if (-not ($events | Where-Object { $_.Event -eq 'UdpDuplicateSuppressed' -and $_.RequestId -eq ($prefix+'-array') })) { throw 'Grouped retry was not suppressed.' }
    if (-not ($events | Where-Object { $_.Event -eq 'AudioDeviceUnavailable' -and $_.RequestId -eq ($prefix+'-virtual') })) { throw 'Missing group member was not logged.' }
    $failures=@($events | Where-Object { $_.Level -eq 'Error' -or $_.Event -match '^Audio(Output.*Failed|GroupShutdownTimeout)' })
    if ($failures.Count -gt 0) { $failures | ConvertTo-Json -Depth 8; throw 'Coordinated audio recorded failures.' }
    $measurements=@($events | Where-Object Event -eq 'AudioSynchronizationMeasured' | ForEach-Object { $_.Data.Outputs })
    if ($measurements.Count -eq 0) { throw 'No endpoint synchronization measurements were recorded.' }
    $maxError=($measurements | ForEach-Object { [Math]::Abs($_.ErrorMilliseconds) } | Measure-Object -Maximum).Maximum
    $maxUnderruns=($measurements | ForEach-Object Underruns | Measure-Object -Maximum).Maximum
    $report=[ordered]@{VerifiedAt=[DateTime]::UtcNow.ToString('o');ProcessId=$app.Id;Outputs=$ids;TracksCompleted=10;MaxEstimatedErrorMilliseconds=$maxError;MaxUnderruns=$maxUnderruns;Errors=$failures.Count;Measurement='Endpoint-clock estimates; acoustic alignment is not measured';PlaybackSeconds=$PlaybackSeconds}
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\.tools\coordinated-audio-verification.json')
    $report | ConvertTo-Json -Depth 8
    if ($maxError -gt 5) { throw "Endpoint clock/cursor error exceeded 5 ms: $maxError" }
    if ($maxUnderruns -gt 0) { throw 'Audio buffer underruns occurred.' }
} finally {
    Set-Content -LiteralPath $configPath -Value $saved
    $client.Dispose()
    Remove-Item -LiteralPath $tone -ErrorAction SilentlyContinue
}
