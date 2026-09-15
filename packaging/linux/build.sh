#!/usr/bin/env bash
# On Ubuntu with the .NET 10 SDK: publish and package in one go.
#   packaging/linux/build.sh                  version from DeskArcade.csproj, amd64
#   packaging/linux/build.sh 1.2.0            override the version
#   packaging/linux/build.sh 1.2.0 arm64      cross-compile and package for arm64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/DeskArcade.csproj" | head -n1)}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 PUBLISH="$ROOT/dist-linux/publish" ;;
  arm64) RID=linux-arm64 PUBLISH="$ROOT/dist-linux/publish-arm64" ;;
  *) echo "Unsupported architecture '$ARCH' (use amd64 or arm64)"; exit 1 ;;
esac

rm -rf "$PUBLISH"
dotnet publish "$ROOT/DeskArcade.csproj" -c Release -r "$RID" --self-contained -p:Version="$VERSION" -o "$PUBLISH"
bash "$ROOT/packaging/linux/build-deb.sh" "$PUBLISH" "$VERSION" "$ROOT/dist-linux" "$ARCH"
