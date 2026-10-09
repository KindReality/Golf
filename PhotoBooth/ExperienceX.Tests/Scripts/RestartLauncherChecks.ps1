$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot)
if (Get-Process ExperienceX -ErrorAction SilentlyContinue) { throw 'Stop the real ExperienceX application before this fixture.' }
$fixture = Join-Path $PSScriptRoot ('restart-fixture-'+[Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture "source & user's output"
$tempRoot = Join-Path $fixture 'test temp'
$cache = Join-Path $tempRoot 'Experience'
[void][IO.Directory]::CreateDirectory($source)
[void][IO.Directory]::CreateDirectory($cache)
try {
    $program = Join-Path $fixture 'Fixture.cs'
    @'
using System; using System.IO; using System.Threading;
class Fixture {
 static void Main() {
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"started.txt"),
   System.Diagnostics.Process.GetCurrentProcess().Id+"|"+AppDomain.CurrentDomain.BaseDirectory);
  Thread.Sleep(Timeout.Infinite);
 }
}
'@ | Set-Content -LiteralPath $program -Encoding ascii
    $executable=Join-Path $source 'ExperienceX.exe'
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+$executable) $program
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compile the isolated launcher fixture.' }
    foreach ($name in @('ExperienceX.dll','ExperienceX.deps.json','ExperienceX.runtimeconfig.json','appsettings.defaults.json')) {
        [IO.File]::WriteAllText((Join-Path $source $name),'version-one')
    }
    foreach ($name in @('Restart-Experience.cmd','Restart-Experience.ps1')) {
        Copy-Item -LiteralPath (Join-Path $workspace ('experiencex\'+$name)) -Destination $source
    }
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json'),'source settings')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json.bak'),'source backup')
    [IO.File]::WriteAllText((Join-Path $cache 'appsettings.json'),'keep local settings')
    [IO.File]::WriteAllText((Join-Path $cache 'appsettings.json.bak'),'keep local backup')
    [IO.File]::WriteAllText((Join-Path $cache 'obsolete.dll'),'remove')
    [void][IO.Directory]::CreateDirectory((Join-Path $cache 'logs'))
    [IO.File]::WriteAllText((Join-Path $cache 'logs\history.txt'),'retain')
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'Media\nested'))
    [void][IO.Directory]::CreateDirectory((Join-Path $cache 'media\nested'))
    [IO.File]::WriteAllText((Join-Path $source 'Media\nested\clip.mp4'),'source media')
    [IO.File]::WriteAllText((Join-Path $source 'Media\source-only.mp4'),'do not copy')
    [IO.File]::WriteAllText((Join-Path $cache 'media\nested\clip.mp4'),'keep local media')
    [IO.File]::WriteAllText((Join-Path $cache 'media\cache-only.mp4'),'retain')
    $original = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 250
    function Run-Launcher {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $env:ComSpec
        $start.Arguments = '/d /s /c ""'+(Join-Path $source 'Restart-Experience.cmd')+'""'
        $start.WorkingDirectory = $fixture
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.RedirectStandardInput = $true
        $start.EnvironmentVariables['TEMP']=$tempRoot
        $start.EnvironmentVariables['TMP']=$tempRoot
        $command = [Diagnostics.Process]::Start($start)
        $command.StandardInput.Close()
        if (!$command.WaitForExit(15000)) { $command.Kill(); throw 'Launcher timed out.' }
        $output=$command.StandardOutput.ReadToEnd()+$command.StandardError.ReadToEnd()
        if ($command.ExitCode -ne 0) { throw $output }
        $report=([IO.File]::ReadAllText((Join-Path $cache 'started.txt'))).Split('|')
        if ($report[1].TrimEnd('\') -ine $cache) { throw 'Application did not launch from the temp cache.' }
        return [int]$report[0]
    }
    $firstId=Run-Launcher
    if (!$original.HasExited -or (Test-Path -LiteralPath (Join-Path $cache 'obsolete.dll'))) { throw 'Old instance or obsolete binary survived refresh.' }
    $unlocked=[IO.File]::Open($executable,'Open','ReadWrite','None'); $unlocked.Dispose()
    [IO.File]::WriteAllText((Join-Path $source 'ExperienceX.dll'),'version-two')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.defaults.json'),'version-two defaults')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json'),'new source settings')
    [IO.File]::WriteAllText((Join-Path $cache 'obsolete.dll'),'remove again')
    $secondId=Run-Launcher
    if ($firstId -eq $secondId -or (Get-Process -Id $firstId -ErrorAction SilentlyContinue)) { throw 'Restart did not replace the cached process.' }
    if ([IO.File]::ReadAllText((Join-Path $cache 'ExperienceX.dll')) -ne 'version-two' -or
        [IO.File]::ReadAllText((Join-Path $cache 'appsettings.defaults.json')) -ne 'version-two defaults') { throw 'Latest binaries/defaults were not copied.' }
    if ([IO.File]::ReadAllText((Join-Path $cache 'appsettings.json')) -ne 'keep local settings' -or
        [IO.File]::ReadAllText((Join-Path $cache 'appsettings.json.bak')) -ne 'keep local backup' -or
        [IO.File]::ReadAllText((Join-Path $cache 'logs\history.txt')) -ne 'retain') { throw 'Configuration, backup or logs were overwritten.' }
    if ([IO.File]::ReadAllText((Join-Path $cache 'media\nested\clip.mp4')) -ne 'keep local media' -or
        [IO.File]::ReadAllText((Join-Path $cache 'media\cache-only.mp4')) -ne 'retain' -or
        (Test-Path -LiteralPath (Join-Path $cache 'media\source-only.mp4'))) { throw 'Local media was changed or source media was copied.' }
    Remove-Item -LiteralPath (Join-Path $cache 'appsettings.json')
    $secondId=Run-Launcher
    if ((Test-Path -LiteralPath (Join-Path $cache 'appsettings.json')) -or
        [IO.File]::ReadAllText((Join-Path $cache 'appsettings.json.bak')) -ne 'keep local backup') { throw 'A surviving configuration backup was replaced by shipped settings.' }
    Remove-Item -LiteralPath (Join-Path $cache 'appsettings.json.bak')
    $secondId=Run-Launcher
    if ([IO.File]::ReadAllText((Join-Path $cache 'appsettings.json')) -ne 'new source settings') { throw 'A fresh cache did not receive its initial configuration.' }
    $mediaTarget=[IO.Path]::GetFullPath((Join-Path $cache 'media'))
    if ([IO.Path]::GetDirectoryName($mediaTarget) -ine $cache -or [IO.Path]::GetFileName($mediaTarget) -ine 'media') { throw 'Media fixture cleanup escaped the cache.' }
    Remove-Item -LiteralPath $mediaTarget -Recurse -Force
    $secondId=Run-Launcher
    if (Test-Path -LiteralPath $mediaTarget) { throw 'Media was copied into a cache without local media.' }
    function Reject-UnsafeSource($sourceDirectory) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $source 'Restart-Experience.ps1')+'" -SourceDirectory "'+$sourceDirectory+'"'
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.EnvironmentVariables['TEMP']=$tempRoot; $start.EnvironmentVariables['TMP']=$tempRoot
        $command=[Diagnostics.Process]::Start($start)
        if (!$command.WaitForExit(10000)) { $command.Kill(); throw 'Unsafe-path check timed out.' }
        $output=$command.StandardOutput.ReadToEnd()+$command.StandardError.ReadToEnd()
        if ($command.ExitCode -eq 0 -or !(Get-Process -Id $secondId -ErrorAction SilentlyContinue)) { throw 'Unsafe path was not rejected before stopping the app.' }
        return $output
    }
    if ((Reject-UnsafeSource $cache) -notmatch 'overlap') { throw 'Overlapping cache/source was not detected.' }
    $protected=Join-Path $fixture 'protected outside cache'
    [void][IO.Directory]::CreateDirectory($protected)
    [IO.File]::WriteAllText((Join-Path $protected 'sentinel.txt'),'retain')
    $junction=Join-Path $cache 'redirected'
    [void](New-Item -ItemType Junction -Path $junction -Target $protected)
    try {
        if ((Reject-UnsafeSource $source) -notmatch 'junction/symlink') { throw 'Redirected cleanup target was not rejected.' }
        if ([IO.File]::ReadAllText((Join-Path $protected 'sentinel.txt')) -ne 'retain') { throw 'Junction target was modified.' }
    }
    finally { [IO.Directory]::Delete($junction) }
    'Passed CMD execution, process restart, temp launch, unlocked source EXE, latest-file refresh, configuration/log/media retention and exclusion of source media even on initial setup.'
    'Passed overlap and junction rejection before process termination or cleanup.'
}
finally {
    foreach ($process in @(Get-Process ExperienceX -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force
            [void]$process.WaitForExit(5000)
        }
    }
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith(([IO.Path]::GetFullPath($PSScriptRoot)+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped the workspace.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
