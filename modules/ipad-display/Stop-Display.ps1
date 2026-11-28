[CmdletBinding()]
param([switch]$IncludeLegacy)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$mutex = Enter-DisplayLock
try {
    $addressFile = Join-Path $DisplayState 'address.txt'
    if (-not (Test-Path -LiteralPath $addressFile)) { return }
    $address = (Get-Content -Raw -LiteralPath $addressFile).Trim()
    $port = Get-DisplayVideoPort
    $control = Get-DisplayListener $address 8442
    $video = Get-DisplayListener $address $port
    $ours = @(
        Get-DisplayProcess $control (Join-Path $PSScriptRoot 'setup_receiver.py')
        Get-DisplayProcess $video (Join-Path $PSScriptRoot 'host\server.py')
    )
    $stoppingVideo = $video -and $video.OwningProcess -in $ours.ProcessId
    if ($IncludeLegacy -and (($control -and $control.OwningProcess -notin $ours.ProcessId) -or ($video -and $video.OwningProcess -notin $ours.ProcessId))) {
        $legacy = @(Get-LegacyWorkers $control $video $address $port)
        if ($legacy.Count -ne 2) { throw 'Could not verify both workers as the original iPad display. No process was stopped.' }
        $ours = $legacy
        $stoppingVideo = $true
    }
    if ($stoppingVideo) {
        $helper = Join-Path $PSScriptRoot 'driver\display_lifecycle.py'
        $python = Get-DisplayPython
    }
    foreach ($worker in $ours) {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($worker.ProcessId)"
        if ($current -and $current.CreationDate -eq $worker.CreationDate) {
            Stop-Process -Id $worker.ProcessId -ErrorAction Stop
        }
    }
    if ($stoppingVideo) {
        $panel = & $python $helper status | ConvertFrom-Json
        if ($panel.managed -and $panel.attached) {
            & $python $helper detach | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Workers stopped, but Windows kept the virtual screen active.' }
        }
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
