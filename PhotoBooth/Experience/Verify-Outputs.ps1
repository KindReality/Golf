param([switch]$DisableBlackKey, [switch]$EnableKeystone, [switch]$SkipAudio, [ValidateRange(0,3600)][double]$VideoFadeSeconds = 0.5)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
$configPath = Join-Path $runtime 'appsettings.json'
$saved = Get-Content -LiteralPath $configPath -Raw
$client = [Net.Sockets.UdpClient]::new()
$prefix = 'outputs-' + [Guid]::NewGuid().ToString('N')
$tone = Join-Path $runtime ('Media\' + $prefix + '.wav')
function Send-Request($message) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Compress))
    [void]$client.Send($bytes,$bytes.Length,'127.0.0.1',21324)
}
function Read-Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\Experience') -Filter ("*-$($app.Id)-*.jsonl") | ForEach-Object {
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
    if (Get-Process Experience -ErrorAction SilentlyContinue) { throw 'Stop Experience before running this startup verification.' }
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
    $config.DefaultMonitorName = '\\.\DISPLAY-NOT-AVAILABLE-VERIFICATION'
    $config.MaxVideoPlaybackLength = 4; $config.MaxAudioPlaybackLength = 4
    $config.TransitionInSeconds = $VideoFadeSeconds; $config.TransitionOutSeconds = $VideoFadeSeconds
    $config.AudioTransitionInSeconds = 0.5; $config.AudioTransitionOutSeconds = 0.5
    $config | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $configPath
    $app = Start-Process (Join-Path $runtime 'Experience.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru
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
        Send-Request @{experience='audio';value=([IO.Path]::GetFileName($tone));audioDevice=$devices[0].Id;channel=1;mute=$true;requestId=($prefix + '-audio-replacement')}
        Wait-Event 'AudioPlaybackCompleted' ($prefix + '-audio-replacement')
    }
    foreach ($device in $devices) {
        if ($device.Channels -ge 2) { Wait-Event 'AudioPlaybackCompleted' ($prefix + '-right-' + $device.Id) }
    }
    Wait-Event 'PlaybackStopped' ($prefix + '-replacement')
    $events = Read-Events
    foreach ($confirmed in @($events | Where-Object Event -eq 'VideoPlaybackConfirmed')) {
        if ($confirmed.Data.SurfaceOpacity -ne 0 -or $confirmed.Data.PositionSeconds -le 0) { throw 'Video did not advance while fully transparent.' }
    }
    foreach ($stopped in @($events | Where-Object Event -eq 'PlaybackStopped')) {
        if ($stopped.Data.SurfaceOpacity -ne 0) { throw 'Video was closed before becoming fully transparent.' }
        $fade = $events | Where-Object { $_.RequestId -eq $stopped.RequestId -and $_.Event -eq 'FadeStarted' -and $_.Data.To -eq 0 } | Select-Object -Last 1
        if ($VideoFadeSeconds -gt 0 -and (-not $fade -or $stopped.Data.PositionSeconds -le $fade.Data.PositionSeconds)) { throw 'Video did not continue advancing during fade-out.' }
        Assert-Event $events 'VideoTransparent' $stopped.RequestId
    }
    Assert-Event $events 'ExperienceBlockedMonitorUnavailable' ($prefix + '-missing-default')
    Assert-Event $events 'MediaOpened' ($prefix + '-replacement')
    Assert-Event $events 'PlaybackReplaced' ($prefix + '-video-' + $monitors[0].Name)
    foreach ($monitor in $monitors) { Assert-Event $events 'MediaOpened' ($prefix + '-video-' + $monitor.Name) }
    foreach ($selected in @($events | Where-Object Event -eq 'VideoOutputSelected')) {
        if ($selected.Data.RequestedMonitorName -like 'VerificationMonitor*') {
            $expected = $config.MonitorAliases[$selected.Data.RequestedMonitorName]
            if ($selected.Data.MonitorName -ne $expected) { throw 'An alias selected the wrong monitor.' }
        }
    }
    foreach ($device in $devices) {
        Assert-Event $events 'AudioPlaybackStarted' ($prefix + '-audio-' + $device.Id)
        if ($device.Channels -ge 2) { Assert-Event $events 'AudioPlaybackCompleted' ($prefix + '-right-' + $device.Id) }
    }
    if ($devices.Count -gt 0) {
        Assert-Event $events 'AudioPlaybackReplaced' ($prefix + '-audio-' + $devices[0].Id)
        Assert-Event $events 'AudioPlaybackCompleted' ($prefix + '-audio-replacement')
    }
    Assert-Event $events 'AudioDeviceUnavailable' ($prefix + '-missing-audio')
    if (-not ($events | Where-Object { $_.Event -eq 'UdpRequestInvalid' -and $_.Data.Reason -like '*Only video and audio*' })) { throw 'Removed stop command was not rejected.' }
    if ($events | Where-Object Level -eq 'Error') { throw 'Errors recorded during live verification.' }
    if ($events | Where-Object { $_.Event -eq 'AudioPlaybackCompleted' -and $_.Data.PositionSeconds -gt 4.01 }) { throw 'Audio exceeded its configured maximum.' }
    "Passed per-monitor windows ($($monitors.Count)), concurrent video, replacement, independent audio devices ($($devices.Count))/channels, unavailable outputs and removed stop command."
} finally {
    Set-Content -LiteralPath $configPath -Value $saved
    $client.Dispose()
    Remove-Item -LiteralPath $tone -ErrorAction SilentlyContinue
    $cameraRuntime = Join-Path $PSScriptRoot '..\Camera\bin\Debug\net9.0-windows\win-x64'
    if (-not (Get-Process Camera -ErrorAction SilentlyContinue)) {
        Start-Process (Join-Path $cameraRuntime 'Camera.exe') -WorkingDirectory $cameraRuntime -WindowStyle Hidden
    }
}
