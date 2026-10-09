param(
    [Alias('ComputerName')][string]$BroadcastAddress = '255.255.255.255',
    [string]$InterfaceAddress,
    [string]$Device = [Environment]::MachineName,
    [ValidateRange(1,65535)][int]$Port = 21325,
    [ValidateSet('video', 'audio')][string]$Experience = 'video',
    [string]$Value = 'ping.mp4',
    [string]$MonitorName,
    [string]$AudioDevice,
    [string[]]$AudioDevices,
    [ValidateRange(1,128)][int]$Channel,
    [ValidateRange(0,100)][double]$Volume,
    [switch]$Mute
)

. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$request = @{ type = 'command'; device = $Device; experience = $Experience; value = $Value; mute = [bool]$Mute }
if ($AudioDevice -and $AudioDevices) { throw 'Use either AudioDevice or AudioDevices.' }
if ($MonitorName) { $request.monitorName = $MonitorName }
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
