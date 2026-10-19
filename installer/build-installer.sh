#!/usr/bin/env bash
# Build the Windows client MSI and the interactive setup bootstrapper on Linux.
# Requirements: .NET 10 SDK, WiX 5 as a global dotnet tool (`dotnet tool install -g wix`).
# Usage: installer/build-installer.sh [version]
#   version defaults to the Version in Directory.Build.props (0.5.0).
# Optional signing: run installer/sign.ps1 on Windows after this script (see README).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props" | head -1)}"
OUT="$ROOT/artifacts"
PUB="$OUT/publish-$VERSION"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

mkdir -p "$OUT" "$PUB/client" "$PUB/helper"

echo "Building Parental OpControl $VERSION"
dotnet publish "$ROOT/src/OpControl.Parental.Client.Windows" -c Release -r win-x64 -o "$PUB/client" -p:Version="$VERSION"
dotnet publish "$ROOT/src/OpControl.Parental.Client.Windows.Helper" -c Release -r win-x64 -o "$PUB/helper" -p:Version="$VERSION"

cat <<EOF

Published payloads to $PUB/client and $PUB/helper.

WiX 5 only runs on Windows. Build the MSI and setup on a Windows build host:

  powershell -ExecutionPolicy Bypass -File installer/build-msi-windows.ps1 \`
    -Version $VERSION -PublishRoot <published-publish-dir> \`
    -OutFile artifacts/OpControlParental-$VERSION.msi -InstallerDir installer

Then, back on any OS with the .NET SDK:

  ./installer/build-setup.sh $VERSION artifacts/OpControlParental-$VERSION.msi
EOF
