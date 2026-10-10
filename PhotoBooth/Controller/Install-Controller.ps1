param(
    [string]$Source = $PSScriptRoot,
    [switch]$Plan,
    [switch]$ConfigureMachine,
    [string]$MediaPath,
    [string]$ResultFile
)
$ErrorActionPreference = 'Stop'
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
        $permissions = Get-Acl -LiteralPath $resolvedMedia
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($everyone, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $permissions.SetAccessRule($rule)
        Set-Acl -LiteralPath $resolvedMedia -AclObject $permissions
        if (!$share) { New-SmbShare -Name media -Path $resolvedMedia -ReadAccess $everyone | Out-Null }
        else { Grant-SmbShareAccess -Name media -AccountName $everyone -AccessRight Read -Force | Out-Null }
        foreach ($firewall in @(
            @{Name='HomeTechnologies.Controller.UDP';Display='Home Technologies UDP events and commands';Protocol='UDP';Ports=@('54242','54243')},
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
    $controllerRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Home Technologies\Controller'
    $applicationPath = Join-Path $controllerRoot 'Application'
    $localMedia = Join-Path $controllerRoot 'Media'
    $executable = Join-Path $applicationPath 'Controller.exe'
    if ($Plan) {
        [pscustomobject]@{Source=$sourcePath;Application=$applicationPath;Media=$localMedia;Share=('\\'+$env:COMPUTERNAME+'\media');ShareAccess='Read';FirewallProfile='Private';FirewallScope='LocalSubnet';Preserve=@('Configuration','Media','logs');Launch=$executable} | ConvertTo-Json
        exit 0
    }
    if (!(Test-Path -LiteralPath (Join-Path $sourcePath 'Controller.exe') -PathType Leaf)) { throw 'Run Install-Controller.cmd from a completed Controller build folder.' }
    $runtime = Get-Content -LiteralPath (Join-Path $sourcePath 'Controller.runtimeconfig.json') -Raw | ConvertFrom-Json
    $desktopFramework = @($runtime.runtimeOptions.frameworks) + @($runtime.runtimeOptions.framework) | Where-Object { $_ -and $_.name -eq 'Microsoft.WindowsDesktop.App' } | Select-Object -First 1
    if ($desktopFramework) {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (!(Test-Path -LiteralPath $dotnet)) { throw 'Install the x64 .NET 9 Windows Desktop Runtime before running this installer.' }
        $requiredMajor = ([version]$desktopFramework.version).Major
        if (!(& $dotnet --list-runtimes | Where-Object { $_ -match ('^Microsoft.WindowsDesktop.App '+$requiredMajor+'\.') })) { throw ('Install the x64 .NET '+$requiredMajor+' Windows Desktop Runtime before running this installer.') }
    }
    if ($sourcePath.TrimEnd('\').Equals($applicationPath.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Run the installer from the build output, not the installed Application folder.' }
    [void][IO.Directory]::CreateDirectory($applicationPath)
    [void][IO.Directory]::CreateDirectory($localMedia)
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    Get-CimInstance Win32_Process -Filter "Name='Controller.exe'" | Where-Object SessionId -eq $session | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    & robocopy.exe $sourcePath $applicationPath /E /R:2 /W:1 /XD Media logs Configuration /XF appsettings.json appsettings.json.bak '*.tmp' /NFL /NDL /NJH /NJS
    if ($LASTEXITCODE -ge 8) { throw ('Copying the Controller build failed with robocopy exit code '+$LASTEXITCODE+'.') }
    $result = Join-Path ([IO.Path]::GetTempPath()) ('Controller-install-'+[Guid]::NewGuid().ToString('N')+'.txt')
    $setupScript = Join-Path $applicationPath 'Install-Controller.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$setupScript+'" -ConfigureMachine -MediaPath "'+$localMedia+'" -ResultFile "'+$result+'"'
    try {
        $admin = Start-Process -FilePath powershell.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($admin.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Raw).Trim() -ne 'OK') {
            $detail = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { 'Administrator setup was canceled or could not complete.' }
            throw $detail
        }
    } finally { if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force } }
    Write-Host ('Media folder: '+$localMedia)
    Write-Host ('Read-only share: \\'+$env:COMPUTERNAME+'\media')
    Write-Host 'Use a Private network profile. Clients access the share with normal Windows file-sharing credentials.'
    Start-Process -FilePath $executable -WorkingDirectory $applicationPath -WindowStyle Normal
} catch {
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Value $_.Exception.Message }
    Write-Error $_.Exception.Message
    exit 1
}
