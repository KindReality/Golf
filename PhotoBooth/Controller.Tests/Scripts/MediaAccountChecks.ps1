$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot)
$tokens = $null; $parseErrors = $null
$controller = [Management.Automation.Language.Parser]::ParseFile((Join-Path $workspace 'Controller\Install-Controller.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Controller installer does not parse.' }
$helper = $controller.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-MediaAccount' }, $true)
Invoke-Expression $helper.Extent.Text

# Replace all machine mutations with in-memory fixtures.
$testUser = [pscustomobject]@{SID=(New-Object Security.Principal.SecurityIdentifier('S-1-5-21-101-102-103-1001'))}
$otherSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-21-101-102-103-1002')
function Get-LocalUser { param($Name,$ErrorAction) if ($script:existing) { return $testUser } }
function Get-LocalGroup { param($SID) [pscustomobject]@{SID=$SID} }
function Get-LocalGroupMember {
    param($Group)
    if (($Group.SID -eq 'S-1-5-32-544' -and $script:administrator) -or
        ($Group.SID -eq 'S-1-5-32-545' -and $script:alreadyMember)) { return $testUser }
}
function New-LocalUser {
    param($Name,$Password,[switch]$AccountNeverExpires,[switch]$PasswordNeverExpires,[switch]$UserMayNotChangePassword,$Description)
    if ($Name -ne 'SaftThings' -or $Password -isnot [Security.SecureString] -or !$AccountNeverExpires -or !$PasswordNeverExpires -or !$UserMayNotChangePassword) { throw 'New media account properties are incorrect.' }
    $script:actions.Add('create'); return $testUser
}
function Set-LocalUser {
    param($Name,$Password,$PasswordNeverExpires,[switch]$AccountNeverExpires,$UserMayChangePassword,$Description)
    if ($Name -ne 'SaftThings' -or $Password -isnot [Security.SecureString] -or !$PasswordNeverExpires -or !$AccountNeverExpires -or $UserMayChangePassword) { throw 'Existing media account properties are incorrect.' }
    $script:actions.Add('update')
}
function Enable-LocalUser { param($Name) $script:actions.Add('enable') }
function Add-LocalGroupMember { param($Group,$Member) if ($Group.SID -ne 'S-1-5-32-545') { throw 'Unexpected group.' }; $script:actions.Add('users') }
function Get-Acl { param($LiteralPath) return $script:permissions }
function Set-Acl { param($LiteralPath,$AclObject) $script:savedPermissions=$AclObject; $script:actions.Add('acl') }
foreach ($case in @('new','existing','administrator')) {
    $script:existing=$case -ne 'new'; $script:administrator=$case -eq 'administrator'; $script:alreadyMember=$case -eq 'existing'
    $script:actions=New-Object 'Collections.Generic.List[string]'
    $script:permissions=New-Object Security.AccessControl.DirectorySecurity
    $script:permissions.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($otherSid,'Read','Allow')))
    $script:savedPermissions=$null
    if ($script:administrator) {
        $rejected=$false
        try { Initialize-MediaAccount -ResolvedMedia 'C:\fixture-media' | Out-Null } catch { $rejected=$_.Exception.Message -match 'administrator' }
        if (!$rejected -or $script:actions.Count) { throw 'Administrator account was changed rather than rejected.' }
        continue
    }
    if ((Initialize-MediaAccount -ResolvedMedia 'C:\fixture-media') -ne 'SaftThings') { throw 'Incorrect media account returned.' }
    $expected=if ($case -eq 'new') { 'create,users,acl' } else { 'update,enable,acl' }
    if (($script:actions -join ',') -ne $expected) { throw 'Account setup was not idempotent.' }
    $rules=@($script:savedPermissions.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $mediaRule=@($rules | Where-Object { $_.IdentityReference.Value -eq $testUser.SID.Value })
    if ($mediaRule.Count -ne 1 -or ($mediaRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -ne [Security.AccessControl.FileSystemRights]::Modify -or
        $mediaRule[0].InheritanceFlags -ne ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit)) { throw 'Media Modify permission or inheritance is incorrect.' }
    if (@($rules | Where-Object { $_.IdentityReference.Value -eq $otherSid.Value }).Count -ne 1) { throw 'Unrelated NTFS permission was removed.' }
}

# Compile the actual client credential code and inspect native layout; do not write credentials.
$experience = [Management.Automation.Language.Parser]::ParseFile((Join-Path $workspace 'experiencex\Install-Experience.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Experience installer does not parse.' }
$native = $experience.Find({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('public static class MediaCredentialStore') }, $true)
if (!$native) { throw 'Credential implementation missing.' }
Add-Type -TypeDefinition $native.Value
$credentialType=[HomeTechnologies.MediaCredentialStore].GetNestedType('Credential',[Reflection.BindingFlags]::NonPublic)
$expectedSize=if ([IntPtr]::Size -eq 8) { 80 } else { 52 }
if ([Runtime.InteropServices.Marshal]::SizeOf([Activator]::CreateInstance($credentialType)) -ne $expectedSize) { throw 'Native CREDENTIAL layout is incorrect.' }
'Passed account creation, existing-account update, non-administrator enforcement, group membership and NTFS permission preservation.'
'Passed native Credential Manager compilation and CREDENTIAL layout without storing real credentials.'
