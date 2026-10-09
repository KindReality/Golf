param([ValidateRange(1,40)][int]$SampleSeconds = 10, [string]$Label = 'sample', [string]$PresentMonPath, [ValidateSet('Experience','ExperienceX')][string]$Application='ExperienceX')
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot 'benchmark-results'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$applicationDirectory=if ($Application -eq 'ExperienceX') { $PSScriptRoot } else { Join-Path $PSScriptRoot '..\Experience' }; $runtimeDirectory = Join-Path $applicationDirectory 'bin\Debug\net9.0-windows'
$configPath = Join-Path $runtimeDirectory 'appsettings.json'
if($Application -eq 'ExperienceX') {
    . (Join-Path $PSScriptRoot 'Get-ConfigurationPath.ps1')
    $configPath=Get-ExperienceXConfigurationPath
}
$savedConfig = Get-Content -LiteralPath $configPath -Raw
$results = [Collections.Generic.List[object]]::new()
$sampleFile = Join-Path $outputDirectory ('samples-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$gpuName = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
$client = [Net.Sockets.UdpClient]::new()
. (Join-Path $PSScriptRoot 'Broadcast-Protocol.ps1')
$client.EnableBroadcast=$true
function Send-BenchmarkRequest($message) {
    $configuration=$savedConfig|ConvertFrom-Json
    if($Application -eq 'ExperienceX') {
        $bytes=ConvertTo-ExperienceBroadcastBytes -Request $message -Configuration $configuration
        [void]$client.Send($bytes,$bytes.Length,'255.255.255.255',$configuration.UdpPort)
    } else {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Compress))
        [void]$client.Send($bytes,$bytes.Length,'127.0.0.1',$configuration.UdpPort)
    }
}
try {
    $process = Get-Process -Name $Application -ErrorAction SilentlyContinue
    if (-not $process) {
        $process = Start-Process -FilePath (Join-Path $runtimeDirectory ($Application + '.exe')) -WorkingDirectory $runtimeDirectory -WindowStyle Hidden -PassThru
    }
    foreach ($case in @(@{Name='Plain';BlackKey=$false;Keystone=$false},@{Name='BlackKey';BlackKey=$true;Keystone=$false},@{Name='BlackKeyAndKeystone';BlackKey=$true;Keystone=$true})) {
        $configuration = $savedConfig | ConvertFrom-Json
        $configuration.BlackKey.Enabled = $case.BlackKey
        $configuration.Keystone.Enabled = $case.Keystone
        if ($case.Keystone) {
            $configuration.Keystone.TopLeft.X = 0.1
            $configuration.Keystone.TopRight.X = 0.9
        }
        if ($Application -eq 'ExperienceX') {
            $configuration.configurationMode=$false
            foreach ($display in $configuration.DisplayKeystones.PSObject.Properties) {
                $display.Value=$configuration.Keystone | ConvertTo-Json -Depth 8 | ConvertFrom-Json
            }
        }
        $configuration.MaxVideoPlaybackLength = $SampleSeconds + 10
        $configuration | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $configPath
        [Threading.Thread]::Sleep(2000)
        $requestId = 'benchmark-' + $case.Name + '-' + [Guid]::NewGuid().ToString('N')
        Send-BenchmarkRequest @{experience='video';value='ping.mp4';requestId=$requestId;source='Benchmark';mute=$true}
        [Threading.Thread]::Sleep(6500)
        $process.Refresh()
        $presentationFile = $null
        $profiler = $null
        if ($PresentMonPath) {
            $presentationFile = Join-Path $outputDirectory ($Label + '-' + $requestId + '-presents.csv')
            $profiler = Start-Process -FilePath $PresentMonPath -WindowStyle Hidden -PassThru -ArgumentList @('--process_id', $process.Id, '--output_file', ('"' + $presentationFile + '"'), '--timed', $SampleSeconds, '--terminate_after_timed', '--no_console_stats', '--no_track_input', '--session_name', $requestId) -RedirectStandardError ($presentationFile + '.stderr.txt') -RedirectStandardOutput ($presentationFile + '.stdout.txt')
        }
        $cpuBefore = $process.TotalProcessorTime.TotalSeconds
        $wall = [Diagnostics.Stopwatch]::StartNew()
        $gpuSamples = @()
        $counterError = $null
        try {
            $counterPath = '\GPU Engine(pid_' + $process.Id + '_*)\Utilization Percentage'
            $gpuSamples = @(Get-Counter -Counter $counterPath -SampleInterval 1 -MaxSamples $SampleSeconds -ErrorAction Stop)
        } catch {
            $counterError = $_.Exception.Message
            if ($wall.Elapsed.TotalSeconds -lt $SampleSeconds) { [Threading.Thread]::Sleep([int](1000 * ($SampleSeconds - $wall.Elapsed.TotalSeconds))) }
        }
        $wall.Stop()
        if ($profiler) { $profiler.WaitForExit(10000) | Out-Null }
        $process.Refresh()
        $cpu = 100 * ($process.TotalProcessorTime.TotalSeconds - $cpuBefore) / $wall.Elapsed.TotalSeconds / [Environment]::ProcessorCount
        $engines = @{}
        foreach ($engine in @('3D','VideoDecode','VideoProcessing','Copy')) {
            $totals = @($gpuSamples | ForEach-Object {
                $matching = @($_.CounterSamples | Where-Object { $_.InstanceName -match ('engtype_' + $engine + '$') -and $_.Status -eq 0 })
                if ($matching.Count -gt 0) { ($matching | Measure-Object CookedValue -Sum).Sum }
            })
            $engines[$engine] = if ($totals.Count -gt 0) { ($totals | Measure-Object -Average).Average } else { $null }
        }
        # No stop command: allow the playback deadline to produce the summary.
        [Threading.Thread]::Sleep(5000)
        $log = Get-ChildItem (Join-Path $runtimeDirectory ('logs\' + $Application)) -Filter '*.jsonl' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $events = @(Get-Content -LiteralPath $log.FullName | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object RequestId -eq $requestId)
        $performance = $events | Where-Object Event -eq $(if ($Application -eq 'ExperienceX') {'DirectXPlaybackPerformance'} else {'PlaybackPerformance'}) | Select-Object -Last 1
        if (-not $performance) { throw ('No playback measurements were recorded for ' + $case.Name) }
        $capabilities = $events | Where-Object Event -eq 'DirectXRendererReady' | Select-Object -Last 1
        $presentEvents = if ($presentationFile -and (Test-Path -LiteralPath $presentationFile)) { @(Import-Csv -LiteralPath $presentationFile) } else { @() }
        $presentRows = $presentEvents.Count
        $displayGaps = @($presentEvents | Where-Object { $_.MsBetweenDisplayChange -ne 'NA' } | ForEach-Object { [double]$_.MsBetweenDisplayChange } | Sort-Object)
        $presentationTiming = if ($displayGaps.Count) { [pscustomobject]@{
            P95DisplayGapMilliseconds=$displayGaps[[Math]::Min($displayGaps.Count-1,[int]($displayGaps.Count*0.95))]
            MaxDisplayGapMilliseconds=($displayGaps|Measure-Object -Maximum).Maximum
            DisplayGapsOver50Milliseconds=@($displayGaps|Where-Object { $_ -gt 50 }).Count
            UndisplayedPresents=@($presentEvents|Where-Object MsUntilDisplayed -eq 'NA').Count
            Measurement='PresentMon display events; separate from decoded video frame drops'
        } } else { $null }
        $presentationStatus = if (-not $PresentMonPath) { 'Not requested' } elseif ($presentRows -gt 0) { 'Captured present events; these are not decoded video frames' } else { 'No present events captured; dropped video frames cannot be determined' }
        $result = [pscustomobject]@{Application=$Application;Label=$Label;PresentationCsv=$presentationFile;PresentEventCount=$presentRows;PresentationStatus=$presentationStatus;PresentationTiming=$presentationTiming;ProfilerExitCode=$(if ($profiler -and $profiler.HasExited) {$profiler.ExitCode});Case=$case.Name;RequestId=$requestId;SampleSeconds=$wall.Elapsed.TotalSeconds;
            CpuPercentOfMachine=$cpu;WorkingSetMiB=$process.WorkingSet64 / 1MB;GpuEnginePercent=$engines;
            GpuCounterError=$counterError;RenderingCapabilities=$capabilities.Data;RenderTiming=$performance.Data;
            Errors=@($events | Where-Object Level -eq 'Error')}
        $results.Add($result)
        [pscustomobject]@{Machine=[Environment]::MachineName;LogicalProcessors=[Environment]::ProcessorCount;Gpu=$gpuName;Results=$results.ToArray()} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $sampleFile
        Write-Output ($result | ConvertTo-Json -Depth 10 -Compress)
    }
} finally {
    $client.Dispose()
    Set-Content -LiteralPath $configPath -Value $savedConfig
}
Write-Output ('Results saved to ' + $sampleFile)
