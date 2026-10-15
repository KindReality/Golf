$ErrorActionPreference = 'Stop'
if (Get-Process ExperienceX -ErrorAction SilentlyContinue) { throw 'Close Experience before running this isolated check.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixture = Join-Path $env:TEMP ('Experience-supervision-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source'
$temporary = Join-Path $fixture 'temp'
$runtime = Join-Path $temporary 'Experience'
[void][IO.Directory]::CreateDirectory($source)
[void][IO.Directory]::CreateDirectory((Join-Path $runtime 'Media'))
try {
    $code = Join-Path $fixture 'Fixture.cs'
    [IO.File]::WriteAllText($code, 'class Fixture { static void Main() { System.Threading.Thread.Sleep(-1); } }')
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $source 'ExperienceX.exe')) $code
    if ($LASTEXITCODE) { throw 'Fixture compilation failed.' }
    foreach ($name in @('ExperienceX.dll','ExperienceX.deps.json','appsettings.defaults.json')) { [IO.File]::WriteAllText((Join-Path $source $name),'{}') }
    [IO.File]::WriteAllText((Join-Path $source 'ExperienceX.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net9.0"}}')
    Copy-Item -LiteralPath (Join-Path $workspace 'experiencex\Restart-Experience.ps1') -Destination $source
    Copy-Item -LiteralPath (Join-Path $workspace 'Shared\Updates\Stage-ApplicationUpdate.ps1') -Destination $source
    $manifest = @(Get-ChildItem -LiteralPath $source -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash+'|'+$_.Name }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $source 'build-manifest.sha256'),$manifest)
    [IO.File]::WriteAllText((Join-Path $runtime 'Media\retain.txt'),'retained')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.TEST.json'),'retained configuration')
    function Run-Start {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $source 'Restart-Experience.ps1')+'" -SourceDirectory "'+$source+'" -AutomaticUpdate -StartIfNotRunning'
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.EnvironmentVariables['TEMP'] = $temporary; $start.EnvironmentVariables['TMP'] = $temporary
        $process = [Diagnostics.Process]::Start($start)
        if (!$process.WaitForExit(20000)) { $process.Kill(); throw 'Startup timed out.' }
        if ($process.ExitCode) { throw ('Startup failed: '+$process.ExitCode) }
        return @(Get-Process ExperienceX -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq (Join-Path $runtime 'ExperienceX.exe') })[0]
    }
    $first = Run-Start
    if (!$first) { throw 'Missing local process after startup.' }
    $second = Run-Start
    if ($second.Id -ne $first.Id) { throw 'Watchdog replaced an existing instance.' }
    $first.Kill(); $first.WaitForExit()
    $third = Run-Start
    if (!$third -or $third.Id -eq $first.Id) { throw 'Experience was not relaunched after exiting.' }
    if ([IO.File]::ReadAllText((Join-Path $runtime 'Media\retain.txt')) -ne 'retained' -or [IO.File]::ReadAllText((Join-Path $source 'appsettings.TEST.json')) -ne 'retained configuration') { throw 'Media or configuration changed.' }
    'PASS verified startup to local TEMP, existing-process preservation, relaunch after exit, media and configuration retention.'
}
finally {
    foreach ($process in @(Get-Process ExperienceX -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase) })) { $process.Kill(); $process.WaitForExit() }
    # Only remove the exact, uniquely named fixture below the resolved TEMP directory.
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -ine [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -or [IO.Path]::GetFileName($fixture) -notmatch '^Experience-supervision-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
