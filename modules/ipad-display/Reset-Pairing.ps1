[CmdletBinding()]
param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$state = Join-Path $env:LOCALAPPDATA 'iPadWiFiDisplay\session'
$address = (Get-Content -LiteralPath (Join-Path $state 'address.txt') -Raw).Trim()
. (Join-Path $PSScriptRoot 'Common.ps1')
$python = Get-DisplayPython
$port = Get-DisplayVideoPort
$setup = Join-Path $PSScriptRoot 'setup_receiver.py'
& (Join-Path $PSScriptRoot 'Stop-Display.ps1') -IncludeLegacy
& $python $setup rotate $address --video-port $port
if ($LASTEXITCODE -ne 0) { throw 'Could not replace the pairing token.' }
& (Join-Path $PSScriptRoot 'Start-Display.ps1') -Address $address -NoBrowser:$NoBrowser
