#!/usr/bin/env bash
# Build PlanetbaseCoop.dll in the .NET 8 SDK container.
# usage: ./build.sh <game dir> <BepInEx dir (the folder that contains BepInEx/core)>
# Prints BUILD_OK <path> or BUILD_FAILED. Game files are mounted read-only and never copied.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME="$(cd "${1:?game dir}" && pwd)"
BEPINEX="$(cd "${2:?bepinex dir}" && pwd)"
CACHE="${COOP_BUILD_CACHE:-$HERE/.cache}"
mkdir -p "$CACHE/home" "$HERE/out"
docker run --rm \
  -e HOME=/home/dev -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "$HERE:/src" \
  -v "$GAME:/game:ro" \
  -v "$BEPINEX:/bepinex:ro" \
  -v "$CACHE/home:/home/dev" \
  mcr.microsoft.com/dotnet/sdk:8.0 bash -c "
    cd /src/src && dotnet build -c Release -nologo -v q -o /src/out \
      -p:GameManaged=/game/Planetbase_Data/Managed -p:BepInExCore=/bepinex/BepInEx/core 2>&1 | grep -E 'error|warn|Warn|Error' | grep -v 'warning MSB3277' | head -40
    rc=\${PIPESTATUS[0]}
    chown -R $(id -u):$(id -g) /src/out /src/src/obj /home/dev 2>/dev/null
    exit \$rc
  "
rc=$?
if [ $rc -eq 0 ] && [ -f "$HERE/out/PlanetbaseCoop.dll" ]; then
  echo "BUILD_OK $HERE/out/PlanetbaseCoop.dll"
else
  echo "BUILD_FAILED (exit $rc)"
  exit 1
fi
