param([switch]$DisableBlackKey, [switch]$EnableKeystone, [switch]$SkipAudio, [ValidateRange(0,3600)][double]$VideoFadeSeconds = 0.5)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
. (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$configPath = Get-ExperienceXConfigurationPath
$saved = Get-Content -LiteralPath $configPath -Raw
$client = [Net.Sockets.UdpClient]::new()
$client.EnableBroadcast=$true
$prefix = 'outputs-' + [Guid]::NewGuid().ToString('N')
$tone = Join-Path $runtime ('Media\' + $prefix + '.wav')
function Send-Request($message) {
    $message.source='OutputVerification'
    $bytes=ConvertTo-ExperienceBroadcastBytes -Request $message -Configuration ($saved|ConvertFrom-Json)
    [void]$client.Send($bytes,$bytes.Length,'255.255.255.255',($saved|ConvertFrom-Json).UdpPort)
}
function Read-Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\ExperienceX') -Filter ("*-$($app.Id)-*.jsonl") | ForEach-Object {
        Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    } | Where-Object ProcessId -eq $app.Id)
}
function Assert-Event($events, $name, $id) {
    if (-not ($events | Where-Object { $_.Event -eq $name -and $_.RequestId -eq $id })) { throw "Missing $name for $id" }
}
function Wait-Event($name, $id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (Read-Events | Where-Object { $_.Event -eq $name -and $_.RequestId -eq $id }) { return }
        [Threading.Thread]::Sleep(200)
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $name for $id"
}
try {
    if (Get-Process ExperienceX -ErrorAction SilentlyContinue) { throw 'Stop ExperienceX before running this startup verification.' }
    # Silent PCM exercises decoding and real endpoints without audible test noise.
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($tone))
    try {
        $data = [byte[]]::new(48000 * 2 * 2 * 8)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $data.Length))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([int16]1); $writer.Write([int16]2); $writer.Write([int]48000)
        $writer.Write([int]192000); $writer.Write([int16]4); $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$data.Length); $writer.Write($data)
    } finally { $writer.Dispose() }
    $config = $saved | ConvertFrom-Json
    $config.BlackKey.Enabled = -not $DisableBlackKey
    $config.Keystone.Enabled = [bool]$EnableKeystone
    $config.configurationMode=$false
    foreach ($display in $config.DisplayKeystones.PSObject.Properties) { $display.Value.Enabled=[bool]$EnableKeystone }
    $config.DefaultMonitorName = '\\.\DISPLAY-NOT-AVAILABLE-VERIFICATION'
    $config.MaxVideoPlaybackLength = 4; $config.MaxAudioPlaybackLength = 4
    $config.TransitionInSeconds = $VideoFadeSeconds; $config.TransitionOutSeconds = $VideoFadeSeconds
    $config.AudioTransitionInSeconds = 0.5; $config.AudioTransitionOutSeconds = 0.5
    $config | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $configPath
    $app = Start-Process (Join-Path $runtime 'ExperienceX.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru
    [Threading.Thread]::Sleep(2500)
    $events = Read-Events
    if ($events | Where-Object Event -eq 'PlaybackRequested') { throw 'Unexpected playback at startup.' }
    if (-not ($events | Where-Object Event -eq 'DefaultMonitorUnavailable')) { throw 'Missing default-monitor startup failure.' }
    $monitors = @(($events | Where-Object Event -eq 'MonitorsAvailable' | Select-Object -Last 1).Data.Monitors)
    if (@($events | Where-Object Event -eq 'MonitorWindowCreated').Count -ne $monitors.Count) { throw 'A window was not created for each monitor.' }
    Send-Request @{experience='video';value='Video.mp4';requestId=($prefix + '-missing-default')}
    $config.MonitorAliases = [ordered]@{}
    for ($index = 0; $index -lt $monitors.Count; $index++) { $config.MonitorAliases["VerificationMonitor$index"] = $monitors[$index].Name }
    $config.DefaultMonitorName = 'VerificationMonitor0'
    $config | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $configPath
    [Threading.Thread]::Sleep(1800)
    for ($index = 0; $index -lt $monitors.Count; $index++) {
        $monitor = $monitors[$index]
        Send-Request @{experience='video';value='Video.mp4';monitorName="VerificationMonitor$index";mute=$true;requestId=($prefix + '-video-' + $monitor.Name)}
    }
    [Threading.Thread]::Sleep(1400)
    $duplicateId=$prefix+'-video-'+$monitors[0].Name
    Wait-Event 'VideoPlaybackConfirmed' $duplicateId
    $retry=[Net.Sockets.UdpClient]::new()
    try {
        $retry.EnableBroadcast=$true
        $bytes=ConvertTo-ExperienceBroadcastBytes -Request @{experience='video';value='Video.mp4';monitorName='VerificationMonitor0';mute=$true;requestId=$duplicateId;source='OutputVerification'} -Configuration ($saved|ConvertFrom-Json)
        [void]$retry.Send($bytes,$bytes.Length,'255.255.255.255',($saved|ConvertFrom-Json).UdpPort)
        $retry.Client.ReceiveTimeout=200
        $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try {[void]$retry.Receive([ref]$remote);throw 'ExperienceX unexpectedly sent a UDP reply.'}
        catch [Net.Sockets.SocketException] {if($_.Exception.SocketErrorCode -ne [Net.Sockets.SocketError]::TimedOut){throw}}
    } finally {$retry.Dispose()}
    Send-Request @{experience='video';value='Video.mp4';monitorName=$null;mute=$true;requestId=($prefix + '-replacement')}
    Send-Request @{experience='stop';requestId=($prefix + '-stop')}
    $devices = @((Read-Events | Where-Object Event -eq 'AudioOutputsAvailable' | Select-Object -Last 1).Data.Devices)
    if ($SkipAudio) { $devices = @() }
    foreach ($device in $devices) {
        Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice=$device.Id;channel=1;requestId=($prefix + '-audio-' + $device.Id)}
        if ($device.Channels -ge 2) {
            Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice=$device.Id;channel=2;requestId=($prefix + '-right-' + $device.Id)}
        }
    }
    Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice='MISSING-DEVICE';requestId=($prefix + '-missing-audio')}
    if ($devices.Count -gt 0) {
        Wait-Event 'AudioPlaybackStarted' ($prefix + '-audio-' + $devices[0].Id)
        Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice=$devices[0].Id;channel=1;volume=25;requestId=($prefix + '-audio-overlap')}
        Wait-Event 'AudioPlaybackStarted' ($prefix + '-audio-overlap')
        $overlapEvents = Read-Events
        if ($overlapEvents | Where-Object { $_.Event -eq 'AudioPlaybackCompleted' -and $_.RequestId -eq ($prefix + '-audio-' + $devices[0].Id) }) { throw 'Original track ended before the overlap check.' }
        $overlapStarted = $overlapEvents | Where-Object { $_.Event -eq 'AudioPlaybackStarted' -and $_.RequestId -eq ($prefix + '-audio-overlap') } | Select-Object -Last 1
        if ($overlapStarted.Data.Volume -ne 25 -or $overlapStarted.Data.ActivePlaybacks -lt 2) { throw 'Per-request volume or overlapping playback was not applied.' }
        # Retrying one audio request must not create another overlapping track.
        Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice=$devices[0].Id;channel=1;volume=25;requestId=($prefix + '-audio-overlap')}
        Wait-Event 'AudioPlaybackCompleted' ($prefix + '-audio-overlap')
    }
    foreach ($device in $devices) {
        Wait-Event 'AudioPlaybackCompleted' ($prefix + '-audio-' + $device.Id)
        if ($device.Channels -ge 2) { Wait-Event 'AudioPlaybackCompleted' ($prefix + '-right-' + $device.Id) }
    }
    Wait-Event 'PlaybackStopped' ($prefix + '-replacement')
    $events = Read-Events
    foreach ($confirmed in @($events | Where-Object Event -eq 'VideoPlaybackConfirmed')) {
        if ($confirmed.Data.SurfaceOpacity -ne 0 -or $confirmed.Data.PositionSeconds -le 0) { throw 'Video did not advance while fully transparent.' }
    }
    foreach ($stopped in @($events | Where-Object Event -eq 'PlaybackStopped')) {
        if ($stopped.Data.SurfaceOpacity -ne 0) { throw 'Video was closed before becoming fully transparent.' }
        $fade = $events | Where-Object { $_.RequestId -eq $stopped.RequestId -and $_.Event -eq 'VideoFadeOutStarted' } | Select-Object -Last 1
        if ($stopped.Data.Reason -ne 'Replaced' -and $VideoFadeSeconds -gt 0 -and (-not $fade -or $stopped.Data.PositionSeconds -le $fade.Data.PositionSeconds)) { throw 'Video did not continue advancing during fade-out.' }
        Assert-Event $events 'VideoTransparentFramePresented' $stopped.RequestId
    }
    Assert-Event $events 'ExperienceBlockedMonitorUnavailable' ($prefix + '-missing-default')
    Assert-Event $events 'VideoPlaybackConfirmed' ($prefix + '-replacement')
    Assert-Event $events 'UdpDuplicateSuppressed' $duplicateId
    if(@($events|Where-Object { $_.Event -eq 'VideoOpening' -and $_.RequestId -eq $duplicateId }).Count -ne 1){throw 'A UDP retry restarted playback.'}
    Assert-Event $events 'PlaybackStopped' ($prefix + '-video-' + $monitors[0].Name)
    foreach ($monitor in $monitors) { Assert-Event $events 'VideoPlaybackConfirmed' ($prefix + '-video-' + $monitor.Name) }
    foreach ($selected in @($events | Where-Object Event -eq 'VideoOutputSelected')) {
        if ($selected.Data.RequestedMonitorName -like 'VerificationMonitor*') {
            $expected = $config.MonitorAliases[$selected.Data.RequestedMonitorName]
            if ($selected.Data.MonitorName -ne $expected) { throw 'An alias selected the wrong monitor.' }
        }
    }
    foreach ($device in $devices) {
        Assert-Event $events 'AudioPlaybackStarted' ($prefix + '-audio-' + $device.Id)
        Assert-Event $events 'AudioPlaybackCompleted' ($prefix + '-audio-' + $device.Id)
        if ($device.Channels -ge 2) { Assert-Event $events 'AudioPlaybackCompleted' ($prefix + '-right-' + $device.Id) }
    }
    if ($devices.Count -gt 0) {
        Assert-Event $events 'AudioPlaybackCompleted' ($prefix + '-audio-overlap')
        Assert-Event $events 'UdpDuplicateSuppressed' ($prefix + '-audio-overlap')
        if (@($events | Where-Object { $_.Event -eq 'AudioPlaybackStarted' -and $_.RequestId -eq ($prefix + '-audio-overlap') }).Count -ne 1) { throw 'An audio retry created an extra track.' }
        if ($events | Where-Object Event -eq 'AudioPlaybackReplaced') { throw 'An audio request replaced an existing track.' }
    }
    Assert-Event $events 'AudioDeviceUnavailable' ($prefix + '-missing-audio')
    if (-not ($events | Where-Object { $_.Event -eq 'UdpRequestInvalid' -and $_.Data.Reason -like '*Only video and audio*' })) { throw 'Removed stop command was not rejected.' }
    if ($events | Where-Object Level -eq 'Error') { throw 'Errors recorded during live verification.' }
    if ($events | Where-Object { $_.Event -eq 'AudioPlaybackCompleted' -and $_.Data.PositionSeconds -gt 4.01 }) { throw 'Audio exceeded its configured maximum.' }
    "Passed per-monitor windows ($($monitors.Count)), concurrent video, video replacement, independent audio devices ($($devices.Count))/channels, same-channel audio overlap, per-track volume, retry suppression, unavailable outputs and removed stop command."
} finally {
    Set-Content -LiteralPath $configPath -Value $saved
    $client.Dispose()
    Remove-Item -LiteralPath $tone -ErrorAction SilentlyContinue
    $cameraRuntime = Join-Path $PSScriptRoot '..\Camera\bin\Debug\net9.0-windows\win-x64'
    if (-not (Get-Process Camera -ErrorAction SilentlyContinue)) {
        Start-Process (Join-Path $cameraRuntime 'Camera.exe') -WorkingDirectory $cameraRuntime -WindowStyle Hidden
    }
}
