$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot)
if (Get-Process Controller -ErrorAction SilentlyContinue) { throw 'Stop the real Controller application before this fixture.' }
$fixture = Join-Path $PSScriptRoot ('restart-fixture-'+[Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture "source & user's output"
$tempRoot = Join-Path $fixture 'test temp'
$cache = Join-Path $tempRoot 'Home Technologies\Controller\Application'
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
    $executable=Join-Path $source 'Controller.exe'
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 ('/out:'+$executable) $program
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compile the isolated launcher fixture.' }
    foreach ($name in @('Controller.dll','Controller.deps.json','Controller.runtimeconfig.json','appsettings.defaults.json','appsettings.environment.json')) {
        [IO.File]::WriteAllText((Join-Path $source $name),'version-one')
    }
    [IO.File]::WriteAllText((Join-Path $source 'Controller.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net9.0"}}')
    foreach ($name in @('Restart-Controller.cmd','Restart-Controller.ps1','Install-Controller.cmd','Install-Controller.ps1')) {
        Copy-Item -LiteralPath (Join-Path $workspace ('Controller\'+$name)) -Destination $source
    }
    Copy-Item -LiteralPath (Join-Path $workspace 'Shared\Networking\NetworkAccess.ps1') -Destination $source
    $programs = Join-Path $fixture 'Start Menu\Programs'
    $helper = [IO.File]::ReadAllText((Join-Path $workspace 'Shared\Windows\StartMenuShortcut.ps1'))
    $helper = $helper.Replace("[Environment]::GetFolderPath('Programs')", "'"+$programs.Replace("'","''")+"'")
    [IO.File]::WriteAllText((Join-Path $source 'StartMenuShortcut.ps1'), $helper)
    # Verify hashing without PowerShell's Get-FileHash, including literal paths and file closure.
    $launcherPath = Join-Path $source 'Restart-Controller.ps1'
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($launcherPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw 'Restart script does not parse.' }
    $hashHelper = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-FileSha256' }, $true)
    if (!$hashHelper) { throw 'Module-independent hash helper is missing.' }
    Invoke-Expression $hashHelper.Extent.Text
    $hashSample = Join-Path $fixture "hash & user's [sample].txt"
    [IO.File]::WriteAllText($hashSample, 'abc', [Text.Encoding]::ASCII)
    if ((Get-FileSha256 -LiteralPath $hashSample) -ne 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD') { throw 'SHA256 checksum is incorrect.' }
    $unlockedSample = [IO.File]::Open($hashSample, 'Open', 'ReadWrite', 'None'); $unlockedSample.Dispose()
    [IO.File]::WriteAllText($hashSample, 'abd', [Text.Encoding]::ASCII)
    if ((Get-FileSha256 -LiteralPath $hashSample) -eq 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD') { throw 'Changed file retained the same checksum.' }
    $missingRejected = $false
    try { Get-FileSha256 -LiteralPath (Join-Path $fixture 'missing-file') | Out-Null } catch { $missingRejected = $true }
    if (!$missingRejected) { throw 'Missing hash input was accepted.' }
    # All CMD and installer lifecycle runs must succeed when Get-FileHash is unavailable.
    $disabledCmdlet = "function Get-FileHash { throw 'Get-FileHash is unavailable on this test PC.' }`r`n"
    $launcherText = [IO.File]::ReadAllText($launcherPath)
    $launcherText = $launcherText.Replace("`$ErrorActionPreference = 'Stop'", $disabledCmdlet+"`$ErrorActionPreference = 'Stop'")
    [IO.File]::WriteAllText($launcherPath, $launcherText)
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json'),'source settings')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json.bak'),'source backup')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.ALPHA.json'),'hostname settings')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.ALPHA.json.bak'),'hostname backup')
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
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'logs'))
    [IO.File]::WriteAllText((Join-Path $source 'logs\history.txt'),'source log must not overwrite history')
    [IO.File]::WriteAllText((Join-Path $source 'logs\source-only.txt'),'do not copy')
    $original = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 250
    function Run-Launcher {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $env:ComSpec
        $start.Arguments = '/d /s /c ""'+(Join-Path $source 'Restart-Controller.cmd')+'""'
        $start.WorkingDirectory = $fixture
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.RedirectStandardInput = $true
        $start.EnvironmentVariables['LOCALAPPDATA']=$tempRoot
        $start.EnvironmentVariables['TEMP']=$tempRoot
        $start.EnvironmentVariables['TMP']=$tempRoot
        $command = [Diagnostics.Process]::Start($start)
        $command.StandardInput.Close()
        if (!$command.WaitForExit(15000)) { $command.Kill(); throw 'Launcher timed out.' }
        $output=$command.StandardOutput.ReadToEnd()+$command.StandardError.ReadToEnd()
        if ($command.ExitCode -ne 0) { throw $output }
        $report=([IO.File]::ReadAllText((Join-Path $cache 'started.txt'))).Split('|')
        if ($report[1].TrimEnd('\') -ine $cache) { throw 'Application did not launch from the local application folder.' }
        return [int]$report[0]
    }
    $firstId=Run-Launcher
    if ((Test-Path -LiteralPath (Join-Path $cache 'appsettings.ALPHA.json')) -or
        (Test-Path -LiteralPath (Join-Path $cache 'appsettings.ALPHA.json.bak')) -or
        [IO.File]::ReadAllText((Join-Path $source 'appsettings.ALPHA.json')) -ne 'hostname settings') {
        throw 'Launcher copied or changed hostname configuration.'
    }
    if (!$original.HasExited -or (Test-Path -LiteralPath (Join-Path $cache 'obsolete.dll'))) { throw 'Old instance or obsolete binary survived refresh.' }
    # Exercise installer orchestration without touching real firewall/share permissions.
    $driver = Join-Path $fixture 'Install-Driver.ps1'
    @'
param([string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
function global:Start-Process {
    param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    if ($Verb -ne 'RunAs' -or $ArgumentList -notmatch '-ConfigureMachine') { throw 'Unexpected installer elevation.' }
    $result = [regex]::Match($ArgumentList, '-ResultFile "([^"]+)"').Groups[1].Value
    if (!$result) { throw 'Installer did not provide a setup result file.' }
    [IO.File]::WriteAllText($result, 'OK')
    [IO.File]::WriteAllText((Join-Path $SourceDirectory 'admin-setup-requested.txt'), 'one setup request')
    return [pscustomobject]@{ExitCode=0}
}
& (Join-Path $SourceDirectory 'Install-Controller.ps1') -Source $SourceDirectory
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $driver -Encoding ascii
    $installStart = New-Object Diagnostics.ProcessStartInfo
    $installStart.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $installStart.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$driver+'" -SourceDirectory "'+$source+'"'
    $installStart.UseShellExecute=$false; $installStart.CreateNoWindow=$true
    $installStart.RedirectStandardOutput=$true; $installStart.RedirectStandardError=$true
    $installStart.EnvironmentVariables['TEMP']=$tempRoot; $installStart.EnvironmentVariables['TMP']=$tempRoot
    $installStart.EnvironmentVariables['LOCALAPPDATA']=$tempRoot
    $installer = [Diagnostics.Process]::Start($installStart)
    if (!$installer.WaitForExit(20000)) { $installer.Kill(); throw 'Installer fixture timed out.' }
    $installOutput = $installer.StandardOutput.ReadToEnd()+$installer.StandardError.ReadToEnd()
    if ($installer.ExitCode -ne 0) { throw $installOutput }
    $installedId = [int]([IO.File]::ReadAllText((Join-Path $cache 'started.txt'))).Split('|')[0]
    if ($installedId -eq $firstId -or (Get-Process -Id $firstId -ErrorAction SilentlyContinue) -or
        !(Test-Path -LiteralPath (Join-Path $source 'admin-setup-requested.txt'))) { throw 'Installer did not call the shared restart script after setup.' }
    $firstId = $installedId
    if ((Test-Path -LiteralPath (Join-Path $cache 'logs\source-only.txt')) -or
        [IO.File]::ReadAllText((Join-Path $cache 'logs\history.txt')) -ne 'retain') { throw 'Source logs were copied over local history.' }
    $unlocked=[IO.File]::Open($executable,'Open','ReadWrite','None'); $unlocked.Dispose()
    [IO.File]::WriteAllText((Join-Path $source 'Controller.dll'),'version-two')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.defaults.json'),'version-two defaults')
    [IO.File]::WriteAllText((Join-Path $source 'appsettings.json'),'new source settings')
    [IO.File]::WriteAllText((Join-Path $cache 'obsolete.dll'),'remove again')
    $secondId=Run-Launcher
    if ($firstId -eq $secondId -or (Get-Process -Id $firstId -ErrorAction SilentlyContinue)) { throw 'Restart did not replace the cached process.' }
    if ([IO.File]::ReadAllText((Join-Path $cache 'Controller.dll')) -ne 'version-two' -or
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
    if ((Test-Path -LiteralPath (Join-Path $cache 'appsettings.json')) -or (Test-Path -LiteralPath (Join-Path $cache 'appsettings.json.bak'))) { throw 'A fresh runtime cache received a live configuration file instead of defaults only.' }
    $mediaTarget=[IO.Path]::GetFullPath((Join-Path $cache 'media'))
    if ([IO.Path]::GetDirectoryName($mediaTarget) -ine $cache -or [IO.Path]::GetFileName($mediaTarget) -ine 'media') { throw 'Media fixture cleanup escaped the cache.' }
    Remove-Item -LiteralPath $mediaTarget -Recurse -Force
    $secondId=Run-Launcher
    if (Test-Path -LiteralPath $mediaTarget) { throw 'Media was copied into a cache without local media.' }
    function Reject-UnsafeSource($sourceDirectory) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $source 'Restart-Controller.ps1')+'" -SourceDirectory "'+$sourceDirectory+'"'
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.EnvironmentVariables['LOCALAPPDATA']=$tempRoot
        $start.EnvironmentVariables['TEMP']=$tempRoot; $start.EnvironmentVariables['TMP']=$tempRoot
        $command=[Diagnostics.Process]::Start($start)
        if (!$command.WaitForExit(10000)) { $command.Kill(); throw 'Unsafe-path check timed out.' }
        $output=$command.StandardOutput.ReadToEnd()+$command.StandardError.ReadToEnd()
        if ($command.ExitCode -eq 0 -or !(Get-Process -Id $secondId -ErrorAction SilentlyContinue)) { throw 'Unsafe path was not rejected before stopping the app.' }
        return $output
    }
    if ((Reject-UnsafeSource $cache) -notmatch 'overlap') { throw 'Overlapping cache/source was not detected.' }
    $runtimeFile = Join-Path $source 'Controller.runtimeconfig.json'
    $originalRuntime = [IO.File]::ReadAllText($runtimeFile)
    [IO.File]::WriteAllText($runtimeFile, '{"runtimeOptions":{"framework":{"name":"Microsoft.WindowsDesktop.App","version":"99.0.0"}}}')
    try {
        if ((Reject-UnsafeSource $source) -notmatch 'Windows Desktop Runtime') { throw 'Missing runtime was not detected before stopping the app.' }
    } finally { [IO.File]::WriteAllText($runtimeFile, $originalRuntime) }
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
    'Passed SHA256 known value, changed/missing inputs, literal path and file closure; CMD and installer restarts with Get-FileHash unavailable.'
    'Passed CMD execution, process restart, local application launch, unlocked source EXE, latest-file refresh, configuration/log/media retention and exclusion of source media even on initial setup.'
    'Passed installer-to-restart delegation with mocked administrative setup, log preservation and missing-runtime preflight.'
    'Passed overlap and junction rejection before process termination or cleanup.'
}
finally {
    foreach ($process in @(Get-Process Controller -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force
            [void]$process.WaitForExit(5000)
        }
    }
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith(([IO.Path]::GetFullPath($PSScriptRoot)+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped the workspace.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
