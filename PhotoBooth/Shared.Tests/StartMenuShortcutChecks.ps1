$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $PSScriptRoot ('shortcut-fixture-'+[Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture "source & user's output"
$programs = Join-Path $fixture 'Start Menu\Programs'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
[void][IO.Directory]::CreateDirectory($source)
try {
    $helper = Join-Path $source 'StartMenuShortcut.ps1'
    # Isolate the known-folder lookup; never replace a real user's Start Menu entries.
    $helperText = [IO.File]::ReadAllText((Join-Path $workspace 'Shared\Windows\StartMenuShortcut.ps1'))
    $helperText = $helperText.Replace("[Environment]::GetFolderPath('Programs')", "'"+$programs.Replace("'","''")+"'")
    [IO.File]::WriteAllText($helper, $helperText)
    . $helper
    $shell = New-Object -ComObject WScript.Shell
    foreach ($app in @(
        @{Project='Controller';Name='Controller';Exe='Controller.exe';Restart='Restart-Controller.cmd';Installer='Install-Controller.ps1'},
        @{Project='experiencex';Name='Experience';Exe='ExperienceX.exe';Restart='Restart-Experience.cmd';Installer='Install-Experience.ps1'}
    )) {
        $command = Join-Path $source $app.Restart
        [IO.File]::WriteAllText($command, "@echo off`r`ncd >`"%~dp0invoked.txt`"`r`nexit /b 0`r`n", [Text.Encoding]::ASCII)
        Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination (Join-Path $source $app.Exe)
        $path = Join-Path $programs ('Saft Technologies\'+$app.Name+'.lnk')
        function Repair-Shortcut {
            New-SaftStartMenuShortcut -SourceDirectory $source -ApplicationName $app.Name -RestartCommand $app.Restart -IconPath (Join-Path $source $app.Exe)
        }
        Repair-Shortcut
        $link = $shell.CreateShortcut($path)
        try {
            if ($link.TargetPath -ine (Join-Path $env:SystemRoot 'System32\cmd.exe') -or
                $link.Arguments -cne ('/d /s /c ""'+$command+'""') -or
                $link.WorkingDirectory -ine $source -or $link.WindowStyle -ne 7 -or
                $link.IconLocation -ine ((Join-Path $source $app.Exe)+',0')) { throw 'Shortcut properties are incorrect.' }
            $process = Start-Process -FilePath $link.TargetPath -ArgumentList $link.Arguments -WorkingDirectory $link.WorkingDirectory -WindowStyle Hidden -Wait -PassThru
            if ($process.ExitCode -ne 0 -or [IO.File]::ReadAllText((Join-Path $source 'invoked.txt')).Trim() -ine $source) { throw 'Shortcut command quoting failed.' }
            $process.Dispose()
            $stamp = [IO.File]::GetLastWriteTimeUtc($path)
            Repair-Shortcut
            if ([IO.File]::GetLastWriteTimeUtc($path) -ne $stamp) { throw 'Healthy shortcut was unnecessarily rewritten.' }
            $link.Arguments = '/c wrong-command'
            $link.Save()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        Repair-Shortcut
        $link = $shell.CreateShortcut($path)
        try { if ($link.Arguments -cne ('/d /s /c ""'+$command+'""')) { throw 'Broken shortcut was not repaired.' } }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        Remove-Item -LiteralPath $path -Force

        # Run the real installer up to a deliberately failed prerequisite check.
        # No machine setup, credentials, process termination or application launch occurs.
        Copy-Item -LiteralPath (Join-Path $workspace ($app.Project+'\'+$app.Installer)) -Destination $source
        $restartScript = [IO.Path]::ChangeExtension($app.Restart, '.ps1')
        Copy-Item -LiteralPath (Join-Path $workspace ($app.Project+'\'+$restartScript)) -Destination $source
        Copy-Item -LiteralPath (Join-Path $workspace 'Shared\Networking\NetworkAccess.ps1') -Destination $source
        $output = & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $source $app.Installer) -Source $source 2>&1
        if ($LASTEXITCODE -eq 0 -or !(Test-Path -LiteralPath $path -PathType Leaf) -or ($output -join "`n") -notmatch 'startup checks failed') { throw ('Installer failed to create its shortcut before startup failure: '+($output -join "`n")) }
        Write-Output ('PASS: '+$app.Name+' shortcut properties, invocation, idempotence, repair and creation before installer startup failure.')
    }
} finally {
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped the test folder.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
