#!/usr/bin/env bash
# Maintainers: publish the built mod for the installers.
#   ./release.sh            copies out/PlanetbaseCoop.dll to release/<version>/ and rewrites
#                           release/latest.txt (version, SHA-256, installer checksums)
# Then commit and push: the installers read release/latest.txt from the main branch.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE"
VERSION=$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' src/Plugin.cs)
DLL=out/PlanetbaseCoop.dll
[ -f "$DLL" ] || { echo "build first: ./build.sh ..."; exit 1; }
[ -n "$VERSION" ] || { echo "no version in src/Plugin.cs"; exit 1; }
GAME_SHA=245441e4892d10551823334b7171ec712e88e7218913c20d8b44154ca98af045   # Planetbase 1.4.2, Steam build 20301361
INSTALLER_VERSION=$(sed -n 's/^INSTALLER_VERSION=\([0-9]*\).*/\1/p' install/planetbase-coop.sh)

mkdir -p "release/$VERSION"
if [ -f "release/$VERSION/PlanetbaseCoop.dll" ] && ! cmp -s "$DLL" "release/$VERSION/PlanetbaseCoop.dll"; then
  echo "release/$VERSION already holds a different build: bump the version in src/Plugin.cs first."
  exit 1
fi
cp -f "$DLL" "release/$VERSION/PlanetbaseCoop.dll"
sha() { sha256sum "$1" | cut -d' ' -f1; }
{
  echo "# Planetbase Co-op: latest release, read by install/planetbase-coop.sh and install/Planetbase-Coop.bat"
  echo "version=$VERSION"
  echo "dll=release/$VERSION/PlanetbaseCoop.dll"
  echo "sha256=$(sha "release/$VERSION/PlanetbaseCoop.dll")"
  echo "game_version=1.4.2"
  echo "game_sha256=$GAME_SHA"
  echo "installer_version=$INSTALLER_VERSION"
  echo "installer_sh=install/planetbase-coop.sh"
  echo "installer_sh_sha256=$(sha install/planetbase-coop.sh)"
  if [ -f install/Planetbase-Coop.bat ]; then
    echo "installer_bat=install/Planetbase-Coop.bat"
    echo "installer_bat_sha256=$(sha install/Planetbase-Coop.bat)"
  fi
} > release/latest.txt
cat release/latest.txt
