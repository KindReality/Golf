$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot
. (Join-Path $workspace 'Shared\Networking\NetworkAccess.ps1')
$script:rules=@{}
function Get-NetFirewallRule {
    [CmdletBinding()] param($PolicyStore,$Name,$Enabled,$Direction,$Action)
    if ($Name) { if (!$script:rules.ContainsKey($Name)) { throw 'Missing rule' }; return $script:rules[$Name] }
    return @($script:rules.Values | Where-Object Action -eq 'Block')
}
function Get-NetFirewallPortFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$Rule)
    process { [pscustomobject]@{Protocol=$Rule.Protocol;LocalPort=$Rule.Ports} }
}
function Get-NetFirewallApplicationFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$Rule)
    process { [pscustomobject]@{Program=$Rule.Program} }
}
function Get-NetFirewallAddressFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$Rule)
    process { [pscustomobject]@{RemoteAddress=$Rule.RemoteAddress} }
}
foreach ($application in @('Controller','ExperienceX')) {
    $runtime='C:\fixture\'+$application+'.exe'
    $script:rules=@{}
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Missing rules accepted.' }
    foreach ($spec in @(Get-SaftNetworkRules $application $runtime)) {
        $script:rules[$spec.Name]=[pscustomobject]@{Name=$spec.Name;Enabled='True';Direction='Inbound';Action='Allow';Profile='Private, Public';Protocol=$spec.Protocol;Ports=$spec.Ports;Program=$spec.Program;RemoteAddress='LocalSubnet'}
    }
    if (!(Test-SaftNetworkAccess $application $runtime)) { throw 'Valid setup would request elevation again.' }
    $udp=$script:rules['HomeTechnologies.'+$application+'.UDP']
    $udp.Profile='Private'
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Private-only rules accepted.' }
    $udp.Profile='Private, Public';$udp.Program='Any'
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Unscoped application rule accepted.' }
    $udp.Program=$runtime;$udp.Ports=@('54242')
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Incomplete ports accepted.' }
    $udp.Ports=@('54242','54243','5353');$udp.RemoteAddress='Any'
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Unrestricted remote addresses accepted.' }
    $udp.RemoteAddress='LocalSubnet'
    $script:rules['block']=[pscustomobject]@{Action='Block';Program=$runtime}
    if (Test-SaftNetworkAccess $application $runtime) { throw 'Overriding application block accepted.' }
    $script:rules['block'].Program='C:\unrelated.exe'
    if (!(Test-SaftNetworkAccess $application $runtime)) { throw 'Unrelated block prevented idempotent setup.' }
}
'Passed existing setup, profile changes, executable scope, port changes, subnet scope and explicit-block detection without modifying Windows.'
