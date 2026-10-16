# Shared by installation and restart; works in inbox Windows PowerShell 5.1.
function New-SaftStartMenuShortcut(
    [string]$SourceDirectory,
    [string]$ApplicationName,
    [string]$RestartCommand,
    [string]$IconPath,
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs')
) {
    if ([string]::IsNullOrWhiteSpace($ProgramsDirectory)) { throw 'The current user Start Menu Programs folder is unavailable.' }
    $source = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\')
    $command = Join-Path $source $RestartCommand
    if (!(Test-Path -LiteralPath $command -PathType Leaf)) { throw ('Missing restart command: '+$command) }
    if (!(Test-Path -LiteralPath $IconPath -PathType Leaf)) { throw ('Missing application icon: '+$IconPath) }
    $folder = Join-Path $ProgramsDirectory 'Saft Technologies'
    [void][IO.Directory]::CreateDirectory($folder)
    $path = Join-Path $folder ($ApplicationName+'.lnk')
    $target = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $arguments = '/d /s /c ""'+$command+'""'
    $icon = $IconPath+',0'
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($path)
        # Do not rewrite a healthy shortcut on every automatic application update.
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            $shortcut.TargetPath -ine $target -or $shortcut.Arguments -cne $arguments -or
            $shortcut.WorkingDirectory -ine $source -or $shortcut.WindowStyle -ne 7 -or
            $shortcut.IconLocation -ine $icon) {
            $shortcut.TargetPath = $target
            $shortcut.Arguments = $arguments
            $shortcut.WorkingDirectory = $source
            $shortcut.Description = 'Refresh and start '+$ApplicationName+' from the current OneDrive build.'
            $shortcut.WindowStyle = 7
            $shortcut.IconLocation = $icon
            $shortcut.Save()
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Start Menu shortcut was not saved: '+$path) }
        }
        Write-Host ('Start Menu shortcut: '+$path)
    } finally {
        if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}
