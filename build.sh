#!/bin/bash
# Build the plugin and package it as a zip for manual installation.
set -euo pipefail

VERSION=${1:-1.0.0}
OUT="dist/jellyfin-plugin-punchplay_${VERSION}.zip"

dotnet publish Jellyfin.Plugin.PunchPlay \
  -c Release \
  -o "dist/publish" \
  /p:Version="$VERSION"

mkdir -p dist
rm -f "$OUT"
(cd dist/publish && zip -j "../../$OUT" Jellyfin.Plugin.PunchPlay.dll)

echo "Built: $OUT"
