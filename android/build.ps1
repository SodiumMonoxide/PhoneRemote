[CmdletBinding()]
param(
    [string] $AndroidSdk = $env:ANDROID_HOME,
    [string] $JavaHome = $env:JAVA_HOME,
    [string] $BuildToolsVersion = '35.0.1',
    [int] $PlatformVersion = 35,
    [string] $BuildToolsPath,
    [string] $PlatformJar,
    [string] $KeystorePath,
    [string] $StorePasswordFile,
    [string] $KeyAlias,
    [string] $KeyPasswordFile
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$repo = [IO.Path]::GetFullPath((Join-Path $source '..'))
$web = Join-Path $repo 'windows/Web'
$output = Join-Path $repo 'artifacts'
if (-not $AndroidSdk) { $AndroidSdk = $env:ANDROID_SDK_ROOT }
if (-not $JavaHome) { throw 'Set JAVA_HOME to a JDK 21 directory, or pass -JavaHome.' }
if (-not $BuildToolsPath) {
    if (-not $AndroidSdk) { throw 'Set ANDROID_HOME or pass -AndroidSdk.' }
    $BuildToolsPath = Join-Path $AndroidSdk "build-tools/$BuildToolsVersion"
}
if (-not $PlatformJar) {
    if (-not $AndroidSdk) { throw 'Set ANDROID_HOME or pass -AndroidSdk.' }
    $PlatformJar = Join-Path $AndroidSdk "platforms/android-$PlatformVersion/android.jar"
}

function Invoke-Tool([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Tool failed ($LASTEXITCODE): $File" }
}

function Assert-ExternalFile([string] $Path, [string] $Label) {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $repoPrefix = $repo.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be kept outside the repository."
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Label is not a file." }
    return $resolved
}

$signing = [bool]($KeystorePath -or $StorePasswordFile -or $KeyAlias -or $KeyPasswordFile)
if ($signing) {
    if (-not ($KeystorePath -and $StorePasswordFile -and $KeyAlias)) {
        throw 'Signing requires -KeystorePath, -StorePasswordFile and -KeyAlias together.'
    }
    $KeystorePath = Assert-ExternalFile $KeystorePath 'Keystore'
    $StorePasswordFile = Assert-ExternalFile $StorePasswordFile 'Store password file'
    if ($KeyPasswordFile) { $KeyPasswordFile = Assert-ExternalFile $KeyPasswordFile 'Key password file' }
}

$required = @(
    $PlatformJar,
    (Join-Path $JavaHome 'bin/javac.exe'),
    (Join-Path $JavaHome 'bin/jar.exe'),
    (Join-Path $BuildToolsPath 'aapt2.exe'),
    (Join-Path $BuildToolsPath 'd8.bat'),
    (Join-Path $BuildToolsPath 'zipalign.exe')
)
if ($signing) { $required += (Join-Path $BuildToolsPath 'apksigner.bat') }
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing build dependency: $file" }
}

# Use a fresh directory so stale class/dex files cannot enter a new APK.
$build = Join-Path $repo ('.build/android/' + [Guid]::NewGuid().ToString('N'))
$classes = Join-Path $build 'classes'
$dex = Join-Path $build 'dex'
$assets = Join-Path $build 'assets'
New-Item -ItemType Directory -Force $classes, $dex, $assets, $output | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
$hasher = [Security.Cryptography.SHA256]::Create()
$oldJavaHome = $env:JAVA_HOME
$env:JAVA_HOME = $JavaHome
try {
    $script = [IO.File]::ReadAllText((Join-Path $web 'app.js')).Replace("`r`n", "`n")
    $style = [IO.File]::ReadAllText((Join-Path $web 'styles.css')).Replace("`r`n", "`n")
    $page = [IO.File]::ReadAllText((Join-Path $web 'index.html'))
    if ($script -match '</script' -or $style -match '</style') { throw 'Page assets cannot be inlined.' }
    $externalScript = '<script src="/app.js" defer></script>'
    $externalStyle = '<link rel="stylesheet" href="/styles.css">'
    if (-not ($page.Contains($externalScript) -and $page.Contains($externalStyle) -and $page.Contains('</body>'))) {
        throw 'Web page asset references changed.'
    }
    $scriptHash = [Convert]::ToBase64String($hasher.ComputeHash($encoding.GetBytes($script)))
    $styleHash = [Convert]::ToBase64String($hasher.ComputeHash($encoding.GetBytes($style)))
    $csp = "default-src 'none'; script-src 'sha256-$scriptHash'; style-src 'sha256-$styleHash'; img-src blob:; base-uri 'none'; form-action 'none'"
    $policy = '<meta http-equiv="Content-Security-Policy" content="' + $csp + '">'
    $page = $page.Replace($externalScript, '').Replace($externalStyle, "$policy<style>$style</style>")
    $page = $page.Replace('</body>', "<script>$script</script>`n</body>")
    [IO.File]::WriteAllText((Join-Path $assets 'remote.html'), $page, $encoding)

    $manifest = Join-Path $source 'AndroidManifest.xml'
    [xml] $manifestXml = [IO.File]::ReadAllText($manifest)
    $version = $manifestXml.manifest.GetAttribute('versionName', 'http://schemas.android.com/apk/res/android')
    $suffix = if ($signing) { '' } else { '-unsigned' }
    $apk = Join-Path $output "PhoneRemote-v$version-Android$suffix.apk"
    $compiledRes = Join-Path $build 'resources.zip'
    $unsigned = Join-Path $build 'unsigned.apk'
    $aligned = Join-Path $build 'aligned.apk'
    Invoke-Tool (Join-Path $BuildToolsPath 'aapt2.exe') @('compile', '--dir', (Join-Path $source 'res'), '-o', $compiledRes)
    Invoke-Tool (Join-Path $BuildToolsPath 'aapt2.exe') @('link', '-I', $PlatformJar, '--manifest', $manifest, '-A', $assets, '-o', $unsigned, $compiledRes)
    $java = @(Get-ChildItem -LiteralPath (Join-Path $source 'src') -Filter '*.java' -Recurse | ForEach-Object FullName)
    Invoke-Tool (Join-Path $JavaHome 'bin/javac.exe') (@('-encoding', 'UTF-8', '--release', '8', '-Xlint:-options', '-classpath', $PlatformJar, '-d', $classes) + $java)
    $compiledClasses = @(Get-ChildItem -LiteralPath $classes -Filter '*.class' -Recurse | ForEach-Object FullName)
    Invoke-Tool (Join-Path $BuildToolsPath 'd8.bat') (@('--release', '--min-api', '29', '--lib', $PlatformJar, '--output', $dex) + $compiledClasses)
    Invoke-Tool (Join-Path $JavaHome 'bin/jar.exe') @('uf', $unsigned, '-C', $dex, 'classes.dex')
    Invoke-Tool (Join-Path $BuildToolsPath 'zipalign.exe') @('-f', '4', $unsigned, $aligned)

    if ($signing) {
        $signArgs = @('sign', '--ks', $KeystorePath, '--ks-key-alias', $KeyAlias, '--ks-pass', "file:$StorePasswordFile")
        if ($KeyPasswordFile) { $signArgs += @('--key-pass', "file:$KeyPasswordFile") }
        Invoke-Tool (Join-Path $BuildToolsPath 'apksigner.bat') ($signArgs + @('--out', $apk, $aligned))
        Invoke-Tool (Join-Path $BuildToolsPath 'apksigner.bat') @('verify', '--verbose', $apk)
    } else {
        Copy-Item -LiteralPath $aligned -Destination $apk -Force
        Write-Host 'Unsigned build only. Sign the APK before installing it on Android.'
    }
    Invoke-Tool (Join-Path $BuildToolsPath 'zipalign.exe') @('-c', '4', $apk)
    Get-Item -LiteralPath $apk | Select-Object FullName, Length
} finally {
    $hasher.Dispose()
    $env:JAVA_HOME = $oldJavaHome
}
