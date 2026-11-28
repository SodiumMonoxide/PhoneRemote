$DisplayState = Join-Path $env:LOCALAPPDATA 'iPadWiFiDisplay\session'
$DisplayRuntime = Join-Path $env:LOCALAPPDATA 'iPadWiFiDisplay\runtime'

function Get-DisplayVideoPort {
    $setting = Join-Path $DisplayState 'video-port.txt'
    if (Test-Path -LiteralPath $setting) {
        $savedPort = (Get-Content -Raw -LiteralPath $setting).Trim()
        if ($savedPort -notin '8443','8453') { throw 'Invalid iPad display port in video-port.txt.' }
        return [int]$savedPort
    }
    $profilePath = Join-Path $DisplayState 'receiver.mobileconfig'
    if (Test-Path -LiteralPath $profilePath) {
        $plist = New-Object System.Xml.XmlDocument
        $plist.XmlResolver = $null
        $plist.Load($profilePath)
        $url = $plist.SelectSingleNode("//dict[key[.='PayloadType']/following-sibling::*[1][.='com.apple.webClip.managed']]/key[.='URL']/following-sibling::string[1]")
        if ($url) {
            $receiver = [uri]$url.InnerText
            if ($receiver.Scheme -eq 'https' -and $receiver.Port -in 8443,8453) { return $receiver.Port }
        }
        throw 'Could not identify the installed iPad Web Clip port.'
    }
    return 8453
}

function Get-DisplayPython([switch]$Install) {
    $check = Join-Path $PSScriptRoot 'check_runtime.py'
    $owned = Join-Path $DisplayRuntime 'Scripts\python.exe'
    $candidates = @($owned)
    $registered = Join-Path $DisplayState 'runtime-path.txt'
    if (Test-Path -LiteralPath $registered) { $candidates += (Get-Content -Raw -LiteralPath $registered).Trim() }
    # Existing standalone installs register both the venv and its base interpreter.
    $apps = @(Get-NetFirewallRule -Name 'iPadWiFiDisplay.Prototype.*' -ErrorAction SilentlyContinue |
        Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue)
    $paths = @($apps | ForEach-Object { $_.Program } | Where-Object { $_ -and $_ -like '*\python.exe' })
    $candidates += @($paths | Where-Object { $_ -like '*\Scripts\python.exe' }) + $paths
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $fromLauncher = & $launcher.Source -3.12 -I -c 'import sys; print(sys.executable)' 2>$null
        if ($LASTEXITCODE -eq 0) { $candidates += $fromLauncher }
    }
    $candidates += @(Get-Command python.exe -All -ErrorAction SilentlyContinue | ForEach-Object Source |
        Where-Object { $_ -notlike '*\Microsoft\WindowsApps\python.exe' })
    $base = $null
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        & $candidate -I $check
        if ($LASTEXITCODE -eq 0) { return $candidate }
        if (-not $base) {
            & $candidate -I $check --interpreter
            if ($LASTEXITCODE -eq 0) { $base = $candidate }
        }
    }
    if (-not $Install) { throw 'Select iPad display in PhoneRemote to initialize its Python runtime first.' }
    if (-not $base) { throw 'Install 64-bit Python 3.12 for Windows with the Python launcher, then select iPad display again.' }
    $readyToInstall = $false
    if (Test-Path -LiteralPath $owned -PathType Leaf) {
        & $owned -I $check --interpreter
        $readyToInstall = $LASTEXITCODE -eq 0
    }
    if (-not $readyToInstall) {
        & $base -I -m venv $DisplayRuntime | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the iPad display Python runtime.' }
    }
    & $owned -I -m pip install --disable-pip-version-check --only-binary=:all: -r (Join-Path $PSScriptRoot 'host\requirements.txt') -r (Join-Path $PSScriptRoot 'requirements-setup.txt') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the iPad display dependencies. Check network access and retry.' }
    & $owned -I $check
    if ($LASTEXITCODE -ne 0) { throw 'The iPad display runtime did not pass its dependency check.' }
    return $owned
}

function Get-DisplayListener([string]$Address, [int]$Port) {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -in $Address,'0.0.0.0','::' } | Select-Object -First 1
}

function Initialize-DisplayRuntime {
    New-Item -ItemType Directory -Force -Path $DisplayState | Out-Null
    $python = Get-DisplayPython -Install
    $basePython = & $python -I -c 'import sys; print(sys._base_executable)'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $basePython)) { throw 'Could not resolve the Python interpreter.' }
    [IO.File]::WriteAllText((Join-Path $DisplayState 'runtime-path.txt'), $python)
    [IO.File]::WriteAllText((Join-Path $DisplayState 'runtime-base.txt'), $basePython)
    return $python
}

function Get-DisplayProcess($Listener, [string]$Script) {
    if (-not $Listener) { return }
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$($Listener.OwningProcess)"
    if (Test-DisplayCommand $proc $Script) { return $proc }
}

function Test-DisplayCommand($Process, [string]$Script) {
    $path = [regex]::Escape($Script)
    return $Process.Name -eq 'python.exe' -and $Process.CommandLine -match ('^\s*(?:"[^"]+"|\S+)\s+(?:"' + $path + '"|' + $path + ')(?:\s|$)')
}

function Get-DisplayVideoArguments([string]$Address, [int]$Port) {
    @('--bind', $Address, '--port', "$Port", '--cert', (Join-Path $DisplayState 'server.pem'),
        '--key', (Join-Path $DisplayState 'key.pem'), '--token-file', (Join-Path $DisplayState 'pair-token'),
        '--approval-dir', (Join-Path $DisplayState 'approval'), '--display', 'auto', '--manage-display')
}

function Join-WorkerArguments($Parts) {
    ($Parts | ForEach-Object { '"' + $_ + '"' }) -join ' '
}

function Get-LegacyWorkers($Control, $Video, [string]$Address, [int]$Port) {
    if (-not $Control -or -not $Video -or $Port -ne 8443) { return }
    $console = Get-CimInstance Win32_Process -Filter "ProcessId=$($Control.OwningProcess)"
    $stream = Get-CimInstance Win32_Process -Filter "ProcessId=$($Video.OwningProcess)"
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($process in @($console, $stream)) {
        if ($process.Name -ne 'python.exe' -or (Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid).Sid -ne $sid) { return }
    }
    if ($console.CommandLine -notmatch '^\s*"[^"]+"\s+"(?<script>[^"]+\\setup_receiver\.py)"\s+') { return }
    $setup = $Matches.script
    $server = Join-Path (Split-Path -Parent $setup) 'host\server.py'
    $expectedConsole = Join-WorkerArguments @($setup, 'serve', $Address)
    $expectedVideo = Join-WorkerArguments (@($server) + @(Get-DisplayVideoArguments $Address $Port))
    if (-not $console.CommandLine.EndsWith(' ' + $expectedConsole,[StringComparison]::OrdinalIgnoreCase) -or
        -not $stream.CommandLine.EndsWith(' ' + $expectedVideo,[StringComparison]::OrdinalIgnoreCase)) { return }
    # Only the released standalone worker pair may be taken over on explicit stop.
    if (-not (Test-Path -LiteralPath $server -PathType Leaf) -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) { return }
    if ((Get-FileHash -LiteralPath $setup).Hash -ne '582319DFE28DE03CD30CB3229F6036A584E8A17A39A078C117EC012585BE69D6' -or
        (Get-FileHash -LiteralPath $server).Hash -ne '3601EF36D153E4421752A6CC445245C7969F6ED2A541A995FA8F36A52D7CED22') { return }
    @($console, $stream)
}

function Enter-DisplayLock {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = [Threading.Mutex]::new($false, "Local\PhoneRemote.iPadDisplay.$sid")
    try {
        if (-not $mutex.WaitOne([TimeSpan]::FromMinutes(8))) { throw 'The iPad display is still being initialized.' }
    } catch [Threading.AbandonedMutexException] { }
    return $mutex
}
