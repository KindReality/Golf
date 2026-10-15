# Shared by the installers. Ordinary restart/update scripts never change the firewall.
function Get-SaftNetworkRules([string]$Application, [string]$RuntimePath) {
    if ($Application -notin @('Controller','ExperienceX')) { throw 'Unknown application.' }
    $prefix = 'HomeTechnologies.'+$Application
    $common = @{Profile=@('Private','Public');RemoteAddress='LocalSubnet'}
    @(
        @{Name=$prefix+'.UDP';Display='Home Technologies '+$Application+' events, commands and WLED discovery';Protocol='UDP';Ports=@('54242','54243','5353');Program=$RuntimePath;Profile=$common.Profile},
        @{Name=$prefix+$(if ($Application -eq 'Controller') {'.Definitions'} else {'.Inventory'});Display='Home Technologies '+$Application+' shared definitions and inventory';Protocol='TCP';Ports=@($(if ($Application -eq 'Controller') {'54244'} else {'54245'}));Program=$RuntimePath;Profile=$common.Profile}
    )
    if ($Application -eq 'Controller') {
        @{Name=$prefix+'.Media';Display='Home Technologies media share';Protocol='TCP';Ports=@('445');Program='Any';Profile=$common.Profile}
    }
}
function Test-SaftNetworkAccess([string]$Application, [string]$RuntimePath) {
    try {
        foreach ($spec in @(Get-SaftNetworkRules $Application $RuntimePath)) {
            $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $spec.Name -ErrorAction Stop)
            if ($rules.Count -ne 1) { return $false }
            $rule=$rules[0]
            if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or
                ($rule.Profile.ToString() -replace '\s','') -ne 'Private,Public') { return $false }
            $ports=$rule | Get-NetFirewallPortFilter -ErrorAction Stop
            if ($ports.Protocol.ToString() -notin @($spec.Protocol,$(if ($spec.Protocol -eq 'UDP') {'17'} else {'6'})) -or
                (@($ports.LocalPort | Sort-Object) -join ',') -ne (@($spec.Ports | Sort-Object) -join ',')) { return $false }
            $program=$rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
            $addresses=$rule | Get-NetFirewallAddressFilter -ErrorAction Stop
            if ($program.Program -ine $spec.Program -or (@($addresses.RemoteAddress) -join ',') -ne 'LocalSubnet') { return $false }
        }
        # A canceled Windows prompt leaves explicit blocks, which override allow rules.
        $blocks=@(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Inbound -Action Block -ErrorAction Stop |
            Get-NetFirewallApplicationFilter -ErrorAction Stop | Where-Object { $_.Program -ieq $RuntimePath })
        return $blocks.Count -eq 0
    } catch { return $false }
}
function Set-SaftNetworkAccess([string]$Application, [string]$RuntimePath) {
    $RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
    if ([IO.Path]::GetFileName($RuntimePath) -ine ($Application+'.exe')) { throw 'Unexpected application executable.' }
    foreach ($spec in @(Get-SaftNetworkRules $Application $RuntimePath)) {
        $existing=Get-NetFirewallRule -PolicyStore PersistentStore -Name $spec.Name -ErrorAction SilentlyContinue
        if ($existing) {
            Set-NetFirewallRule -PolicyStore PersistentStore -Name $spec.Name -Enabled True -Direction Inbound -Action Allow -Profile $spec.Profile -RemoteAddress LocalSubnet -Program $spec.Program -Service Any -Protocol $spec.Protocol -LocalPort $spec.Ports | Out-Null
        } else {
            New-NetFirewallRule -PolicyStore PersistentStore -Name $spec.Name -DisplayName $spec.Display -Enabled True -Direction Inbound -Action Allow -Profile $spec.Profile -RemoteAddress LocalSubnet -Program $spec.Program -Protocol $spec.Protocol -LocalPort $spec.Ports | Out-Null
        }
    }
    # Remove only Windows' per-application prompt rules for this exact runtime path.
    # Keep administrator-created blocks and policies intact.
    Get-NetFirewallApplicationFilter -PolicyStore PersistentStore | Where-Object { $_.Program -ieq $RuntimePath } |
        Get-NetFirewallRule | Where-Object { $_.Name -match '^(TCP|UDP) Query User' } |
        Remove-NetFirewallRule
    if (!(Test-SaftNetworkAccess $Application $RuntimePath)) { throw 'Network rules could not be verified. An administrator policy may override them.' }
    Write-Host ($Application+': local-subnet network access ready on Public and Private networks.')
}
