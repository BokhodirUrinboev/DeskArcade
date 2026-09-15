#!/usr/bin/env bash
# On Ubuntu with the .NET 10 SDK: publish and package in one go.
#   packaging/linux/build.sh            version from DeskArcade.csproj
#   packaging/linux/build.sh 1.2.0      override the version
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/DeskArcade.csproj" | head -n1)}"
PUBLISH="$ROOT/dist-linux/publish"

rm -rf "$PUBLISH"
dotnet publish "$ROOT/DeskArcade.csproj" -c Release -r linux-x64 --self-contained -p:Version="$VERSION" -o "$PUBLISH"
bash "$ROOT/packaging/linux/build-deb.sh" "$PUBLISH" "$VERSION" "$ROOT/dist-linux"
