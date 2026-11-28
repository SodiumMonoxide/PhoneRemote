#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param([ValidateSet('Install', 'Remove', 'UpdateModes', 'RestoreModes')][string]$Action = 'Install')

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw 'Run this setup from x64 PowerShell on an x64 Windows PC.'
}
$store = Join-Path $env:ProgramFiles 'iPad WiFi Display Driver'
$registry = 'HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver'
$stateFile = Join-Path $store 'owned-device.json'
$package = Join-Path $store 'VirtualDisplayDriver'
$config = Join-Path $package 'vdd_settings.xml'
$hwid = 'Root\MttVDD'
$pnp = Join-Path $env:SystemRoot 'System32\pnputil.exe'

function Find-Target {
    foreach ($device in (Get-PnpDevice | Where-Object InstanceId -like 'ROOT\*')) {
        $ids = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data
        if ($ids -contains $hwid) { $device }
    }
}

function Read-VerifiedFile($name, $url, $digest) {
    $dst = Join-Path $store $name
    if ((Test-Path -LiteralPath $dst) -and (Get-FileHash -LiteralPath $dst).Hash -eq $digest) { return $dst }
    $cache = Join-Path (Join-Path $env:LOCALAPPDATA 'iPadWiFiDisplay\driver-staging') $name
    if (Test-Path -LiteralPath $cache) { Copy-Item -LiteralPath $cache -Destination $dst -Force }
    if (-not (Test-Path -LiteralPath $dst) -or (Get-FileHash -LiteralPath $dst).Hash -ne $digest) {
        Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing -TimeoutSec 90
    }
    if ((Get-FileHash -LiteralPath $dst).Hash -ne $digest) { throw "Unexpected SHA256 for $name" }
    return $dst
}

function Save-State($state) {
    $tmp = Join-Path $store 'owned-device.tmp'
    $state | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $stateFile -Force
}

function Set-VideoModes([xml]$xml) {
    # These are independent Windows display modes. The receiver chooses
    # among them based on its viewport and the requested transport quality.
    $modeNodes = @(
        '<resolution><width>1600</width><height>1200</height><refresh_rate>60</refresh_rate></resolution>'
        '<resolution><width>1600</width><height>1120</height><refresh_rate>60</refresh_rate></resolution>'
        '<resolution><width>1920</width><height>1440</height><refresh_rate>60</refresh_rate></resolution>'
        '<resolution><width>1920</width><height>1344</height><refresh_rate>60</refresh_rate></resolution>'
    ) -join ''
    $rates = '<g_refresh_rate>30</g_refresh_rate><g_refresh_rate>60</g_refresh_rate>'
    $resolutions = $xml.SelectSingleNode('/vdd_settings/resolutions')
    $globalRates = $xml.SelectSingleNode('/vdd_settings/global')
    $changed = $resolutions.InnerXml -ne $modeNodes -or $globalRates.InnerXml -ne $rates
    $resolutions.InnerXml = $modeNodes
    $globalRates.InnerXml = $rates
    return $changed
}

function Restart-OwnedAdapter($id) {
    & $pnp /restart-device $id | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not restart owned virtual adapter $id (code $LASTEXITCODE)." }
    for ($i = 0; $i -lt 10; $i++) {
        $device = Get-PnpDevice -InstanceId $id -PresentOnly -ErrorAction SilentlyContinue
        if ($device -and $device.Status -eq 'OK' -and (Get-PnpDevice -PresentOnly -Class Monitor -ErrorAction SilentlyContinue | Where-Object InstanceId -like 'DISPLAY\MTT1337\*')) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "The restarted virtual adapter or its monitor did not become ready."
}

function Update-OwnedModes($state, $existing) {
    if ($existing.Count -ne 1 -or -not $state -or $state.InstanceId -ne $existing[0].InstanceId -or $existing[0].Status -ne 'OK') {
        throw 'Could not verify ownership of a single available virtual adapter.'
    }
    $configured = Get-ItemPropertyValue -LiteralPath $registry -Name 'VDDPATH'
    if ($configured -ne $package) { throw "Refusing to edit another VDD configuration at $configured." }
    $backup = Join-Path $store 'vdd_settings.before-update.xml'
    $oldXml = [IO.File]::ReadAllBytes($config)
    $pending = Join-Path $package 'vdd_settings.next.xml'
    if ($Action -eq 'RestoreModes') {
        if (-not (Test-Path -LiteralPath $backup)) { throw 'No previous set of display modes was saved.' }
        [IO.File]::WriteAllBytes($pending, [IO.File]::ReadAllBytes($backup))
    } else {
        [xml]$doc = Get-Content -Raw -LiteralPath $config
        if (-not (Set-VideoModes $doc)) { Write-Host 'The requested modes are already configured.'; return }
        $doc.Save($pending)
        [IO.File]::WriteAllBytes($backup, $oldXml)
    }
    $rollback = Join-Path $package 'vdd_settings.transaction.xml'
    [IO.File]::Replace($pending, $config, $rollback)
    try {
        Restart-OwnedAdapter $state.InstanceId
    } catch {
        $originalFailure = $_
        [IO.File]::Replace($rollback, $config, $pending)
        Restart-OwnedAdapter $state.InstanceId
        throw $originalFailure
    }
    Write-Host "Updated modes on $($state.InstanceId). Verify advertised monitor modes with display_lifecycle.py modes."
}

function Enable-ExtendedPair {
    $monitors = @()
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $monitors = @(Get-PnpDevice -PresentOnly -Class Monitor)
        if ($monitors.InstanceId -like 'DISPLAY\MTT1337\*') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($monitors.Count -ne 2 -or -not ($monitors.InstanceId -like 'DISPLAY\MTT1337\*')) {
        Write-Warning 'Leaving the existing monitor layout in place. Check Windows Display settings for the virtual monitor.'
        return
    }
    if (-not ('IPadWifiDisplay.Layout' -as [type])) {
        Add-Type -Namespace IPadWifiDisplay -Name Layout -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint paths, System.IntPtr pathData, uint modes, System.IntPtr modeData, uint flags);
'@
    }
    # SM_CMONITORS counts desktops in the current layout. Request Extend only for
    # one physical panel plus our own display, and only if it is still necessary.
    if ([IPadWifiDisplay.Layout]::GetSystemMetrics(80) -eq 1) {
        $code = [IPadWifiDisplay.Layout]::SetDisplayConfig(0, [IntPtr]::Zero, 0, [IntPtr]::Zero, 0x84)
        if ($code -ne 0) { Write-Warning "Windows did not apply Extend (code $code). Select Extend in Display settings." }
    }
}

function Remove-OwnedDevice($state) {
    $targets = @(Find-Target)
    if ($state.InstanceId) {
        $targets = @($targets | Where-Object InstanceId -eq $state.InstanceId)
    } elseif ($targets.Count -gt 1) {
        throw 'Multiple devices match an interrupted installation. No device was removed.'
    }
    foreach ($target in $targets) {
        & $pnp /remove-device $target.InstanceId | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Windows could not remove $($target.InstanceId)." }
    }
    $setting = Get-ItemPropertyValue -LiteralPath $registry -Name 'VDDPATH' -ErrorAction SilentlyContinue
    if ($setting -eq $package) { Remove-ItemProperty -LiteralPath $registry -Name 'VDDPATH' }
    if ($state.CreatedKey -and (Test-Path -LiteralPath $registry)) {
        $key = Get-Item -LiteralPath $registry
        if (-not $key.ValueCount -and -not $key.SubKeyCount) { Remove-Item -LiteralPath $registry }
    }
    Remove-Item -LiteralPath $stateFile
    Write-Host 'Removed the virtual display device. Verified install files remain cached for reuse.'
}

if ($Action -eq 'Remove' -and -not (Test-Path -LiteralPath $stateFile)) {
    Write-Host 'No virtual display device is owned by this setup.'
    return
}
New-Item -ItemType Directory -Path $store -Force | Out-Null
$lock = [IO.File]::Open((Join-Path $store 'install.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $state = if (Test-Path -LiteralPath $stateFile) { Get-Content -Raw -LiteralPath $stateFile | ConvertFrom-Json }
    if ($Action -eq 'Remove') { Remove-OwnedDevice $state; return }
    $existing = @(Find-Target)
    if ($Action -in 'UpdateModes','RestoreModes') { Update-OwnedModes $state $existing; return }
    if ($existing.Count -gt 0) {
        if ($existing.Count -ne 1 -or $existing[0].Status -ne 'OK') {
            throw 'A VirtualDrivers device already exists but is unavailable. No duplicate was created.'
        }
        Write-Host "Reusing existing virtual display adapter $($existing[0].InstanceId)."
        if ($state -and $state.InstanceId -eq $existing[0].InstanceId) { Enable-ExtendedPair }
        return
    }
    $existingPath = Get-ItemPropertyValue -LiteralPath $registry -Name 'VDDPATH' -ErrorAction SilentlyContinue
    if ($existingPath -and $existingPath -ne $package) { throw "Existing VirtualDrivers settings at $existingPath. No configuration was overwritten." }
    $defaultFile = 'C:\VirtualDisplayDriver\vdd_settings.xml'
    if (-not $existingPath -and (Test-Path -LiteralPath $defaultFile)) { throw "An existing driver configuration is present at $defaultFile. It was preserved." }

    $driverZip = Read-VerifiedFile 'VirtualDisplayDriver-x86.Driver.Only.zip' 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip' 'E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A'
    $devconCab = Read-VerifiedFile 'wdk-devcon.cab' 'https://download.microsoft.com/download/7/b/f/7bfc8dbe-00cb-47de-b856-70e696ef4f46/wdk/Installers/787bee96dbd26371076b37b13c405890.cab' '6ECFAF7042C525DDB6C0BF0501BECFBCE85CBDF2A33B1BCA67734FEA567AF3B8'
    Expand-Archive -LiteralPath $driverZip -DestinationPath $store -Force
    foreach ($name in 'mttvdd.cat','MttVDD.dll') {
        $sig = Get-AuthenticodeSignature -LiteralPath (Join-Path $package $name)
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne '3CF8CF26D8BA266C3A483AB7D26D4A818E317D76') {
            throw "Signature verification failed for $name."
        }
    }
    $member = 'filbad6e2cce5ebc45a401e19c613d0a28f'
    & "$env:SystemRoot\System32\expand.exe" $devconCab "-F:$member" $store | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract Microsoft DevCon.' }
    $devcon = Join-Path $store 'devcon.exe'
    Copy-Item -LiteralPath (Join-Path $store $member) -Destination $devcon -Force
    $sig = Get-AuthenticodeSignature -LiteralPath $devcon
    if ((Get-FileHash -LiteralPath $devcon).Hash -ne 'A92D4BC174079DF703512E778E3192F524E4D0E33FC0EF718ACC43C29E3D6E30' -or $sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
        throw 'Microsoft DevCon verification failed.'
    }
    [xml]$xml = Get-Content -Raw -LiteralPath $config
    $xml.vdd_settings.monitors.count = '1'
    Set-VideoModes $xml | Out-Null
    $xml.vdd_settings.options.HardwareCursor = 'false'
    $xml.Save($config)
    if (-not $state) {
        $state = [pscustomobject]@{ InstanceId = ''; CreatedKey = -not (Test-Path -LiteralPath $registry) }
        Save-State $state
    }
    try {
        if (-not (Test-Path -LiteralPath $registry)) { New-Item -Path $registry -Force | Out-Null }
        New-ItemProperty -LiteralPath $registry -Name VDDPATH -PropertyType String -Value $package -Force | Out-Null
        & $devcon install (Join-Path $package 'MttVDD.inf') $hwid | Out-Host
        $code = $LASTEXITCODE
        if ($code -notin 0,1) { throw "DevCon failed with exit code $code. Check Windows driver-installation prompts or setupapi.dev.log." }
        $new = @(Find-Target)
        if ($new.Count -ne 1) { throw 'Windows did not register one virtual adapter.' }
        $state.InstanceId = $new[0].InstanceId
        Save-State $state
        if ($code -eq 1) { Write-Warning 'Windows reports that a reboot is required.' }
        if ($new[0].Status -ne 'OK' -and $code -eq 0) { throw "Virtual adapter status: $($new[0].Status)" }
        Write-Host "Registered the independent virtual adapter $($state.InstanceId)."
        if ($code -eq 0) { Enable-ExtendedPair }
    } catch {
        Remove-OwnedDevice $state
        throw
    }
} finally { $lock.Dispose() }
