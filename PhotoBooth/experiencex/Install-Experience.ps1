param([string]$Source = $PSScriptRoot, [switch]$Plan, [switch]$ConfigureMachine, [string]$ResultFile, [string[]]$ControllerHostnames = @())
$ErrorActionPreference = 'Stop'
try {
    if ($ConfigureMachine) {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Firewall setup requires administrator permissions.' }
        $name = 'HomeTechnologies.ExperienceX.UDP'
        if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule -Name $name -Enabled True -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet
            Get-NetFirewallRule -Name $name | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol UDP -LocalPort 54242,54243
        } else {
            New-NetFirewallRule -Name $name -DisplayName 'Home Technologies Experience UDP events and commands' -Enabled True -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet -Protocol UDP -LocalPort 54242,54243 | Out-Null
        }
        Set-Content -LiteralPath $ResultFile -Value 'OK'
        exit 0
    }
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $restart = Join-Path $sourcePath 'Restart-Experience.ps1'
    if ($Plan) {
        [pscustomobject]@{Source=$sourcePath;Restart=$restart;MediaUser='SaftThings';CredentialStorage='Windows Credential Manager (current user)';FirewallProfile='Private';FirewallScope='LocalSubnet';UdpPorts=@(54242,54243)} | ConvertTo-Json
        exit 0
    }
    if (!(Test-Path -LiteralPath $restart -PathType Leaf)) { throw 'Missing Restart-Experience.ps1. Wait for the complete build to sync.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw 'Experience startup checks failed; installation canceled.' }
    if ($ControllerHostnames.Count -eq 0) {
        $ControllerHostnames = @((Read-Host 'Controller computer name(s), separated by commas'))
    }
    $controllerNames = @($ControllerHostnames | ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    if ($controllerNames.Count -eq 0) { throw 'Enter at least one Controller computer name.' }
    foreach ($controllerName in $controllerNames) {
        if ($controllerName.Length -gt 253 -or $controllerName -notmatch '^[A-Za-z0-9_]+(?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$') {
            throw 'Use Controller computer names only, such as ALPHA; do not enter a UNC path or share name.'
        }
    }
    $result = Join-Path ([IO.Path]::GetTempPath()) ('Experience-install-'+[Guid]::NewGuid().ToString('N')+'.txt')
    $setup = Join-Path $sourcePath 'Install-Experience.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$setup+'" -ConfigureMachine -ResultFile "'+$result+'"'
    try {
        Write-Host 'Setting up Experience network access. Accept the administrator prompt.'
        $admin = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($admin.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Raw).Trim() -ne 'OK') {
            throw 'Firewall setup was canceled or failed; Experience was not restarted.'
        }
    } finally { if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force } }
    # Save for the current user, outside OneDrive and outside the elevated firewall process.
    # Native CredWrite keeps the password out of child-process command lines.
    if (!('HomeTechnologies.MediaCredentialStore' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HomeTechnologies {
    public static class MediaCredentialStore {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential {
            public uint Flags, Type;
            public string TargetName, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        public static void Save(string hostname, string username, string password) {
            IntPtr secret = Marshal.StringToCoTaskMemUni(password);
            try {
                var credential = new Credential {
                    Type = 2, TargetName = hostname, UserName = username,
                    CredentialBlobSize = checked((uint)(password.Length * 2)),
                    CredentialBlob = secret, Persist = 2
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
        }
    }
}
'@
    }
    foreach ($controllerName in $controllerNames) {
        [HomeTechnologies.MediaCredentialStore]::Save($controllerName, ($controllerName+'\SaftThings'), '10375Saft')
        Write-Host ('Saved media credentials for \\'+$controllerName+'\media as '+$controllerName+'\SaftThings')
    }
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restart -SourceDirectory $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Experience restart failed. Review the error above.' }
    Write-Host 'Experience installation complete. Use Restart-Experience.cmd for future updates.'
    exit 0
} catch {
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Value $_.Exception.Message }
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
