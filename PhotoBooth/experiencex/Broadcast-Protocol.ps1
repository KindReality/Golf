# Shared by the command-line sender and live verification scripts. No socket replies.
function ConvertTo-ExperienceBroadcastBytes {
    param([hashtable]$Request, $Configuration)
    if (!$Request.ContainsKey('type')) { $Request.type = 'command' }
    if (!$Request.ContainsKey('device')) {
        $Request.device = if ([string]::IsNullOrWhiteSpace($Configuration.DeviceName)) { [Environment]::MachineName } else { $Configuration.DeviceName.Trim() }
    }
    if (!$Request.ContainsKey('source')) { $Request.source = [Environment]::MachineName }
    if (!$Request.ContainsKey('requestId')) { $Request.requestId = [Guid]::NewGuid().ToString('N') }
    foreach ($name in @('device','source','requestId')) {
        if ([string]::IsNullOrWhiteSpace($Request[$name]) -or $Request[$name].Trim().Length -gt 128 -or $Request[$name] -match '[\x00-\x1F\x7F]') { throw "Invalid $name." }
    }
    if ($Request.source.Trim() -eq '*' -or $Request.requestId.Trim() -eq '*') { throw 'source/requestId cannot be *.' }
    if ($Request.type -notin @('command','event')) { throw 'type must be command or event.' }
    if ($Request.type -eq 'event' -and [string]::IsNullOrWhiteSpace($Request.event)) { throw 'An event name is required.' }
    if (!$Request.ContainsKey('processingTimeoutSeconds')) { $Request.processingTimeoutSeconds = 10 }
    if (!$Request.ContainsKey('hopCount')) { $Request.hopCount = 0 }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Depth 16 -Compress))
    if ($bytes.Length -gt 1200) { throw 'UDP message exceeds 1200 UTF-8 bytes. Use a configured virtual audio group for large output lists.' }
    # Prevent PowerShell from unrolling the byte array into individual output objects.
    return ,$bytes
}
