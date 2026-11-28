#requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][ipaddress]$Address, [switch]$Remove)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$stateDir = Join-Path $env:LOCALAPPDATA 'iPadWiFiDisplay\session'
$log = Join-Path $stateDir 'setup-result.txt'
$rulePrefix = 'iPadWiFiDisplay.Prototype'
try {
    if ($Remove) {
        & (Join-Path $PSScriptRoot 'driver\Install-VirtualScreen.ps1') -Action Remove
        Get-NetFirewallRule -Name "$rulePrefix.*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        'Removed the project display device and firewall rules.' | Set-Content $log
        exit 0
    }
    $interface = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $Address.IPAddressToString
    $videoPort = Get-DisplayVideoPort
    $programs = @(foreach ($name in 'runtime-path.txt','runtime-base.txt') {
        $entry = Join-Path $stateDir $name
        if (-not (Test-Path -LiteralPath $entry)) { throw 'Select iPad display in PhoneRemote once to prepare the runtime before configuring this PC.' }
        $path = (Get-Content -LiteralPath $entry -Raw).Trim()
        if ($path -notmatch '^[a-zA-Z]:\\' -or (Split-Path -Leaf $path) -ne 'python.exe' -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'Invalid Python path. Start the iPad display module again.'
        }
        $path
    })
    $programs = $programs | Select-Object -Unique
    $bytes = $Address.GetAddressBytes()
    for ($i = 0; $i -lt 4; $i++) {
        $bits = [Math]::Min(8,[Math]::Max(0,([int]$interface.PrefixLength - $i * 8)))
        $bytes[$i] = $bytes[$i] -band ((255 -shl (8 - $bits)) -band 255)
    }
    $network = ($bytes -join '.') + '/' + $interface.PrefixLength
    $index = 0
    foreach ($program in $programs) {
        $index++
        foreach ($proto in 'TCP','UDP') {
            $name = "$rulePrefix.$index.$proto"
            $ports = if ($proto -eq 'TCP') { @('8442',"$videoPort") } else { @('49152-65535') }
            $argsForRule = @{
                Name = $name; DisplayName = "iPad Wi-Fi Display ($proto $index)"; Group = 'iPad Wi-Fi Display'
                Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any'; Protocol = $proto
                InterfaceAlias = $interface.InterfaceAlias; LocalAddress = $Address.IPAddressToString
                LocalPort = $ports; RemoteAddress = "$network"; Program = $program
            }
            Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule @argsForRule | Out-Null
        }
    }
    'LAN access configured. Installing virtual monitor.' | Set-Content $log
    & (Join-Path $PSScriptRoot 'driver\Install-VirtualScreen.ps1') -Action Install
    'OK' | Set-Content $log
} catch {
    $_.Exception.Message | Set-Content $log
    exit 1
}
