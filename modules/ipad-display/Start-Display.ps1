[CmdletBinding()]
param([string]$Address, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not $Address) {
    $saved = Join-Path $DisplayState 'address.txt'
    if (Test-Path -LiteralPath $saved) {
        $last = (Get-Content -Raw -LiteralPath $saved).Trim()
        if (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $last -AddressState Preferred -ErrorAction SilentlyContinue) { $Address = $last }
    }
    if (-not $Address) {
        $wifi = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' -and $_.MediaType -eq 'Native 802.11' })
        if ($wifi.Count -ne 1) { throw 'Choose a LAN IPv4 address for the iPad display.' }
        $Address = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $wifi[0].ifIndex -AddressState Preferred |
            Where-Object { -not $_.SkipAsSource } | Select-Object -First 1).IPAddress
    }
}
if (-not $Address -or -not (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $Address -ErrorAction SilentlyContinue)) {
    throw 'Choose a LAN IPv4 address assigned to this PC.'
}

$mutex = Enter-DisplayLock
$created = @()
try {
    $port = Get-DisplayVideoPort
    $setup = Join-Path $PSScriptRoot 'setup_receiver.py'
    $server = Join-Path $PSScriptRoot 'host\server.py'
    $controlSocket = Get-DisplayListener $Address 8442
    $videoSocket = Get-DisplayListener $Address $port
    $localSocket = Get-DisplayListener '127.0.0.1' 8442
    if ($localSocket -and (-not $controlSocket -or $localSocket.OwningProcess -ne $controlSocket.OwningProcess)) {
        throw 'The local iPad control port 8442 is already in use. Stop the previous instance first.'
    }
    $control = Get-DisplayProcess $controlSocket $setup
    $video = Get-DisplayProcess $videoSocket $server
    $legacy = @()
    if (($controlSocket -and -not $control) -or ($videoSocket -and -not $video)) {
        $legacy = @(Get-LegacyWorkers $controlSocket $videoSocket $Address $port)
        if ($legacy.Count -ne 2) { throw "A required iPad port (8442 or $port) is in use. Stop the service using that port and retry." }
    }
    if (($control -and $video) -or $legacy.Count -eq 2) {
        Write-Output 'PHONEREMOTE_IPAD_REUSED'
    } else {
        $python = Initialize-DisplayRuntime
        & $python $setup prepare $Address --video-port $port
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the iPad TLS identity and Web Clip.' }
        [IO.File]::WriteAllText((Join-Path $DisplayState 'address.txt'), $Address)
        [IO.File]::WriteAllText((Join-Path $DisplayState 'video-port.txt'), "$port")

        $workers = @(
            @{ Name='control'; Script=$setup; Port=8442; Running=$control; Args=@('serve',$Address,'--video-port',"$port") },
            @{ Name='video'; Script=$server; Port=$port; Running=$video; Args=@(Get-DisplayVideoArguments $Address $port) }
        )
        foreach ($worker in $workers) {
            if ($worker.Running) { continue }
            $name = $worker.Name
            $launch = Join-Path $PSScriptRoot 'launch_worker.py'
            $workerArgs = $worker.Args
            $workerId = & $python -I $launch $DisplayState $name $worker.Script @workerArgs
            if ($LASTEXITCODE -ne 0) { throw "$name failed to launch. See $name.err.log." }
            $proc = Get-Process -Id ([int]$workerId) -ErrorAction Stop
            $created += [pscustomobject]@{ Wrapper=$proc; Worker=$worker; ListenerProcess=$null }
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                if ($proc.HasExited) { throw "$name failed to start. See $name.err.log." }
                $listening = Get-DisplayProcess (Get-DisplayListener $Address $worker.Port) $worker.Script
                if ($listening -and ($listening.ProcessId -eq $proc.Id -or $listening.ParentProcessId -eq $proc.Id)) {
                    $created[-1].ListenerProcess = $listening.ProcessId
                    break
                }
                Start-Sleep -Milliseconds 120
            } while ([DateTime]::UtcNow -lt $deadline)
            if (-not $created[-1].ListenerProcess) { throw "$name did not open port $($worker.Port)." }
        }
        Write-Output 'PHONEREMOTE_IPAD_STARTED'
    }
    if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:8442/' }
} catch {
    foreach ($item in $created) {
        if (-not $item.Wrapper.HasExited) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($item.Wrapper.Id)" -ErrorAction SilentlyContinue)
            foreach ($child in $children) {
                if (Test-DisplayCommand $child $item.Worker.Script) {
                    Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                }
            }
            if (-not $item.Wrapper.HasExited) { $item.Wrapper.Kill() }
        }
    }
    throw
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
