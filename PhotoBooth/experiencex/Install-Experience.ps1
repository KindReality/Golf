param([string]$Source = $PSScriptRoot, [switch]$Plan, [switch]$ConfigureMachine, [string]$ResultFile, [string]$RuntimePath, [switch]$RepairMachine)
$ErrorActionPreference = 'Stop'


try {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $networkSetup=Join-Path $sourcePath 'NetworkAccess.ps1'
    if (!(Test-Path -LiteralPath $networkSetup)) { $networkSetup=Join-Path $PSScriptRoot '..\Shared\Networking\NetworkAccess.ps1' }
    . $networkSetup
    if ($ConfigureMachine) {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Firewall setup requires administrator permissions.' }
        Set-SaftNetworkAccess 'ExperienceX' $RuntimePath
        Set-Content -LiteralPath $ResultFile -Value 'OK'
        exit 0
    }
    $restart = Join-Path $sourcePath 'Restart-Experience.ps1'
    if ($Plan) {
        [pscustomobject]@{Source=$sourcePath;Restart=$restart;Controller='127.0.0.1:54244';FirewallProfile='Private, Public';FirewallScope='LocalSubnet';UdpPorts=@(54242,54243,5353);TcpPorts=@(54245)} | ConvertTo-Json
        exit 0
    }
    # Create the current user's entry before runtime, network or application startup can fail.
    $shortcutSetup = Join-Path $sourcePath 'StartMenuShortcut.ps1'
    if (!(Test-Path -LiteralPath $shortcutSetup -PathType Leaf)) { $shortcutSetup = Join-Path $PSScriptRoot '..\Shared\Windows\StartMenuShortcut.ps1' }
    . $shortcutSetup
    New-SaftStartMenuShortcut -SourceDirectory $sourcePath -ApplicationName 'Experience' -RestartCommand 'Restart-Experience.cmd' -IconPath (Join-Path $sourcePath 'ExperienceX.exe')
    if (!(Test-Path -LiteralPath $restart -PathType Leaf)) { throw 'Missing Restart-Experience.ps1. Wait for the complete build to sync.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw 'Experience startup checks failed; installation canceled.' }
    $result = Join-Path ([IO.Path]::GetTempPath()) ('Experience-install-'+[Guid]::NewGuid().ToString('N')+'.txt')
    $setup = Join-Path $sourcePath 'Install-Experience.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$setup+'" -ConfigureMachine -ResultFile "'+$result+'" -RuntimePath "'+(Join-Path ([IO.Path]::GetTempPath()) 'Experience\ExperienceX.exe')+'"'
    if ($RepairMachine -or !(Test-SaftNetworkAccess 'ExperienceX' (Join-Path ([IO.Path]::GetTempPath()) 'Experience\ExperienceX.exe'))) {
        try {
            Write-Host 'Setting up Experience network access. Accept the administrator prompt.'
            $admin = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if ($admin.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Raw).Trim() -ne 'OK') {
                throw 'Firewall setup was canceled or failed; Experience was not restarted.'
            }
        } finally { if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force } }
    } else { Write-Host 'Network setup is already current; no administrator prompt is needed.' }
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Experience restart failed. Review the error above.' }
    Write-Host 'Experience installation complete. Use Restart-Experience.cmd for future updates.'
    exit 0
} catch {
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Value $_.Exception.Message }
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
