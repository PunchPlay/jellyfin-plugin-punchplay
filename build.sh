#!/bin/bash
# Build the plugin and package it as a zip for manual installation.
set -euo pipefail

PROJECT="Jellyfin.Plugin.PunchPlay/Jellyfin.Plugin.PunchPlay.csproj"
ASSEMBLY_VERSION=${1:-$(sed -n 's:.*<Version>\\(.*\\)</Version>.*:\\1:p' "$PROJECT" | head -n 1)}
PACKAGE_VERSION=${ASSEMBLY_VERSION%.0}
OUT="dist/jellyfin-plugin-punchplay_${PACKAGE_VERSION}.zip"

dotnet publish Jellyfin.Plugin.PunchPlay \
  -c Release \
  -o "dist/publish" \
  /p:Version="$ASSEMBLY_VERSION" \
  /p:AssemblyVersion="$ASSEMBLY_VERSION" \
  /p:FileVersion="$ASSEMBLY_VERSION"

mkdir -p dist
rm -f "$OUT"
(cd dist/publish && zip -j "../../$OUT" Jellyfin.Plugin.PunchPlay.dll)

echo "Built: $OUT"
