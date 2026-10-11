$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($application in @('Controller','ExperienceX')) {
    if (Get-Process -Name $application -ErrorAction SilentlyContinue) { throw "Stop the real $application before running this fixture." }
    $fixture = Join-Path $PSScriptRoot ('restart-fixture-' + [Guid]::NewGuid().ToString('N'))
    $source = Join-Path $fixture "source & user's output"
    $temporary = Join-Path $fixture 'local temp'
    $cache = if ($application -eq 'Controller') { Join-Path $temporary 'Home Technologies\Controller\Application' } else { Join-Path $temporary 'Experience' }
    $project = if ($application -eq 'Controller') { 'Controller' } else { 'experiencex' }
    $script = if ($application -eq 'Controller') { 'Restart-Controller.ps1' } else { 'Restart-Experience.ps1' }
    New-Item -ItemType Directory -Path $source,$cache -Force | Out-Null
    try {
        $program = Join-Path $fixture 'Fixture.cs'
        @'
using System; using System.IO; using System.Threading;
class Fixture {
 static void Main() {
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"started.txt"),
   System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
  Thread.Sleep(Timeout.Infinite);
 }
}
'@ | Set-Content -LiteralPath $program
        & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $source ($application+'.exe'))) $program
        if ($LASTEXITCODE -ne 0) { throw 'Could not build isolated executable.' }
        Copy-Item -LiteralPath (Join-Path $workspace "$project\$script") -Destination $source
        Copy-Item -LiteralPath (Join-Path $workspace 'Shared\Updates\Stage-ApplicationUpdate.ps1') -Destination $source
        $names = @(($application+'.exe'), ($application+'.dll'), ($application+'.deps.json'), ($application+'.runtimeconfig.json'), 'appsettings.defaults.json', $script, 'Stage-ApplicationUpdate.ps1', 'runtimes\win-x64\native\dependency.dll')
        if ($application -eq 'Controller') { $names += 'appsettings.environment.json' }
        foreach ($name in $names) {
            $path = Join-Path $source $name
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            if (!(Test-Path -LiteralPath $path)) { [IO.File]::WriteAllText($path, 'version-one') }
        }
        [IO.File]::WriteAllText((Join-Path $source ($application+'.runtimeconfig.json')), '{"runtimeOptions":{"tfm":"net9.0"}}')
        [void][IO.Directory]::CreateDirectory((Join-Path $cache 'Media'))
        [IO.File]::WriteAllText((Join-Path $cache 'Media\keep.mp4'), 'keep local media')
        [IO.File]::WriteAllText((Join-Path $cache 'appsettings.json'), 'keep local configuration')
        [IO.File]::WriteAllText((Join-Path $source 'appsettings.TEST.json'), 'keep OneDrive configuration')
        function Run-Restart([switch]$AutomaticUpdate) {
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = $powershell
            $start.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $source $script) + '" -SourceDirectory "' + $source + '"'
            if ($AutomaticUpdate) { $start.Arguments += ' -AutomaticUpdate' }
            $start.UseShellExecute = $false; $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
            $start.EnvironmentVariables['TEMP'] = $temporary
            $start.EnvironmentVariables['TMP'] = $temporary
            $start.EnvironmentVariables['LOCALAPPDATA'] = $temporary
            $process = [Diagnostics.Process]::Start($start)
            if (!$process.WaitForExit(20000)) { $process.Kill(); throw 'Restart fixture timed out.' }
            $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
            return [pscustomobject]@{Code=$process.ExitCode; Output=$output}
        }
        function Write-Manifest {
            $lines = foreach ($name in $names) { (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash + '|' + $name }
            [IO.File]::WriteAllText((Join-Path $source 'build-manifest.sha256'), ($lines -join "`n"))
        }
        $first = Run-Restart
        if ($first.Code -ne 0) { throw $first.Output }
        $firstId = [int][IO.File]::ReadAllText((Join-Path $cache 'started.txt'))
        [IO.File]::WriteAllText((Join-Path $source ($application+'.dll')), 'version-two')
        [IO.File]::WriteAllText((Join-Path $source 'runtimes\win-x64\native\dependency.dll'), 'version-two')
        Write-Manifest
        [IO.File]::WriteAllText((Join-Path $source 'runtimes\win-x64\native\dependency.dll'), 'incomplete sync')
        $incomplete = Run-Restart -AutomaticUpdate
        if ($incomplete.Code -eq 0 -or !(Get-Process -Id $firstId -ErrorAction SilentlyContinue)) { throw 'Partial sync stopped the existing app.' }
        if ([IO.File]::ReadAllText((Join-Path $cache ($application+'.dll'))) -ne 'version-one') { throw 'Partial sync modified the runtime.' }
        [IO.File]::WriteAllText((Join-Path $source 'runtimes\win-x64\native\dependency.dll'), 'version-two')
        $complete = Run-Restart -AutomaticUpdate
        if ($complete.Code -ne 0) { throw $complete.Output }
        $secondId = [int][IO.File]::ReadAllText((Join-Path $cache 'started.txt'))
        if ($secondId -eq $firstId -or (Get-Process -Id $firstId -ErrorAction SilentlyContinue)) { throw 'Old executable was not restarted.' }
        foreach ($name in @(($application+'.dll'), 'runtimes\win-x64\native\dependency.dll')) {
            if ([IO.File]::ReadAllText((Join-Path $cache $name)) -ne 'version-two') { throw 'Latest package was not installed.' }
        }
        if ([IO.File]::ReadAllText((Join-Path $cache 'Media\keep.mp4')) -ne 'keep local media' -or
            [IO.File]::ReadAllText((Join-Path $cache 'appsettings.json')) -ne 'keep local configuration' -or
            (Test-Path -LiteralPath (Join-Path $cache 'appsettings.TEST.json'))) { throw 'Update copied or changed user media/configuration.' }
        if (@(Get-ChildItem -LiteralPath $temporary -Directory -Filter 'HomeTechnologies-Update-*').Count) { throw 'Temporary update stage was not removed.' }
        Write-Output "PASS: $application manual restart, rejected partial update before stopping, verified automatic restart, configuration/media retention and staging cleanup (Windows PowerShell 5.1)."
    } finally {
        foreach ($process in @(Get-Process -Name $application -ErrorAction SilentlyContinue)) {
            if ($process.Path -and $process.Path.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force
                [void]$process.WaitForExit(5000)
            }
        }
        $resolved = [IO.Path]::GetFullPath($fixture)
        if (!$resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped its workspace folder.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
