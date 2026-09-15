#!/usr/bin/env bash
# Packages a linux-x64 publish folder as an upgradable Debian/Ubuntu package.
#   packaging/linux/build-deb.sh <publish-dir> <version> [output-dir]
#
# Upgrades: the package name (deskarcade) never changes, so installing a newer .deb
# ("sudo apt install ./deskarcade_1.2.0_amd64.deb") replaces the old version in place.
# Settings and high scores live in ~/.config/DeskArcade and are never touched.
set -euo pipefail

PUBLISH_DIR="${1:?publish dir}"
VERSION="${2:?version}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT_DIR="${3:-$ROOT/dist-linux}"

command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found (run this on Ubuntu/Debian or in WSL)"; exit 1; }
[ -f "$PUBLISH_DIR/DeskArcade" ] || { echo "$PUBLISH_DIR/DeskArcade missing - publish for linux-x64 first"; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

install -d "$STAGE/DEBIAN" "$STAGE/opt/deskarcade" "$STAGE/usr/bin" \
  "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/doc/deskarcade"

cp -r "$PUBLISH_DIR"/. "$STAGE/opt/deskarcade/"
rm -f "$STAGE"/opt/deskarcade/*.pdb
find "$STAGE/opt/deskarcade" -type d -exec chmod 0755 {} +
find "$STAGE/opt/deskarcade" -type f -exec chmod 0644 {} +
find "$STAGE/opt/deskarcade" -type f -name '*.so' -exec chmod 0755 {} +
chmod 0755 "$STAGE/opt/deskarcade/DeskArcade"
ln -s /opt/deskarcade/DeskArcade "$STAGE/usr/bin/deskarcade"

install -m 0644 "$HERE/deskarcade.desktop" "$STAGE/usr/share/applications/deskarcade.desktop"
install -m 0644 "$ROOT/assets/DeskArcade.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/deskarcade.png"
install -m 0644 "$ROOT/README.md" "$STAGE/usr/share/doc/deskarcade/README.md"

SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE@/$SIZE_KB/" "$HERE/control.in" > "$STAGE/DEBIAN/control"
install -m 0755 "$HERE/postinst" "$STAGE/DEBIAN/postinst"
install -m 0755 "$HERE/prerm" "$STAGE/DEBIAN/prerm"

mkdir -p "$OUT_DIR"
DEB="$OUT_DIR/deskarcade_${VERSION}_amd64.deb"
dpkg-deb --build --root-owner-group "$STAGE" "$DEB"
echo "Package: $DEB"
