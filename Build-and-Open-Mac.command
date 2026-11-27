#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
/bin/bash "$root/scripts/build-macos.sh"
case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) exit 1 ;;
esac
open "$root/artifacts/macos/$rid/PhoneRemote.app"
