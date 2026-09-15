# Releasing Desk Arcade

This page covers how releases are built and published, every file a release ships, Windows code
signing, and the manual steps for winget and Flathub. The workflows are
[`.github/workflows/release.yml`](../.github/workflows/release.yml) and
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## Contents

- [How a release works](#how-a-release-works)
- [Artifacts](#artifacts)
- [Identifiers that must never change](#identifiers-that-must-never-change)
- [Building packages locally](#building-packages-locally)
- [Windows code signing](#windows-code-signing)
- [winget](#winget)
- [Flatpak and Flathub](#flatpak-and-flathub)
- [AppImage](#appimage)
- [macOS](#macos)

## How a release works

1. Bump `<Version>` in `DeskArcade.csproj` (for example `1.2.2` → `1.3.0`).
2. Add a `<release version="1.3.0" date="YYYY-MM-DD"/>` line at the top of `<releases>` in
   `packaging/flatpak/com.imperiumgames.DeskArcade.metainfo.xml`.
3. Merge to `main`, then tag that commit and push the tag:
   `git tag v1.3.0 && git push origin v1.3.0`.

The **Release** workflow then runs:

| Job | Runner | What it does |
|---|---|---|
| Check version | ubuntu-latest | Refuses a tag that is not `vX.Y.Z` or does not match `<Version>` in the csproj |
| Linux amd64 / arm64 | ubuntu-22.04 | Publishes `linux-x64` / `linux-arm64` (arm64 is cross-compiled), builds the `.deb` and the AppImage. amd64: installs the `.deb` and runs both packages with `--signal quit`. arm64: checks the package metadata and that the binaries are aarch64 |
| Windows installers | windows-latest | Imports the signing certificate if the secrets exist, then builds the x64, x64 standalone and ARM64 installers, and verifies the signatures when signing |
| macOS arm64 / x64 | macos-latest / macos-15-intel | Publishes `osx-arm64` / `osx-x64`, builds `DeskArcade.app`, zips it, then unzips the zip and launch-tests the app (see [macOS](#macos)) |
| Publish release | ubuntu-latest | Only for tag pushes, and only if every build succeeded: creates the GitHub Release with a download table and generated notes |

Only the publish job has `contents: write`; everything else is read-only.

**Dry run:** run the Release workflow by hand (Actions → Release → Run workflow) with the version from
the csproj. It builds and uploads every package as workflow artifacts but publishes nothing.

**CI** (`ci.yml`) runs on pull requests, pushes to `main` and on demand. It builds for Windows x64 and
macOS arm64, builds, packages and install-checks the amd64 `.deb` on Ubuntu, and lints the workflows
with actionlint (including shellcheck on `run:` scripts). A new push to a pull request cancels that PR's
older run. When a test project exists, enable the `dotnet test` step marked in the Ubuntu job.

## Artifacts

| File | Platform | Notes |
|---|---|---|
| `DeskArcade-Setup-X.Y.Z.exe` | Windows 10/11 x64 | Per-user Inno Setup installer; needs the .NET 10 Runtime and offers its download page |
| `DeskArcade-Setup-X.Y.Z-standalone.exe` | Windows 10/11 x64 | Same installer with the runtime bundled |
| `DeskArcade-Setup-X.Y.Z-arm64.exe` | Windows 10/11 on ARM | Native ARM64 build, runtime bundled |
| `deskarcade_X.Y.Z_amd64.deb` | Ubuntu 22.04 / 24.04 x64 | `sudo apt install ./deskarcade_X.Y.Z_amd64.deb`; runtime bundled |
| `deskarcade_X.Y.Z_arm64.deb` | Ubuntu 22.04 / 24.04 arm64 | Same package for 64-bit ARM |
| `DeskArcade-X.Y.Z-x86_64.AppImage` | Other Linux distributions, x86_64 | Portable single file: `chmod +x` and run |
| `DeskArcade-X.Y.Z-aarch64.AppImage` | Other Linux distributions, aarch64 | Portable single file |
| `DeskArcade-X.Y.Z-macos-arm64.zip` | macOS 14+ on Apple silicon | `DeskArcade.app`, ad-hoc signed, not notarized |
| `DeskArcade-X.Y.Z-macos-x64.zip` | macOS 14+ on Intel | `DeskArcade.app`, ad-hoc signed, not notarized |

All three Windows installers share one AppId, so any of them upgrades any other in place. The Flatpak
and the winget manifests are not release artifacts; they are built or submitted by hand (below).

## Identifiers that must never change

Upgrades find the previous version through these. Changing one makes the new version install side by
side with the old one.

| Package | Identifier | Where |
|---|---|---|
| Windows installers | AppId `8F3C2A6E-5B7D-4E1A-9C2F-6D4B8A1E7C35` | `MyAppGuid` in `installer/DeskArcade.iss` |
| Debian packages | Package name `deskarcade` | `packaging/linux/control.in` |
| Flatpak | App id `com.imperiumgames.DeskArcade` | `packaging/flatpak/` |
| macOS | Bundle id `com.imperiumgames.deskarcade` | `packaging/macos/build-app.sh` |
| winget | `ImperiumGames.DeskArcade`, product code `{8F3C2A6E-…}_is1` | `packaging/winget/manifests/` |

## Building packages locally

| Package | Command | Output |
|---|---|---|
| Windows installers | `.\build-installer.ps1 [-SelfContained] [-Arch arm64] [-SignCertThumbprint <sha1>]` | `installer\Output\` |
| `.deb` from Windows (WSL) | `.\build-linux.ps1 [-Arch arm64] [-AppImage]` | `dist-linux\` |
| `.deb` on Ubuntu | `packaging/linux/build.sh [version] [amd64\|arm64]` | `dist-linux/` |
| AppImage | `packaging/linux/build-appimage.sh <publish-dir> <version> <x86_64\|aarch64> <out-dir>` | `<out-dir>` |
| macOS app (on a Mac) | `dotnet publish DeskArcade.csproj -c Release -r osx-arm64 --self-contained -o dist/macos/publish-arm64`, then `packaging/macos/build-app.sh dist/macos/publish-arm64 <version> arm64 dist/macos/arm64` | `dist/macos/arm64/` |
| Flatpak | See [Flatpak and Flathub](#flatpak-and-flathub) | |

## Windows code signing

Signing is optional. The Windows job signs only when both repository secrets exist (Settings → Secrets
and variables → Actions):

| Secret | Value |
|---|---|
| `WINDOWS_CERTIFICATE_PFX_BASE64` | The code-signing certificate and private key as a `.pfx`, base64-encoded |
| `WINDOWS_CERTIFICATE_PASSWORD` | The `.pfx` password |

To encode the `.pfx`:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("DeskArcade.pfx")) | Set-Clipboard
```

or `base64 -w0 DeskArcade.pfx` on Linux. Without the secrets the build runs unsigned: the import step
logs that it is skipping signing, and the release notes warn that SmartScreen may ask users to confirm.

**What happens when signing:**

1. `packaging/windows/Import-SigningCertificate.ps1` imports the `.pfx` from memory (it is never written
   to disk) into `Cert:\CurrentUser\My` and outputs its thumbprint.
2. `build.ps1` signs `DeskArcade.exe` with `signtool` (SHA-256, RFC 3161 timestamp from
   `http://timestamp.digicert.com`) straight after `dotnet publish`, before Inno Setup packs it, so the
   installed exe is signed.
3. `build-installer.ps1` compiles the `.iss` with `/DSign` and a `deskarcade` sign tool
   (`/Sdeskarcade=signtool sign /sha1 <thumbprint> …`). Inno Setup's `SignTool` directive signs Setup
   and, through `SignedUninstaller=yes`, the uninstaller.
4. The workflow checks every installer and the exe with `signtool verify /pa`.

**Why Inno Setup's SignTool directive rather than signing Setup after the build:** Inno Setup does not
ship `unins000.exe` as a file. Setup writes it on the user's machine from a copy of its own code, so
signing only the finished `DeskArcade-Setup-*.exe` would leave the uninstaller unsigned, and nothing can
sign it later. With the directive, Inno Setup signs the uninstaller while compiling, embeds it, and
signs Setup last. The app exe is outside Inno Setup's reach as a signing target unless it is signed
before packing, so it is signed right after publishing.

To sign a local build, import the certificate into your user store and run
`.\build-installer.ps1 -SignCertThumbprint <thumbprint>`. A certificate that lives on a hardware token
or in a cloud HSM (for example Azure Trusted Signing) cannot be exported as a `.pfx`; that would need a
different import step, but the signing commands stay the same.

## winget

`packaging/winget/manifests/` holds manifest templates (schema 1.10: version, installer and en-US
default locale) for the per-user Inno Setup installer. winget uses the installers with the runtime
bundled (`-standalone` for x64, `-arm64` for ARM64), so it needs no .NET dependency and a silent install
never stops at the "install .NET first" prompt. Version `0.0.0` and the placeholder digests are filled
in per release.

After the GitHub Release is published:

```powershell
# Downloads the release installers, fills in version, URLs and SHA256, writes dist\winget\1.3.0 and validates it
.\packaging\winget\Update-WingetManifests.ps1 -Version 1.3.0

# Try the stamped manifest on a clean machine or in Windows Sandbox
winget install --manifest dist\winget\1.3.0
```

`-InstallerDir installer\Output` hashes local files instead, but only use it with the exact files
attached to the release: rebuilt installers have different digests.

**Submitting to winget-pkgs** (not done yet):

1. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).
2. Copy the three files from `dist\winget\X.Y.Z` to
   `manifests/i/ImperiumGames/DeskArcade/X.Y.Z/` in the fork.
3. Run `winget validate --manifest manifests/i/ImperiumGames/DeskArcade/X.Y.Z` and open a pull request.
   The pipeline installs the package in a sandbox; the first submission of a new package also gets a
   manual review.

Alternatively, [wingetcreate](https://github.com/microsoft/winget-create) does the fork and pull request
for you: `wingetcreate submit --token <GitHub PAT> dist\winget\X.Y.Z`. Once the package is accepted,
later versions can use `wingetcreate update ImperiumGames.DeskArcade --version X.Y.Z --urls <x64 url>
<arm64 url> --submit`.

## Flatpak and Flathub

`packaging/flatpak/` contains the manifest `com.imperiumgames.DeskArcade.yml`, the AppStream metainfo,
the desktop file and the `deskarcade` launcher. The manifest packages the prebuilt self-contained
`linux-x64` publish output on `org.freedesktop.Platform` 24.08, so no .NET SDK extension is needed.

**Build and install a local bundle** (needs `flatpak-builder` and the 24.08 Platform and Sdk from Flathub):

```bash
dotnet publish DeskArcade.csproj -c Release -r linux-x64 --self-contained -o dist-linux/publish
flatpak-builder --user --install --force-clean --state-dir=dist-linux/.flatpak-builder \
  --repo=dist-linux/flatpak-repo dist-linux/flatpak-build packaging/flatpak/com.imperiumgames.DeskArcade.yml
flatpak build-bundle dist-linux/flatpak-repo dist-linux/DeskArcade.flatpak com.imperiumgames.DeskArcade
flatpak run com.imperiumgames.DeskArcade
```

**Permissions** (`finish-args`) and why:

| Permission | Reason |
|---|---|
| `--socket=x11`, `--share=ipc` | The overlay is an X11 client (XShape input regions, window list, key grabs), also under Wayland through XWayland |
| `--device=dri` | Hardware-accelerated rendering |
| `--socket=pulseaudio` | Sound effects through libpulse-simple (PulseAudio or PipeWire) |
| `--talk-name=org.kde.StatusNotifierWatcher`, `--own-name=org.kde.StatusNotifierItem-2-0` | Tray icon: Avalonia registers `org.kde.StatusNotifierItem-<pid>-0`, and the launcher `exec`s the game so it runs as pid 2 in the sandbox |
| `--filesystem=xdg-config/DeskArcade:create` | Settings, high scores and `crash.log` in `~/.config/DeskArcade`, shared with the `.deb` and AppImage |
| `--filesystem=xdg-config/autostart:create` | "Start when I sign in" writes `~/.config/autostart/deskarcade.desktop` |

The launcher sets `XDG_CONFIG_HOME=~/.config` so the app uses those host folders, and sets `TMPDIR` to
`$XDG_RUNTIME_DIR/app/com.imperiumgames.DeskArcade`, which every instance of the app shares, so the
single-instance lock and `--signal` work across `flatpak run` calls. Inside the sandbox the app writes
its own `/app/...` path into the autostart entry, so the launcher rewrites that entry to
`flatpak run com.imperiumgames.DeskArcade` when it starts and every 5 seconds while the game runs.
Claude Code hooks for the Flatpak must call `flatpak run com.imperiumgames.DeskArcade --signal done`
(and so on), not the path that **Copy Claude Code hook config** puts on the clipboard.

**Submitting to Flathub** (not done yet). The manifest works for local bundles, but Flathub will ask for
changes first:

1. **App id:** Flathub verifies the domain behind the id (`imperiumgames.com`). Without that domain, use
   `io.github.BokhodirUrinboev.DeskArcade`. Renaming changes the Flatpak's upgrade identity, so decide
   before the first submission.
2. **Sources:** Flathub builds offline from downloadable sources, so a local `type: dir` is not accepted.
   Either attach a `linux-x64` publish tarball to each GitHub Release and use a `type: archive` source
   with its URL and `sha256`, or build from source with `org.freedesktop.Sdk.Extension.dotnet10` and a
   NuGet sources file made by `flatpak-dotnet-generator.py` from
   [flatpak-builder-tools](https://github.com/flatpak/flatpak-builder-tools).
3. **Metadata:** add `<screenshots>` and a `<release>` entry per version to the metainfo, and check it
   with `flatpak run --command=flatpak-builder-lint org.flatpak.Builder manifest <manifest>` and
   `appstreamcli validate`.
4. **Permissions review:** reviewers usually reject `xdg-config/autostart` in favour of the Background
   portal (`org.freedesktop.portal.Background`), which needs a code change in
   `src/Platform/Linux/X11Platform.cs`. Be ready to justify X11-only (`--socket=x11`) as well.
5. Fork [flathub/flathub](https://github.com/flathub/flathub), add the manifest on a branch based on
   `new-pr`, and open a pull request against `new-pr`. After acceptance Flathub creates a
   `flathub/<app-id>` repository, and updates go there.

## AppImage

`packaging/linux/build-appimage.sh` builds an AppDir (`AppRun`, desktop file, icon, the publish output
under `usr/lib/deskarcade`) and packs it with appimagetool. The script downloads appimagetool 1.9.1
and the type2-runtime `20251108` build, verifies both against SHA256 digests, and caches them in
`~/.cache/deskarcade-appimage`. To update a pin, change the version and digests at the top of the
script. Without FUSE (containers, CI) it sets `APPIMAGE_EXTRACT_AND_RUN=1`; the release workflow sets
it for the whole Linux job.

**libfuse2 on Ubuntu 22.04 and later:** Ubuntu no longer installs `libfuse2`, and AppImages built with
the old AppImageKit runtime fail there with `dlopen(): error loading libfuse.so.2`. The Desk Arcade
AppImages use the static type2 runtime, which needs no libfuse2, only FUSE itself (`fusermount3`,
present on desktop Ubuntu). If an AppImage still doesn't start:

- run it with `--appimage-extract-and-run` (or `APPIMAGE_EXTRACT_AND_RUN=1`), which needs no FUSE at all;
- or install the FUSE 2 library for other, older AppImages: `sudo apt install libfuse2` on 22.04,
  `sudo apt install libfuse2t64` on 24.04. Don't install the `fuse` package on 22.04 or later: it
  replaces `fuse3` and can remove parts of the desktop.

**Limitations:** the AppImage runs from a temporary mount, so **Start when I sign in** and **Copy Claude
Code hook config** record a `/tmp/.mount_…` path that is gone after the game exits. Use the `.deb` for
those, or edit the `Exec=` line in `~/.config/autostart/deskarcade.desktop` to point at the AppImage.
The AppImage expects the X11, fontconfig and PulseAudio libraries that desktop distributions install.

## macOS

`packaging/macos/build-app.sh` turns an `osx-arm64` or `osx-x64` publish folder into `DeskArcade.app`:
the publish output in `Contents/MacOS`, an `Info.plist` (bundle id `com.imperiumgames.deskarcade`,
`LSUIElement` so there is no Dock icon, minimum macOS 14 like .NET 10), and an `.icns` made from
`assets/DeskArcade.png` with `sips` and `iconutil`. It signs the bundle ad hoc (Apple silicon only runs
signed code), then zips it with `ditto`, which keeps the signatures.

**Launch test:** the release job unzips the zip, verifies the signature, starts the app with
`--profile ci --demo`, fails if it has exited after 15 seconds, then sends `--profile ci --signal quit`
and fails unless it exits cleanly within 20 seconds. The output and any `crash.log` are uploaded when the
test fails. The x64 build runs on the `macos-15-intel` runner; when GitHub retires it, move it to
`macos-latest`, where Rosetta 2 runs the x64 app.

**Gatekeeper:** the app is not signed with a Developer ID or notarized, so macOS blocks the first launch
of the downloaded copy. Tell users to:

1. Unzip the download and move `DeskArcade.app` to Applications.
2. **Right-click (or Control-click) DeskArcade.app → Open**, then click **Open** in the dialog. This is
   needed only once.
3. On macOS 15 Sequoia and later the right-click route no longer offers **Open**. Open the app once,
   dismiss the warning, then go to **System Settings → Privacy & Security** and click **Open Anyway**
   next to the message about DeskArcade.

Advanced users can instead clear the quarantine flag: `xattr -dr com.apple.quarantine /Applications/DeskArcade.app`.

The app has no Dock icon or app menu; it is controlled from its menu bar icon.

To remove the Gatekeeper step later: enroll in the Apple Developer Program, sign with a Developer ID
Application certificate using the hardened runtime and the JIT entitlements .NET needs
(`com.apple.security.cs.allow-jit`, `com.apple.security.cs.allow-unsigned-executable-memory`,
`com.apple.security.cs.disable-library-validation`), notarize with `xcrun notarytool submit --wait`,
and staple the ticket with `xcrun stapler staple`.
