#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props" | head -1)}"
MSI="${2:-$ROOT/artifacts/OpControlParental-$VERSION.msi}"
OUT="$ROOT/artifacts"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
if [ ! -f "$MSI" ]; then echo "MSI not found: $MSI" >&2; exit 1; fi
rm -rf "$OUT/setup-$VERSION"
dotnet publish "$ROOT/src/OpControl.Parental.Setup" -c Release -r win-x64 -o "$OUT/setup-$VERSION" \
  -p:Version="$VERSION" -p:MsiPath="$MSI"
cp "$OUT/setup-$VERSION/OpControl.Parental.Setup.exe" "$OUT/OpControlParental-Setup-$VERSION.exe"
rm -rf "$OUT/setup-$VERSION"
ls -lh "$OUT/OpControlParental-Setup-$VERSION.exe"
