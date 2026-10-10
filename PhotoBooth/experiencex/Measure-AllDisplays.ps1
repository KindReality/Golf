param([ValidateRange(10,40)][int]$SampleSeconds=20,
    [string]$PresentMonPath=(Join-Path $PSScriptRoot '..\.tools\PresentMon\PresentMon.exe'))
$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
. (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$configPath=Get-ExperienceXConfigurationPath
$saved=[IO.File]::ReadAllText($configPath)
$directory=Join-Path $PSScriptRoot 'benchmark-results'
[void](New-Item $directory -ItemType Directory -Force)
$output=Join-Path $directory ('all-displays-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
$client=[Net.Sockets.UdpClient]::new()
$client.EnableBroadcast=$true
$results=[Collections.Generic.List[object]]::new()
function Events {
    @(Get-ChildItem (Join-Path $runtime 'logs\ExperienceX') -Filter "*-$($app.Id)-*.jsonl" | ForEach-Object {
        Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    })
}
function Send($message) {
    $configuration=$saved|ConvertFrom-Json
    $bytes=ConvertTo-ExperienceBroadcastBytes -Request $message -Configuration $configuration
    [void]$client.Send($bytes,$bytes.Length,'255.255.255.255',$configuration.UdpCommandsPort)
}
try {
    $app=Get-Process ExperienceX -ErrorAction SilentlyContinue
    if (-not $app) { $app=Start-Process (Join-Path $runtime 'ExperienceX.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru; Start-Sleep -Seconds 3 }
    if (@($app).Count -ne 1) { throw 'Expected one ExperienceX instance.' }
    if (-not (Test-Path $PresentMonPath)) { throw 'PresentMon executable is required for this test.' }
    Add-Type -AssemblyName System.Windows.Forms
    $displays=@([Windows.Forms.Screen]::AllScreens | ForEach-Object DeviceName)
    foreach ($case in @(@{Name='Plain';Key=$false;Warp=$false},@{Name='BlackKey';Key=$true;Warp=$false},@{Name='BlackKeyAndKeystone';Key=$true;Warp=$true})) {
        $config=$saved|ConvertFrom-Json
        $config.BlackKey.Enabled=$case.Key; $config.Points.Enabled=$case.Warp
        if ($case.Warp) { $config.Points.Vertices[0].X=0.1; $config.Points.Vertices[1].X=0.9 }
        $config.configurationMode=$false
        foreach ($display in $config.DisplayScreens.PSObject.Properties) {
            foreach ($screen in $display.Value) { $screen.Points=$config.Points | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
        }
        $config|ConvertTo-Json -Depth 16|Set-Content $configPath
        Start-Sleep -Seconds 2
        $prefix='all-'+$case.Name+'-'+[Guid]::NewGuid().ToString('N')
        $requests=@{}
        foreach ($display in $displays) {
            $id=$prefix+'-'+$requests.Count; $requests[$id]=$display
            Send @{experience='video';value='ping.mp4';displayName=$display;mute=$true;requestId=$id;source='AllDisplayBenchmark'}
        }
        Start-Sleep -Seconds 7
        $beforeEvents=Events
        foreach ($id in $requests.Keys) {
            if (-not ($beforeEvents|Where-Object { $_.RequestId -eq $id -and $_.Event -eq 'VideoPlaybackConfirmed' })) { throw "Playback did not start on $($requests[$id])." }
            if ($beforeEvents|Where-Object { $_.RequestId -eq $id -and $_.Event -eq 'PlaybackStopped' }) { throw 'Video stopped before simultaneous sampling.' }
        }
        $csv=Join-Path $directory ($prefix+'-presents.csv')
        $profiler=Start-Process $PresentMonPath -WindowStyle Hidden -PassThru -ArgumentList @('--process_id',$app.Id,'--output_file',('"'+$csv+'"'),'--timed',$SampleSeconds,'--terminate_after_timed','--no_console_stats','--no_track_input','--session_name',$prefix) -RedirectStandardError ($csv+'.stderr.txt') -RedirectStandardOutput ($csv+'.stdout.txt')
        $captureStart=[DateTimeOffset]::UtcNow
        $app.Refresh(); $cpuBefore=$app.TotalProcessorTime.TotalSeconds
        $clock=[Diagnostics.Stopwatch]::StartNew()
        $gpu=@(); $counterError=$null
        try { $gpu=@(Get-Counter -Counter ('\GPU Engine(pid_'+$app.Id+'_*)\Utilization Percentage') -SampleInterval 1 -MaxSamples $SampleSeconds) }
        catch { $counterError=$_.Exception.Message; if ($clock.Elapsed.TotalSeconds -lt $SampleSeconds) { Start-Sleep -Milliseconds ([int](1000*($SampleSeconds-$clock.Elapsed.TotalSeconds))) } }
        $clock.Stop(); $app.Refresh()
        $captureEnd=[DateTimeOffset]::UtcNow
        $cpu=100*($app.TotalProcessorTime.TotalSeconds-$cpuBefore)/$clock.Elapsed.TotalSeconds/[Environment]::ProcessorCount
        if (-not $profiler.WaitForExit(10000)) { throw 'PresentMon did not complete.' }
        $waitUntil = [DateTime]::UtcNow.AddMinutes(5)
        do {
            $events=@(Events | Where-Object { $requests.ContainsKey([string]$_.RequestId) })
            $completed=@($events | Where-Object Event -eq 'PlaybackStopped' | Select-Object -ExpandProperty RequestId -Unique)
            if ($completed.Count -lt $requests.Count) { Start-Sleep -Milliseconds 200 }
        } while ($completed.Count -lt $requests.Count -and [DateTime]::UtcNow -lt $waitUntil)
        $perDisplay=@(foreach ($id in $requests.Keys) {
            $own=@($events|Where-Object RequestId -eq $id)
            $confirmed=$own|Where-Object Event -eq VideoPlaybackConfirmed|Select-Object -First 1
            $stopped=$own|Where-Object Event -eq PlaybackStopped|Select-Object -Last 1
            $performance=$own|Where-Object Event -eq DirectXPlaybackPerformance|Select-Object -Last 1
            $warm=$beforeEvents|Where-Object { $_.RequestId -eq $id -and $_.Event -eq 'DirectXPlaybackPerformance' }|Select-Object -Last 1
            if (-not $stopped -or -not $performance) { throw "Missing completion on $($requests[$id])." }
            if ([DateTimeOffset]$confirmed.Timestamp -gt $captureStart -or [DateTimeOffset]$stopped.Timestamp -lt $captureEnd) { throw 'A display was not playing for the entire capture interval.' }
            if ($confirmed.Data.SurfaceOpacity -ne 0 -or $confirmed.Data.PositionSeconds -le 0 -or $stopped.Data.SurfaceOpacity -ne 0) { throw 'Transparent start/completion check failed.' }
            [pscustomobject]@{DisplayName=$requests[$id];RequestId=$id;Confirmed=$confirmed.Timestamp;Stopped=$stopped.Timestamp;
                WarmupDecoderDrops=$warm.Data.MediaEngineFramesDropped;Final=$performance.Data}
        })
        $rows=@(Import-Csv $csv)
        $unattributed=@($rows|Where-Object SwapChainAddress -eq '0x0').Count
        $chains=@(foreach ($group in ($rows|Where-Object SwapChainAddress -ne '0x0'|Group-Object SwapChainAddress)) {
            $gaps=@($group.Group|Where-Object MsBetweenDisplayChange -ne NA|ForEach-Object {[double]$_.MsBetweenDisplayChange}|Sort-Object)
            [pscustomobject]@{SwapChain=$group.Name;Presents=$group.Count;Displayed=@($group.Group|Where-Object MsUntilDisplayed -ne NA).Count;
                Undisplayed=@($group.Group|Where-Object MsUntilDisplayed -eq NA).Count;
                P95DisplayGapMs=$(if($gaps.Count){$gaps[[Math]::Min($gaps.Count-1,[int]($gaps.Count*0.95))]});
                MaximumDisplayGapMs=($gaps|Measure-Object -Maximum).Maximum;DisplayGapsOver50Ms=@($gaps|Where-Object {$_ -gt 50}).Count}
        })
        if ($chains.Count -ne $displays.Count) { throw "Expected $($displays.Count) active swap chains; captured $($chains.Count)." }
        $engines=@{}
        foreach ($engine in @('3D','VideoDecode','VideoProcessing','Copy')) {
            $sums=@($gpu|ForEach-Object { $samples=@($_.CounterSamples|Where-Object { $_.InstanceName -match ('engtype_'+$engine+'$') -and $_.Status -eq 0 }); if ($samples.Count) {($samples|Measure-Object CookedValue -Sum).Sum} })
            $engines[$engine]=($sums|Measure-Object -Average).Average
        }
        $errors=@($events|Where-Object Level -eq Error)
        $result=[pscustomobject]@{Case=$case.Name;ProcessId=$app.Id;Video='ping.mp4';CaptureStart=$captureStart;CaptureEnd=$captureEnd;
            CpuPercentOfMachine=$cpu;WorkingSetMiB=$app.WorkingSet64/1MB;GpuEnginePercent=$engines;GpuCounterError=$counterError;
            Displays=$perDisplay;PresentationCsv=$csv;UnattributedPresentEvents=$unattributed;SwapChains=$chains;Errors=$errors}
        $results.Add($result)
        [pscustomobject]@{Machine=[Environment]::MachineName;SimultaneousDisplays=$displays;SampleSeconds=$SampleSeconds;Results=$results.ToArray()}|ConvertTo-Json -Depth 20|Set-Content $output
        $result|ConvertTo-Json -Depth 12 -Compress|Write-Output
        if ($errors.Count) { throw 'Errors were logged during simultaneous playback.' }
    }
}
finally { [IO.File]::WriteAllText($configPath,$saved); $client.Dispose() }
Write-Output "Results saved to $output"
