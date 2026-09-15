#!/usr/bin/env bash
# Wraps a self-contained macOS publish folder into DeskArcade.app and zips it with ditto.
#   packaging/macos/build-app.sh <publish-dir> <version> <arch> <out-dir>
#     arch: arm64 (publish with -r osx-arm64) or x64 (publish with -r osx-x64)
# Output: <out-dir>/DeskArcade.app and <out-dir>/DeskArcade-<version>-macos-<arch>.zip
#
# Runs on macOS only (sips, iconutil, codesign, ditto). The app has an ad-hoc signature, not a
# Developer ID one, and is not notarized: Gatekeeper asks users to confirm the first launch
# (see docs/RELEASING.md).
set -euo pipefail

PUBLISH_DIR="${1:?publish dir}"
VERSION="${2:?version}"
ARCH="${3:?arch (arm64 or x64)}"
OUT_DIR="${4:?output dir}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

case "$ARCH" in
  arm64) MACHO_ARCH=arm64 RID=osx-arm64 ;;
  x64) MACHO_ARCH=x86_64 RID=osx-x64 ;;
  *) echo "Unsupported architecture '$ARCH' (use arm64 or x64)"; exit 1 ;;
esac

for tool in sips iconutil codesign ditto lipo plutil; do
  command -v "$tool" >/dev/null || { echo "$tool not found (run this on macOS)"; exit 1; }
done
[ -f "$PUBLISH_DIR/DeskArcade" ] || { echo "$PUBLISH_DIR/DeskArcade missing - publish for $RID first"; exit 1; }
archs="$(lipo -archs "$PUBLISH_DIR/DeskArcade")"
[ "$archs" = "$MACHO_ARCH" ] || { echo "$PUBLISH_DIR/DeskArcade is $archs, expected $MACHO_ARCH ($RID)"; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
APP="$OUT_DIR/DeskArcade.app"
ZIP="$OUT_DIR/DeskArcade-$VERSION-macos-$ARCH.zip"
rm -rf "$APP" "$ZIP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# The .NET app host looks for DeskArcade.dll and the runtime next to itself, so the whole publish
# folder goes into Contents/MacOS.
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
rm -f "$APP"/Contents/MacOS/*.pdb
chmod 0755 "$APP/Contents/MacOS/DeskArcade"

# Icon: assets/DeskArcade.png is 256x256, so the iconset stops there instead of upscaling.
ICONSET="$WORK/DeskArcade.iconset"
mkdir -p "$ICONSET"
icon() { sips -z "$1" "$1" "$ROOT/assets/DeskArcade.png" --out "$ICONSET/$2" >/dev/null; }
icon 16 icon_16x16.png
icon 32 icon_16x16@2x.png
icon 32 icon_32x32.png
icon 64 icon_32x32@2x.png
icon 128 icon_128x128.png
icon 256 icon_128x128@2x.png
icon 256 icon_256x256.png
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DeskArcade.icns"

# LSUIElement: an agent app without a Dock icon or app menu; the tray (menu bar) icon is the UI.
# LSMinimumSystemVersion: the oldest macOS that .NET 10 supports.
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>Desk Arcade</string>
  <key>CFBundleExecutable</key>
  <string>DeskArcade</string>
  <key>CFBundleIconFile</key>
  <string>DeskArcade</string>
  <key>CFBundleIdentifier</key>
  <string>com.imperiumgames.deskarcade</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Desk Arcade</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.arcade-games</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSHumanReadableCopyright</key>
  <string>© 2026 Imperium Games</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist"

# Ad-hoc signature over the whole bundle. Apple silicon only runs signed code, and a bundle whose
# executable is signed but whose contents are not sealed is reported as "damaged" rather than as
# coming from an unidentified developer. Every file in Contents/MacOS counts as code, so each one
# is signed before the bundle itself.
xattr -cr "$APP"
find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/DeskArcade" -exec codesign --force --sign - --timestamp=none {} +
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# ditto keeps the signatures (stored in extended attributes for non-Mach-O files) and permissions.
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
echo "App: $APP"
echo "Zip: $ZIP"
