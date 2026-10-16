$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (Get-Process Controller,ExperienceX -ErrorAction SilentlyContinue) { throw 'Stop real applications before this isolated check.' }
$fixture = Join-Path $PSScriptRoot ('joint-restart-'+[Guid]::NewGuid().ToString('N'))
$packages = Join-Path $fixture "source & user's output"
$temporary = Join-Path $fixture 'local temp'
$programs = Join-Path $fixture 'Start Menu\Programs'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $code = Join-Path $fixture 'Fixture.cs'
    [IO.File]::WriteAllText($code, 'class Fixture { static void Main() { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,"started.txt"),System.Diagnostics.Process.GetCurrentProcess().Id+"|"+System.DateTime.UtcNow.Ticks); System.Threading.Thread.Sleep(-1); } }')
    $fixtureExe = Join-Path $fixture 'Fixture.exe'
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+$fixtureExe) $code
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    $caches = @{}
    foreach ($app in @('Controller','ExperienceX')) {
        $source = Join-Path $packages $app
        [void][IO.Directory]::CreateDirectory($source)
        $project = if ($app -eq 'Controller') { 'Controller' } else { 'experiencex' }
        $script = if ($app -eq 'Controller') { 'Restart-Controller' } else { 'Restart-Experience' }
        Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $source ($app+'.exe'))
        Copy-Item -LiteralPath (Join-Path $workspace ($project+'\'+$script+'.ps1')) -Destination $source
        Copy-Item -LiteralPath (Join-Path $workspace ($project+'\'+$script+'.cmd')) -Destination $source
        Copy-Item -LiteralPath (Join-Path $workspace 'Shared\Updates\Stage-ApplicationUpdate.ps1') -Destination $source
        $helper = [IO.File]::ReadAllText((Join-Path $workspace 'Shared\Windows\StartMenuShortcut.ps1'))
        $helper = $helper.Replace("[Environment]::GetFolderPath('Programs')", "'"+$programs.Replace("'","''")+"'")
        [IO.File]::WriteAllText((Join-Path $source 'StartMenuShortcut.ps1'), $helper)
        foreach ($name in @(($app+'.dll'),($app+'.deps.json'),'appsettings.defaults.json')) { [IO.File]::WriteAllText((Join-Path $source $name),'version-one') }
        if ($app -eq 'Controller') { [IO.File]::WriteAllText((Join-Path $source 'appsettings.environment.json'),'{}') }
        [IO.File]::WriteAllText((Join-Path $source ($app+'.runtimeconfig.json')), '{"runtimeOptions":{"tfm":"net9.0"}}')
        $cache = if ($app -eq 'Controller') { Join-Path $temporary 'Home Technologies\Controller\Application' } else { Join-Path $temporary 'Experience' }
        $caches[$app] = $cache
        [void][IO.Directory]::CreateDirectory((Join-Path $cache 'Media'))
        [IO.File]::WriteAllText((Join-Path $cache 'Media\keep.mp4'),'keep local media')
        [IO.File]::WriteAllText((Join-Path $source 'appsettings.TEST.json'),'keep hostname configuration')
    }
    function Write-Manifest([string]$App) {
        $source = Join-Path $packages $App
        $lines = Get-ChildItem -LiteralPath $source -File | Where-Object Name -notin @('appsettings.TEST.json','build-manifest.sha256') | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash+'|'+$_.Name }
        [IO.File]::WriteAllText((Join-Path $source 'build-manifest.sha256'), ($lines -join "`n"))
    }
    function Run-Restart([string]$App, [switch]$Both) {
        $source = Join-Path $packages $App
        $script = if ($App -eq 'Controller') { 'Restart-Controller.ps1' } else { 'Restart-Experience.ps1' }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $powershell
        $start.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $source $script)+'" -SourceDirectory "'+$source+'" -AutomaticUpdate'
        if ($Both) { $start.Arguments += ' -RestartExperience' }
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.EnvironmentVariables['TEMP']=$temporary; $start.EnvironmentVariables['TMP']=$temporary
        $start.EnvironmentVariables['LOCALAPPDATA']=$temporary
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (!$process.WaitForExit(45000)) { $process.Kill(); throw 'Joint restart timed out.' }
            return [pscustomobject]@{Code=$process.ExitCode;Output=($process.StandardOutput.ReadToEnd()+$process.StandardError.ReadToEnd())}
        } finally { $process.Dispose() }
    }
    function Running([string]$App) { return [int]([IO.File]::ReadAllText((Join-Path $caches[$App] 'started.txt')).Split('|')[0]) }
    foreach ($app in @('Controller','ExperienceX')) {
        Write-Manifest $app
        $result = Run-Restart $app
        if ($result.Code -ne 0) { throw $result.Output }
    }
    $oldController = Running 'Controller'; $oldExperience = Running 'ExperienceX'
    [IO.File]::WriteAllText((Join-Path $packages 'ExperienceX\ExperienceX.dll'),'incomplete sync')
    $rejected = Run-Restart 'Controller' -Both
    if ($rejected.Code -eq 0 -or !(Get-Process -Id $oldController -ErrorAction SilentlyContinue) -or !(Get-Process -Id $oldExperience -ErrorAction SilentlyContinue)) { throw 'Incomplete Experience package interrupted a running application.' }
    foreach ($app in @('Controller','ExperienceX')) {
        [IO.File]::WriteAllText((Join-Path (Join-Path $packages $app) ($app+'.dll')),'version-two')
        Write-Manifest $app
    }
    $result = Run-Restart 'Controller' -Both
    if ($result.Code -ne 0) { throw $result.Output }
    if ((Running 'Controller') -eq $oldController -or (Running 'ExperienceX') -eq $oldExperience -or
        (Get-Process -Id $oldController -ErrorAction SilentlyContinue) -or (Get-Process -Id $oldExperience -ErrorAction SilentlyContinue)) { throw 'Both applications were not replaced.' }
    $controllerTicks = [long]([IO.File]::ReadAllText((Join-Path $caches['Controller'] 'started.txt')).Split('|')[1])
    $experienceTicks = [long]([IO.File]::ReadAllText((Join-Path $caches['ExperienceX'] 'started.txt')).Split('|')[1])
    if ($experienceTicks -ge $controllerTicks) { throw 'Controller started before Experience finished restarting.' }
    foreach ($app in @('Controller','ExperienceX')) {
        if ([IO.File]::ReadAllText((Join-Path $caches[$app] ($app+'.dll'))) -ne 'version-two' -or
            [IO.File]::ReadAllText((Join-Path $caches[$app] 'Media\keep.mp4')) -ne 'keep local media' -or
            [IO.File]::ReadAllText((Join-Path (Join-Path $packages $app) 'appsettings.TEST.json')) -ne 'keep hostname configuration') { throw 'Joint restart did not refresh binaries while preserving configuration and media.' }
    }
    Write-Output 'PASS joint restart: preflight protects both running apps, both PIDs change, Experience starts before Controller, latest binaries installed, configuration/media preserved.'
    # A failed Experience launch must not prevent Controller from coming back.
    [IO.File]::WriteAllText($code, 'class Fixture { static void Main() { System.Environment.Exit(42); } }')
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $packages 'ExperienceX\ExperienceX.exe')) $code
    if ($LASTEXITCODE -ne 0) { throw 'Failure fixture compilation failed.' }
    Write-Manifest 'ExperienceX'
    $oldController = Running 'Controller'
    $result = Run-Restart 'Controller' -Both
    $newController = Running 'Controller'
    if ($result.Code -eq 0 -or $result.Output -notmatch 'Controller is running again' -or $newController -eq $oldController -or !(Get-Process -Id $newController -ErrorAction SilentlyContinue)) { throw ('Controller did not recover after Experience launch failed: '+$result.Output) }
    Write-Output 'PASS joint restart failure: Experience startup error reported and Controller still relaunched.'
} finally {
    foreach ($app in @('Controller','ExperienceX')) {
        foreach ($process in @(Get-Process -Name $app -ErrorAction SilentlyContinue)) {
            if ($process.Path -and $process.Path.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) { $process.Kill(); [void]$process.WaitForExit(5000) }
        }
    }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped its workspace folder.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
