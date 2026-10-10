param(
    [Parameter(Mandatory)][string]$Event,
    [Parameter(Mandatory)][string]$Input,
    [string]$Device = '*',
    [string]$Source = [Environment]::MachineName,
    [string]$BroadcastAddress = '255.255.255.255',
    [string]$InterfaceAddress,
    [ValidateRange(1,65535)][int]$Port = 54242,
    [hashtable]$Data = @{}
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
if ([string]::IsNullOrWhiteSpace($Input) -or $Input.Trim().Length -gt 128) { throw 'A named input is required.' }
$request = @{ type='event'; device=$Device; source=$Source; input=$Input.Trim(); event=$Event; data=$Data }
$bytes = ConvertTo-ExperienceBroadcastBytes -Request $request
$client = [Net.Sockets.UdpClient]::new()
try {
    $client.EnableBroadcast = $true
    if ($InterfaceAddress) { $client.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Parse($InterfaceAddress),0)) }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [void]$client.Send($bytes,$bytes.Length,$BroadcastAddress,$Port)
        if ($attempt -lt 2) { [Threading.Thread]::Sleep(75) }
    }
    [pscustomobject]@{ Event=$Event; Device=$Device; RequestId=$request.requestId; Attempts=3 }
} finally { $client.Dispose() }
