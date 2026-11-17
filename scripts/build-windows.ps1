[CmdletBinding()]
param([string] $DotNet = 'dotnet')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'windows/RemoteControl.csproj'
$output = Join-Path $repo 'artifacts/windows'

# The included runtime notices correspond to this runtime release.
$runtimeVersion = '10.0.15'
& $DotNet publish $project -c Release -r win-x64 --self-contained true `
    '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' `
    '-p:Deterministic=true' "-p:PathMap=$repo=/src/PhoneRemote" `
    "-p:RuntimeFrameworkVersion=$runtimeVersion" -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $repo 'docs/使用指南.md') -Destination (Join-Path $output '使用指南.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Destination (Join-Path $output '版本说明.md') -Force
$notices = Join-Path $output 'Runtime Notices'
New-Item -ItemType Directory -Force $notices | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'third_party/runtime') -Force | Copy-Item -Destination $notices -Recurse -Force
Get-Item -LiteralPath (Join-Path $output 'PhoneRemote.exe') | Select-Object FullName, Length
