#!/usr/bin/env bash
# Packages a self-contained Linux publish folder as an upgradable Debian/Ubuntu package.
#   packaging/linux/build-deb.sh <publish-dir> <version> [output-dir] [arch]
#     arch: amd64 (default, publish with -r linux-x64) or arm64 (publish with -r linux-arm64)
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
ARCH="${4:-amd64}"

# ELF e_machine of the executable the publish folder must contain for this Debian architecture.
case "$ARCH" in
  amd64) ELF_MACHINE=62 RID=linux-x64 ;;
  arm64) ELF_MACHINE=183 RID=linux-arm64 ;;
  *) echo "Unsupported architecture '$ARCH' (use amd64 or arm64)"; exit 1 ;;
esac

command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found (run this on Ubuntu/Debian or in WSL)"; exit 1; }
[ -f "$PUBLISH_DIR/DeskArcade" ] || { echo "$PUBLISH_DIR/DeskArcade missing - publish for $RID first"; exit 1; }

# Catch a publish folder built for the wrong architecture before it ships under the wrong label.
machine="$(od -An -t u2 -j 18 -N 2 "$PUBLISH_DIR/DeskArcade" | tr -d ' ')"
[ "$machine" = "$ELF_MACHINE" ] || {
  echo "$PUBLISH_DIR/DeskArcade is not a $RID binary (ELF machine $machine, expected $ELF_MACHINE)"; exit 1; }

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
install -m 0644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/deskarcade/copyright"
install -m 0644 "$ROOT/THIRD-PARTY-NOTICES.md" "$STAGE/usr/share/doc/deskarcade/THIRD-PARTY-NOTICES.md"

SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE@/$SIZE_KB/" -e "s/@ARCH@/$ARCH/" "$HERE/control.in" > "$STAGE/DEBIAN/control"
install -m 0755 "$HERE/postinst" "$STAGE/DEBIAN/postinst"
install -m 0755 "$HERE/prerm" "$STAGE/DEBIAN/prerm"

mkdir -p "$OUT_DIR"
DEB="$OUT_DIR/deskarcade_${VERSION}_${ARCH}.deb"
dpkg-deb --build --root-owner-group "$STAGE" "$DEB"
echo "Package: $DEB"
