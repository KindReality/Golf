param(
    [string]$Source = $PSScriptRoot,
    [switch]$Plan,
    [switch]$ConfigureMachine,
    [string]$MediaPath,
    [string]$ResultFile
)
$ErrorActionPreference = 'Stop'
function New-SaftStartMenuShortcut([string]$SourceDirectory, [string]$ApplicationName, [string]$RestartCommand, [string]$IconPath) {
    $programs = [Environment]::GetFolderPath('Programs')
    if ([string]::IsNullOrWhiteSpace($programs)) { throw 'The current user Start Menu Programs folder is unavailable.' }
    $folder = Join-Path $programs 'Saft Technologies'
    [void][IO.Directory]::CreateDirectory($folder)
    $restartCommandPath = Join-Path $SourceDirectory $RestartCommand
    if (!(Test-Path -LiteralPath $restartCommandPath -PathType Leaf)) { throw ('Missing restart command: '+$restartCommandPath) }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $folder ($ApplicationName+'.lnk')))
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $shortcut.Arguments = '/c ""'+$restartCommandPath+'""'
    $shortcut.WorkingDirectory = $SourceDirectory
    $shortcut.Description = 'Refresh and start '+$ApplicationName+' from the current OneDrive build.'
    $shortcut.WindowStyle = 7
    if (Test-Path -LiteralPath $IconPath -PathType Leaf) { $shortcut.IconLocation = $IconPath+',0' }
    $shortcut.Save()
    Write-Host ('Start Menu shortcut: '+(Join-Path $folder ($ApplicationName+'.lnk')))
}

function Initialize-MediaAccount([string]$ResolvedMedia) {
    $mediaUserName = 'SaftThings'
    $mediaPassword = ConvertTo-SecureString '10375Saft' -AsPlainText -Force
    $mediaUser = Get-LocalUser -Name $mediaUserName -ErrorAction SilentlyContinue
    $administrators = Get-LocalGroup -SID 'S-1-5-32-544'
    if ($mediaUser -and (@(Get-LocalGroupMember -Group $administrators | ForEach-Object { $_.SID.Value }) -contains $mediaUser.SID.Value)) {
        throw 'SaftThings is an administrator. Installation canceled; use a dedicated non-administrator media account.'
    }
    if ($mediaUser) {
        Set-LocalUser -Name $mediaUserName -Password $mediaPassword -PasswordNeverExpires $true -AccountNeverExpires -UserMayChangePassword $false -Description 'Home Technologies media transfer'
        Enable-LocalUser -Name $mediaUserName
    } else {
        $mediaUser = New-LocalUser -Name $mediaUserName -Password $mediaPassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -Description 'Home Technologies media transfer'
    }
    $users = Get-LocalGroup -SID 'S-1-5-32-545'
    if (@(Get-LocalGroupMember -Group $users | ForEach-Object { $_.SID.Value }) -notcontains $mediaUser.SID.Value) {
        Add-LocalGroupMember -Group $users -Member $mediaUser
    }
    # Add only this account's media access; retain all other NTFS rules.
    $permissions = Get-Acl -LiteralPath $resolvedMedia
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($mediaUser.SID, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $permissions.SetAccessRule($rule)
    Set-Acl -LiteralPath $resolvedMedia -AclObject $permissions
    return $mediaUserName
}

try {
    if ($ConfigureMachine) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Share and firewall setup requires administrator permissions.' }
        $resolvedMedia = [IO.Path]::GetFullPath($MediaPath)
        if ($resolvedMedia.StartsWith('\\') -or !(Test-Path -LiteralPath $resolvedMedia -PathType Container)) { throw 'The media share must point to an existing local directory.' }
        $everyone = (New-Object Security.Principal.SecurityIdentifier('S-1-1-0')).Translate([Security.Principal.NTAccount]).Value
        Set-Service -Name LanmanServer -StartupType Automatic
        Start-Service -Name LanmanServer
        $share = Get-SmbShare -Name media -ErrorAction SilentlyContinue
        if ($share -and ![IO.Path]::GetFullPath($share.Path).Equals($resolvedMedia, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('A media share already points to ' + $share.Path + '. It was retained; resolve this before installing Controller.')
        }
        $mediaUserName = Initialize-MediaAccount -ResolvedMedia $resolvedMedia
        # Share access permits writes; existing NTFS permissions remain the final limit.
        if (!$share) { New-SmbShare -Name media -Path $resolvedMedia -ChangeAccess $everyone | Out-Null }
        else { Grant-SmbShareAccess -Name media -AccountName $everyone -AccessRight Change -Force | Out-Null }
        Grant-SmbShareAccess -Name media -AccountName ($env:COMPUTERNAME+'\'+$mediaUserName) -AccessRight Change -Force | Out-Null
        Write-Host ('Media account ready: '+$env:COMPUTERNAME+'\'+$mediaUserName)
        foreach ($firewall in @(
            @{Name='HomeTechnologies.Controller.UDP';Display='Home Technologies UDP events, commands and WLED discovery';Protocol='UDP';Ports=@('54242','54243','5353')},
            @{Name='HomeTechnologies.Controller.Definitions';Display='Home Technologies shared definitions';Protocol='TCP';Ports=@('54244')},
            @{Name='HomeTechnologies.Controller.Media';Display='Home Technologies media share';Protocol='TCP';Ports=@('445')}
        )) {
            if (Get-NetFirewallRule -Name $firewall.Name -ErrorAction SilentlyContinue) {
                Set-NetFirewallRule -Name $firewall.Name -Enabled True -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet
                Get-NetFirewallRule -Name $firewall.Name | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol $firewall.Protocol -LocalPort $firewall.Ports
            } else {
                New-NetFirewallRule -Name $firewall.Name -DisplayName $firewall.Display -Enabled True -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet -Protocol $firewall.Protocol -LocalPort $firewall.Ports | Out-Null
            }
        }
        Set-Content -LiteralPath $ResultFile -Value 'OK'
        exit 0
    }
    $sourcePath = [IO.Path]::GetFullPath($Source)
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable.' }
    $controllerRoot = Join-Path $env:LOCALAPPDATA 'Home Technologies\Controller'
    $applicationPath = Join-Path $controllerRoot 'Application'
    $localMedia = Join-Path $controllerRoot 'Media'
    $restart = Join-Path $sourcePath 'Restart-Controller.ps1'
    if ($Plan) {
        [pscustomobject]@{Source=$sourcePath;Application=$applicationPath;Media=$localMedia;Share=('\\'+$env:COMPUTERNAME+'\media');MediaUser='SaftThings';ShareAccess='Change';FirewallProfile='Private';FirewallScope='LocalSubnet';Restart=$restart} | ConvertTo-Json
        exit 0
    }
    if (!(Test-Path -LiteralPath $restart -PathType Leaf)) { throw 'Missing Restart-Controller.ps1. Wait for the complete build to sync.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw 'Controller startup checks failed; installation canceled.' }
    [void][IO.Directory]::CreateDirectory($localMedia)
    $result = Join-Path ([IO.Path]::GetTempPath()) ('Controller-install-'+[Guid]::NewGuid().ToString('N')+'.txt')
    $setup = Join-Path $sourcePath 'Install-Controller.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$setup+'" -ConfigureMachine -MediaPath "'+$localMedia+'" -ResultFile "'+$result+'"'
    try {
        Write-Host 'Setting up Controller media sharing and network access. Accept the administrator prompt.'
        $admin = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($admin.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Raw).Trim() -ne 'OK') {
            $detail = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { 'Administrator setup was canceled or could not complete.' }
            throw $detail
        }
    } finally { if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force } }
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Controller restart failed. Review the error above.' }
    Write-Host ('Media share: \\'+$env:COMPUTERNAME+'\media (read/write, subject to NTFS permissions).')
    New-SaftStartMenuShortcut -SourceDirectory $sourcePath -ApplicationName 'Controller' -RestartCommand 'Restart-Controller.cmd' -IconPath (Join-Path $sourcePath 'Controller.exe')
    Write-Host 'Controller installation complete. Use Restart-Controller.cmd for future updates.'
    exit 0
} catch {
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Value $_.Exception.Message }
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
