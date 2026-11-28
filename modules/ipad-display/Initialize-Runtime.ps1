$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$mutex = Enter-DisplayLock
try {
    Initialize-DisplayRuntime | Out-Null
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
