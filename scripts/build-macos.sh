#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo '请在 Mac 上运行此脚本。' >&2
    exit 1
fi
root="$(cd "$(dirname "$0")/.." && pwd)"
arch="$(uname -m)"
case "$arch" in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "不支持的 Mac 架构 $arch" >&2; exit 1 ;;
esac
if ! xcrun --sdk macosx --find clang >/dev/null 2>&1; then
    echo '请先运行 xcode-select --install，安装命令行开发工具后重试。' >&2
    exit 1
fi

output="$root/artifacts/macos/$rid"
mkdir -p "$output"
stage="$(mktemp -d "$output/build.XXXXXXXX")"
trap 'rm -rf -- "$stage"' EXIT
bundle="$stage/PhoneRemote.app"
contents="$bundle/Contents"
helpers="$contents/Helpers"
resources="$contents/Resources"
mkdir -p "$contents/MacOS" "$helpers" "$resources/Web"
hostSource="${PHONEREMOTE_HOST_DIR:-}"
if [[ -z "$hostSource" && -f "$root/prebuilt/$rid/PhoneRemote.MacHost" ]]; then
    hostSource="$root/prebuilt/$rid"
fi
if [[ -n "$hostSource" ]]; then
    if [[ ! -f "$hostSource/PhoneRemote.MacHost" ]]; then
        echo "缺少适用于 $rid 的被控程序 $hostSource/PhoneRemote.MacHost" >&2
        exit 1
    fi
    ditto "$hostSource" "$helpers"
else
    if ! command -v dotnet >/dev/null; then
        echo "从源码构建被控服务需要 .NET 10 SDK。也可使用带有 prebuilt/$rid 的构建包。" >&2
        exit 1
    fi
    dotnet publish "$root/macos/Host/PhoneRemote.MacHost.csproj" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$helpers"
fi
chmod +x "$helpers/PhoneRemote.MacHost"
lipo "$helpers/PhoneRemote.MacHost" -verify_arch "$arch"

export MACOSX_DEPLOYMENT_TARGET=14.0
xcrun --sdk macosx clang -arch "$arch" -fobjc-arc -fblocks -dynamiclib -O2 -mmacosx-version-min=14.0 -Wl,-headerpad,0x2000 \
    -framework ScreenCaptureKit -framework CoreGraphics -framework ImageIO -framework Foundation \
    -framework ApplicationServices -framework AppKit \
    "$root/macos/Host/QuartzBridge.m" -o "$helpers/libPhoneRemoteQuartz.dylib"
xcrun --sdk macosx clang -arch "$arch" -fobjc-arc -fblocks -O2 -mmacosx-version-min=14.0 \
    -framework AppKit -framework Foundation -framework Security -framework WebKit \
    "$root"/macos/App/*.m -o "$contents/MacOS/PhoneRemote"
cp "$root/macos/App/Info.plist" "$contents/"
cp "$root/macos/App/StartHost.command" "$root/macos/App/bridge.js" "$resources/"
cp "$root/windows/Web/"{index.html,app.js,styles.css} "$resources/Web/"
chmod +x "$resources/StartHost.command"
ditto "$root/third_party/runtime/.NET" "$resources/Notices/dotnet"
ditto "$root/third_party/runtime/ASP.NET Core" "$resources/Notices/aspnet"
plutil -lint "$contents/Info.plist"
codesign --force --sign - --identifier work.codex.phoneremote.mac.host \
    --entitlements "$root/macos/App/Host.entitlements" "$helpers/PhoneRemote.MacHost"
codesign --force --sign - "$helpers/libPhoneRemoteQuartz.dylib"
codesign --force --sign - "$bundle"
codesign --verify --deep --strict "$bundle"

if [[ -e "$output/PhoneRemote.app" ]]; then
    mv "$output/PhoneRemote.app" "$stage/previous.app"
fi
if ! mv "$bundle" "$output/PhoneRemote.app"; then
    if [[ -d "$stage/previous.app" ]]; then mv "$stage/previous.app" "$output/PhoneRemote.app"; fi
    exit 1
fi
echo "构建完成 $output/PhoneRemote.app"
