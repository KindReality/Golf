param(
    [string]$ComputerName = '127.0.0.1',
    [int]$Port = 21324,
    [ValidateSet('video', 'audio')][string]$Experience = 'video',
    [string]$Value = 'ping.mp4',
    [string]$MonitorName,
    [string]$AudioDevice,
    [ValidateRange(1,128)][int]$Channel,
    [switch]$Mute
)

$request = @{ experience = $Experience; value = $Value; mute = [bool]$Mute }
if ($MonitorName) { $request.monitorName = $MonitorName }
if ($AudioDevice) { $request.audioDevice = $AudioDevice }
if ($PSBoundParameters.ContainsKey('Channel')) { $request.channel = $Channel }
$message = $request | ConvertTo-Json -Compress
$bytes = [System.Text.Encoding]::UTF8.GetBytes($message)
$client = [System.Net.Sockets.UdpClient]::new()
try {
    [void]$client.Send($bytes, $bytes.Length, $ComputerName, $Port)
} finally {
    $client.Dispose()
}
