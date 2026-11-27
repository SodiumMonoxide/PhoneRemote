#!/bin/bash
set -euo pipefail
resources="$(cd "$(dirname "$0")" && pwd)"
exec "$resources/../Helpers/PhoneRemote.MacHost"
