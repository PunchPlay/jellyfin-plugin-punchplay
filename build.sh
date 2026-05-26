#!/bin/bash
# Build the plugin and package it as a zip for manual installation.
set -euo pipefail

PROJECT="Jellyfin.Plugin.PunchPlay/Jellyfin.Plugin.PunchPlay.csproj"
ASSEMBLY_VERSION=${1:-$(sed -n 's:.*<Version>\\(.*\\)</Version>.*:\\1:p' "$PROJECT" | head -n 1)}
PACKAGE_VERSION=${ASSEMBLY_VERSION%.0}
OUT="dist/jellyfin-plugin-punchplay_${PACKAGE_VERSION}.zip"
DOTNET_BIN=${DOTNET_BIN:-$(command -v dotnet || true)}

if [ -z "$DOTNET_BIN" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET_BIN="$HOME/.dotnet/dotnet"
fi

if [ -z "$DOTNET_BIN" ]; then
  echo "dotnet not found on PATH and \$HOME/.dotnet/dotnet is not available" >&2
  exit 1
fi

"$DOTNET_BIN" publish Jellyfin.Plugin.PunchPlay \
  -c Release \
  -o "dist/publish" \
  /p:Version="$ASSEMBLY_VERSION" \
  /p:AssemblyVersion="$ASSEMBLY_VERSION" \
  /p:FileVersion="$ASSEMBLY_VERSION"

mkdir -p dist
rm -f "$OUT"
(cd dist/publish && zip -j "../../$OUT" Jellyfin.Plugin.PunchPlay.dll)

echo "Built: $OUT"
