param([ValidateRange(1,40)][int]$SampleSeconds = 10, [string]$Label = 'sample', [string]$PresentMonPath)
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot 'benchmark-results'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$runtimeDirectory = Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows'
$configPath = Join-Path $runtimeDirectory 'appsettings.json'
$savedConfig = Get-Content -LiteralPath $configPath -Raw
$results = [Collections.Generic.List[object]]::new()
$sampleFile = Join-Path $outputDirectory ('samples-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$gpuName = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
$client = [Net.Sockets.UdpClient]::new()
function Send-BenchmarkRequest($message) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Compress))
    [void]$client.Send($bytes,$bytes.Length,'127.0.0.1',21324)
}
try {
    $process = Get-Process -Name Experience -ErrorAction SilentlyContinue
    if (-not $process) {
        $process = Start-Process -FilePath (Join-Path $runtimeDirectory 'Experience.exe') -WorkingDirectory $runtimeDirectory -WindowStyle Hidden -PassThru
    }
    foreach ($case in @(@{Name='Plain';BlackKey=$false;Keystone=$false},@{Name='BlackKey';BlackKey=$true;Keystone=$false},@{Name='BlackKeyAndKeystone';BlackKey=$true;Keystone=$true})) {
        $configuration = $savedConfig | ConvertFrom-Json
        $configuration.BlackKey.Enabled = $case.BlackKey
        $configuration.Keystone.Enabled = $case.Keystone
        if ($case.Keystone) {
            $configuration.Keystone.TopLeft.X = 0.1
            $configuration.Keystone.TopRight.X = 0.9
        }
        $configuration.MaxVideoPlaybackLength = $SampleSeconds + 10
        $configuration | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $configPath
        [Threading.Thread]::Sleep(2000)
        $requestId = 'benchmark-' + $case.Name + '-' + [Guid]::NewGuid().ToString('N')
        Send-BenchmarkRequest @{experience='video';value='ping.mp4';requestId=$requestId;source='Benchmark'}
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
        $log = Get-ChildItem (Join-Path $runtimeDirectory 'logs\Experience') -Filter '*.jsonl' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $events = @(Get-Content -LiteralPath $log.FullName | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object RequestId -eq $requestId)
        $performance = $events | Where-Object Event -eq 'PlaybackPerformance' | Select-Object -Last 1
        if (-not $performance) { throw ('No playback measurements were recorded for ' + $case.Name) }
        $capabilities = $events | Where-Object Event -eq 'PlaybackRenderingCapabilities' | Select-Object -Last 1
        $presentRows = if ($presentationFile -and (Test-Path -LiteralPath $presentationFile)) { @(Import-Csv -LiteralPath $presentationFile).Count } else { 0 }
        $presentationStatus = if (-not $PresentMonPath) { 'Not requested' } elseif ($presentRows -gt 0) { 'Captured present events; these are not decoded video frames' } else { 'No present events captured; dropped video frames cannot be determined' }
        $result = [pscustomobject]@{Label=$Label;PresentationCsv=$presentationFile;PresentEventCount=$presentRows;PresentationStatus=$presentationStatus;ProfilerExitCode=$(if ($profiler -and $profiler.HasExited) {$profiler.ExitCode});Case=$case.Name;RequestId=$requestId;SampleSeconds=$wall.Elapsed.TotalSeconds;
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
