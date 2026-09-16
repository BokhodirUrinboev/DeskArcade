#!/usr/bin/env bash
# Packages a self-contained Linux publish folder as a portable AppImage.
#   packaging/linux/build-appimage.sh <publish-dir> <version> <arch> <out-dir>
#     arch: x86_64 (publish with -r linux-x64) or aarch64 (publish with -r linux-arm64)
#
# appimagetool and the AppImage runtime are downloaded at pinned versions and checked against
# SHA256 digests (set APPIMAGETOOL to use your own appimagetool instead). x86_64 builds both
# architectures: the runtime, not the tool, decides what the AppImage runs on.
#
# Without FUSE (containers, CI, WSL without libfuse2) appimagetool itself cannot mount, so the
# script sets APPIMAGE_EXTRACT_AND_RUN=1, which makes it unpack to a temp folder and run from there.
set -euo pipefail

PUBLISH_DIR="${1:?publish dir}"
VERSION="${2:?version}"
ARCH="${3:?arch (x86_64 or aarch64)}"
OUT_DIR="${4:?output dir}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

# --- pinned tools ------------------------------------------------------------------------------
APPIMAGETOOL_VERSION=1.9.1
APPIMAGETOOL_SHA256_x86_64=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
APPIMAGETOOL_SHA256_aarch64=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158
RUNTIME_VERSION=20251108
RUNTIME_SHA256_x86_64=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
RUNTIME_SHA256_aarch64=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444

case "$ARCH" in
  x86_64 | amd64) ARCH=x86_64 ELF_MACHINE=62 RID=linux-x64 ;;
  aarch64 | arm64) ARCH=aarch64 ELF_MACHINE=183 RID=linux-arm64 ;;
  *) echo "Unsupported architecture '$ARCH' (use x86_64 or aarch64)"; exit 1 ;;
esac

[ -f "$PUBLISH_DIR/DeskArcade" ] || { echo "$PUBLISH_DIR/DeskArcade missing - publish for $RID first"; exit 1; }
machine="$(od -An -t u2 -j 18 -N 2 "$PUBLISH_DIR/DeskArcade" | tr -d ' ')"
[ "$machine" = "$ELF_MACHINE" ] || {
  echo "$PUBLISH_DIR/DeskArcade is not a $RID binary (ELF machine $machine, expected $ELF_MACHINE)"; exit 1; }

CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/deskarcade-appimage"
mkdir -p "$CACHE"

# fetch <url> <file> <sha256>: download once into the cache, always verify.
fetch() {
  local url="$1" file="$CACHE/$2" sha="$3"
  if [ ! -f "$file" ] || ! echo "$sha  $file" | sha256sum -c --status; then
    echo "Downloading $url" >&2
    if command -v curl >/dev/null; then
      curl -fsSL --retry 3 -o "$file.part" "$url"
    else
      wget -q -O "$file.part" "$url"
    fi
    echo "$sha  $file.part" | sha256sum -c --status || { echo "SHA256 mismatch for $url"; rm -f "$file.part"; exit 1; }
    mv "$file.part" "$file"
  fi
  chmod +x "$file"
  printf '%s\n' "$file"
}

if [ -n "${APPIMAGETOOL:-}" ]; then
  TOOL="$APPIMAGETOOL"
else
  host="$(uname -m)"
  case "$host" in
    x86_64) tool_sha="$APPIMAGETOOL_SHA256_x86_64" ;;
    aarch64) tool_sha="$APPIMAGETOOL_SHA256_aarch64" ;;
    *) echo "No pinned appimagetool for $host; set APPIMAGETOOL"; exit 1 ;;
  esac
  TOOL="$(fetch "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-$host.AppImage" \
    "appimagetool-$APPIMAGETOOL_VERSION-$host.AppImage" "$tool_sha")"
fi
case "$ARCH" in
  x86_64) runtime_sha="$RUNTIME_SHA256_x86_64" ;;
  aarch64) runtime_sha="$RUNTIME_SHA256_aarch64" ;;
esac
RUNTIME="$(fetch "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-$ARCH" \
  "runtime-$RUNTIME_VERSION-$ARCH" "$runtime_sha")"

have_fuse() {
  [ -c /dev/fuse ] || return 1
  command -v fusermount3 >/dev/null || command -v fusermount >/dev/null
}
if [ -z "${APPIMAGE_EXTRACT_AND_RUN:-}" ] && ! have_fuse; then
  echo "FUSE not available: running appimagetool with APPIMAGE_EXTRACT_AND_RUN=1"
  export APPIMAGE_EXTRACT_AND_RUN=1
fi

# --- AppDir ------------------------------------------------------------------------------------
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/DeskArcade.AppDir"
LIB="$APPDIR/usr/lib/deskarcade"

install -d "$LIB" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps" \
  "$APPDIR/usr/share/doc/deskarcade"
cp -r "$PUBLISH_DIR"/. "$LIB/"
rm -f "$LIB"/*.pdb
find "$LIB" -type d -exec chmod 0755 {} +
find "$LIB" -type f -exec chmod 0644 {} +
find "$LIB" -type f -name '*.so' -exec chmod 0755 {} +
chmod 0755 "$LIB/DeskArcade"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
# The AppImage runtime sets APPDIR to the mounted (or extracted) image.
HERE="${APPDIR:-$(dirname "$(readlink -f "$0")")}"
exec "$HERE/usr/lib/deskarcade/DeskArcade" "$@"
EOF
chmod 0755 "$APPDIR/AppRun"

# Same desktop entry as the .deb, stamped with the version appimaged and launchers show.
sed "/^\[Desktop Entry\]$/a X-AppImage-Version=$VERSION" "$HERE/deskarcade.desktop" > "$APPDIR/deskarcade.desktop"
install -m 0644 "$APPDIR/deskarcade.desktop" "$APPDIR/usr/share/applications/deskarcade.desktop"
install -m 0644 "$ROOT/assets/DeskArcade.png" "$APPDIR/deskarcade.png"
install -m 0644 "$ROOT/assets/DeskArcade.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/deskarcade.png"
ln -s deskarcade.png "$APPDIR/.DirIcon"
install -m 0644 "$ROOT/LICENSE" "$APPDIR/usr/share/doc/deskarcade/LICENSE"
install -m 0644 "$ROOT/THIRD-PARTY-NOTICES.md" "$APPDIR/usr/share/doc/deskarcade/THIRD-PARTY-NOTICES.md"

mkdir -p "$OUT_DIR"
OUT="$(cd "$OUT_DIR" && pwd)/DeskArcade-$VERSION-$ARCH.AppImage"
rm -f "$OUT"
# --no-appstream: the AppStream metadata lives in the Flatpak package (different app id).
ARCH="$ARCH" "$TOOL" --no-appstream --runtime-file "$RUNTIME" "$APPDIR" "$OUT"
chmod 0755 "$OUT"
echo "AppImage: $OUT"
