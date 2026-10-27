param(
    [Alias('ComputerName')][string]$BroadcastAddress = '255.255.255.255',
    [string]$InterfaceAddress,
    [string]$Device = [Environment]::MachineName,
    [ValidateRange(1,65535)][int]$Port = 54243,
    [ValidateSet('video', 'audio')][string]$Experience = 'video',
    [ValidateSet('play', 'fadeIn', 'fadeOut')][string]$Command = 'play',
    [ValidateRange(0,86400)][double]$DurationSeconds,
    [string]$Value = 'ping.mp4',
    [ValidateRange(0.5,2)][double]$PlaybackSpeed = 1,
    [string]$DisplayName,
    [string]$Screen,
    [string]$Roles,
    [string]$AudioDevice,
    [string[]]$AudioDevices,
    [ValidateRange(1,128)][int]$Channel,
    [ValidateRange(0,100)][double]$Volume,
    [switch]$Mute,
    [switch]$Loop
)

. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$request = @{ type = 'command'; device = $Device; experience = $Experience; value = $Value; mute = [bool]$Mute }
if ($PSBoundParameters.ContainsKey('PlaybackSpeed')) {
    if ($Experience -ne 'video' -or $Command -ne 'play' -or [double]::IsNaN($PlaybackSpeed)) { throw 'PlaybackSpeed applies only to video playback.' }
    $request.playbackSpeed = $PlaybackSpeed
}
if ($Command -ne 'play') {
    if ($Experience -ne 'video' -or $Loop -or !$PSBoundParameters.ContainsKey('DurationSeconds') -or [double]::IsNaN($DurationSeconds)) {
        throw 'Video fadeIn/fadeOut requires DurationSeconds from 0 to 86400 and cannot loop.'
    }
    $request.Remove('value'); $request.Remove('mute')
    $request.command = $Command; $request.durationSeconds = $DurationSeconds
} elseif ($PSBoundParameters.ContainsKey('DurationSeconds')) { throw 'DurationSeconds is used only by fadeIn/fadeOut.' }
if ($Roles) {
    if ($Screen -or $DisplayName -or $AudioDevice -or $PSBoundParameters.ContainsKey('AudioDevices')) { throw 'Roles cannot include direct screen/display/audio targets.' }
    $request.roles = $Roles
}
if ($Loop) { if ($Experience -ne 'video') { throw 'Loop is supported for video only.' }; $request.loop = $true }
if ($AudioDevice -and $AudioDevices) { throw 'Use either AudioDevice or AudioDevices.' }
if ($DisplayName) { $request.displayName = $DisplayName }
if ($Screen) { $request.screen = $Screen }
if ($AudioDevice) { $request.audioDevice = $AudioDevice }
if ($PSBoundParameters.ContainsKey('AudioDevices')) { $request.audioDevices = @($AudioDevices) }
if ($PSBoundParameters.ContainsKey('Channel')) { $request.channel = $Channel }
if ($PSBoundParameters.ContainsKey('Volume')) { $request.volume = $Volume }
$bytes = ConvertTo-ExperienceBroadcastBytes -Request $request
$client = [System.Net.Sockets.UdpClient]::new()
try {
    $client.EnableBroadcast = $true
    if ($InterfaceAddress) { $client.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Parse($InterfaceAddress),0)) }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [void]$client.Send($bytes, $bytes.Length, $BroadcastAddress, $Port)
        if ($attempt -lt 2) { [Threading.Thread]::Sleep(75) }
    }
    [pscustomobject]@{ Device=$Device; BroadcastAddress=$BroadcastAddress; Port=$Port; RequestId=$request.requestId; Attempts=3 }
} finally {
    $client.Dispose()
}
