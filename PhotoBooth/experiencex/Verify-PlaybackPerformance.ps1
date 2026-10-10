$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
. (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$path=Get-ExperienceXConfigurationPath
$saved=[IO.File]::ReadAllText($path)
$app=Get-Process ExperienceX -ErrorAction Stop
$client=[Net.Sockets.UdpClient]::new()
$client.EnableBroadcast=$true
$prefix='improvements-'+[Guid]::NewGuid().ToString('N')
function Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\ExperienceX') -Filter "*-$($app.Id)-*.jsonl" | ForEach-Object {Get-Content $_.FullName | ForEach-Object {$_|ConvertFrom-Json}})
}
try {
    Add-Type -AssemblyName System.Windows.Forms
    $config=$saved|ConvertFrom-Json
    $config.configurationMode=$false
    foreach($display in $config.DisplayScreens.PSObject.Properties){foreach ($screen in $display.Value) { $screen.Points.Enabled=$true }}
    $config|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $path
    Start-Sleep -Milliseconds 1200
    $displays=@([Windows.Forms.Screen]::AllScreens|ForEach-Object DeviceName)
    foreach($display in $displays) {
        $bytes=ConvertTo-ExperienceBroadcastBytes -Request @{experience='video';value='ping.mp4';displayName=$display;mute=$true;requestId=($prefix+'-'+$display)} -Configuration $config
        [void]$client.Send($bytes,$bytes.Length,'255.255.255.255',$config.UdpCommandsPort)
    }
    Start-Sleep -Milliseconds 4500
    $app.Refresh();$cpuBefore=$app.TotalProcessorTime.TotalSeconds;$clock=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 4
    $app.Refresh();$clock.Stop()
    $cpu=100*($app.TotalProcessorTime.TotalSeconds-$cpuBefore)/$clock.Elapsed.TotalSeconds/[Environment]::ProcessorCount
    $waitUntil = [DateTime]::UtcNow.AddMinutes(5)
    do {
        $own=@(Events|Where-Object {$_.RequestId -like ($prefix+'*')})
        $completed=@($own | Where-Object Event -eq 'PlaybackStopped' | Select-Object -ExpandProperty RequestId -Unique)
        if ($completed.Count -lt $displays.Count) { Start-Sleep -Milliseconds 200 }
    } while ($completed.Count -lt $displays.Count -and [DateTime]::UtcNow -lt $waitUntil)
    $performance=@($own|Where-Object Event -eq DirectXPlaybackPerformance|Group-Object RequestId|ForEach-Object {$_.Group|Select-Object -Last 1})
    if($performance.Count -ne $displays.Count){throw 'Missing playback performance for a connected display.'}
    if(@($own|Where-Object Event -eq PlaybackStopped).Count -ne $displays.Count){throw 'Concurrent playback did not finish on all displays.'}
    if($own|Where-Object Level -eq Error){throw 'Errors recorded during performance verification.'}
    $rows=@($performance|ForEach-Object {
        $final=$_
        $first=$own|Where-Object {$_.Event -eq 'DirectXPlaybackPerformance' -and $_.RequestId -eq $final.RequestId}|Select-Object -First 1
        [pscustomobject]@{DisplayName=$final.Data.DisplayName;TransferredFrames=$final.Data.TransferredVideoFrames;SubmittedPresents=$final.Data.SubmittedPresents;TransformComputations=$final.Data.TransformComputations;AdditionalTransformsAfterWarmup=($final.Data.TransformComputations-$first.Data.TransformComputations);DecoderDrops=$final.Data.MediaEngineFramesDropped}
    })
    if($rows|Where-Object {$_.AdditionalTransformsAfterWarmup -gt 1}){throw 'Unchanged geometry was repeatedly recomputed after warmup.'}
    [pscustomobject]@{CpuPercentOfPc=$cpu;SampleSeconds=$clock.Elapsed.TotalSeconds;Displays=$rows;Note='Present submissions and decoder counters do not prove smooth physical display presentation.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $PSScriptRoot '..\.tools\improvements-performance.json')
    $rows|Format-Table -AutoSize
    "ExperienceX CPU during simultaneous playback: $([Math]::Round($cpu,2))% of this PC."
} finally {[IO.File]::WriteAllText($path,$saved);$client.Dispose();Start-Sleep -Milliseconds 1200}
