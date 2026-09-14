#!/usr/bin/env bash
# Package a mod folder into a Thunderstore zip + a drop-in DLL.
#   tools/package_mod.sh <ModDir> <ModName> <version>
# Produces release/FlorpyDorp-<ModName>-<version>/ , release/FlorpyDorp-<ModName>-<version>.zip and dist/<ModName>.dll
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MOD_DIR="$ROOT/$1"; NAME="$2"; VER="$3"
DLL="$MOD_DIR/bin/Release/netstandard2.1/$NAME.dll"
[ -f "$DLL" ] || { echo "missing $DLL (build first)"; exit 1; }
OUT="$ROOT/release/FlorpyDorp-$NAME-$VER"
rm -rf "$OUT"; mkdir -p "$OUT" "$ROOT/dist"
cp "$DLL" "$MOD_DIR/manifest.json" "$MOD_DIR/README.md" "$MOD_DIR/CHANGELOG.md" "$MOD_DIR/LICENSE" "$MOD_DIR/icon.png" "$OUT/"
( cd "$OUT" && rm -f "../FlorpyDorp-$NAME-$VER.zip" && powershell -NoProfile -Command "Compress-Archive -Path * -DestinationPath '../FlorpyDorp-$NAME-$VER.zip' -Force" )
cp "$DLL" "$ROOT/dist/$NAME.dll"
echo "packaged: release/FlorpyDorp-$NAME-$VER.zip  and  dist/$NAME.dll"
