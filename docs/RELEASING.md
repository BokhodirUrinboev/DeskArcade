# Releasing Desk Arcade

This page covers how releases are built and published, every file a release ships, Windows code
signing, publishing to the package managers, and the manual steps for Flathub. The workflows are
[`.github/workflows/release.yml`](../.github/workflows/release.yml),
[`.github/workflows/packages.yml`](../.github/workflows/packages.yml) and
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## Contents

- [How a release works](#how-a-release-works)
- [Artifacts](#artifacts)
- [Identifiers that must never change](#identifiers-that-must-never-change)
- [Building packages locally](#building-packages-locally)
- [Windows code signing](#windows-code-signing)
- [Package managers](#package-managers)
- [winget](#winget)
- [Homebrew and Scoop](#homebrew-and-scoop)
- [Chocolatey](#chocolatey)
- [AUR](#aur)
- [The web page](#the-web-page)
- [The VS Code extension](#the-vs-code-extension)
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
| Windows installers | windows-latest | Publishes the x64, x64 standalone and ARM64 exes and builds their installers. On release tags with SignPath set up, it has SignPath sign the exes before packing and the installers afterwards, then verifies the signatures (see [Windows code signing](#windows-code-signing)) |
| macOS arm64 / x64 | macos-latest / macos-15-intel | Publishes `osx-arm64` / `osx-x64`, builds `DeskArcade.app`, zips it, then unzips the zip and launch-tests the app (see [macOS](#macos)) |
| Publish release | ubuntu-latest | Only for tag pushes, and only if every build succeeded: creates the GitHub Release with a download table and generated notes |
| Package managers | ubuntu-24.04 / windows-latest | `packages.yml`, after the release: pushes the new cask and Scoop manifest to the tap and the bucket, and submits the winget manifests once winget-pkgs has the package (see [Package managers](#package-managers)) |

Only the publish job has `contents: write`; everything else is read-only. The Windows job also has `actions: read`, so SignPath can download the artifacts it signs.
The package-manager jobs write to other repositories with tokens of their own (see [Package managers](#package-managers)).

**Dry run:** run the Release workflow by hand (Actions → Release → Run workflow) with the version from
the csproj. It builds and uploads every package as workflow artifacts but publishes nothing.

**CI** (`ci.yml`) runs on pull requests, pushes to `main` and on demand. It builds for Windows x64 and
macOS arm64, builds, packages and install-checks the amd64 `.deb` on Ubuntu, and lints the workflows
with actionlint (including shellcheck on `run:` scripts). The Windows and Ubuntu jobs also run the unit tests, and the
**smoke** job starts the published app on `windows-11-arm`, `ubuntu-24.04-arm` (under Xvfb) and
`macos-14` runners, opens the stats window and quits it through `--signal` (`tests/smoke.sh`). A new push
to a pull request cancels that PR's older run.

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
and the package-manager manifests are not release artifacts: `packages.yml` publishes the manifests, and
the Flatpak is built by hand (below).

**In-app updates depend on these names.** The game picks its download by name (`UpdateChecker.AssetName`):
the Windows setups, `deskarcade_X.Y.Z_amd64.deb` / `_arm64.deb` for a `.deb` install and
`DeskArcade-X.Y.Z-x86_64.AppImage` / `-aarch64.AppImage` for an AppImage, and checks the file against
the SHA-256 digest GitHub records for the asset. Rename an asset and the updater falls back to the
release page; upload one by hand without a digest and it still installs, but tells the player the file
was not verified. `tests/DeskArcade.Tests/LinuxUpdateTests.cs` checks the names against `release.yml`.
On Linux the `.deb` goes in through `pkexec` (PolicyKit's password prompt) and the AppImage replaces its
own file; the package's `prerm` closes the running game, so a wrapper shell started by the game does the
relaunch. Flatpak copies are told to use `flatpak update`.

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

Windows releases are signed through [SignPath.io](https://signpath.io) with a certificate from the
[SignPath Foundation](https://signpath.org), which signs open-source projects for free. The signature
names **SignPath Foundation** as the publisher. The private key never leaves SignPath, so there is no
`.pfx` to store in GitHub: the workflow uploads the unsigned files, SignPath signs them once a request
is approved, and the workflow downloads the signed files.

### One-time setup

1. **Apply** at [signpath.org/apply](https://signpath.org/apply) (the Foundation, `.org`) with the
   GitHub repository. Don't sign up or start a trial on signpath.io (`.io`): that is the paid
   commercial product. Once the Foundation approves the project, it sets up a free open-source
   subscription on signpath.io for you, with no trial and no payment details. The
   Foundation needs a public OSI-licensed repository (MIT here), release builds from GitHub Actions, and
   the [code signing policy](../README.md#code-signing-policy) in the README.
2. **After approval**, in the SignPath web app:
   - Install the SignPath GitHub App on the repository and add GitHub as a trusted build system for the
     project, so SignPath signs only artifacts built by this repository's workflows.
   - Create the project (slug `DeskArcade`) and two artifact configurations, pasting in the files from
     [`.signpath/artifact-configurations/`](../.signpath/artifact-configurations/): slug `app`
     (`app.xml`) and slug `installers` (`installers.xml`).
   - The signing policy the Foundation sets up is normally `release-signing`, with you as approver.
   - Create a CI user, add it as a submitter on the signing policy, and copy its API token.
3. **In GitHub** (Settings → Secrets and variables → Actions):

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | The CI user's API token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | The organization ID from SignPath |
| Variable | `SIGNPATH_PROJECT_SLUG` | Optional; defaults to `DeskArcade` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Optional; defaults to `release-signing` |

Until the secret and the organization variable exist, releases are built unsigned and the release notes
say that SmartScreen may ask users to confirm. Manual `workflow_dispatch` builds are never signed.

### What happens on a release tag

1. `build.ps1` publishes the three exes into `dist\x64`, `dist\x64-standalone` and `dist\arm64`.
2. **Round 1:** they are uploaded as the `unsigned-app` artifact and submitted to SignPath with the
   `app` configuration. The job waits (up to an hour) until you approve the request in SignPath, then
   copies the signed exes back over the unsigned ones.
3. `build-installer.ps1 -SkipPublish -SourceDir …` packs the signed exes into the three installers.
4. **Round 2:** the installers go to SignPath the same way (`unsigned-installers`, configuration
   `installers`).
5. The job checks every exe and installer with `signtool verify /pa`, and the release job publishes only
   the signed `windows` artifact. The `unsigned-*` artifacts expire after a day.

So each release asks for **two approvals** in SignPath (you also get an email for each).

**The uninstaller is not signed.** Inno Setup doesn't ship `unins000.exe` as a file: Setup writes it on
the user's machine from its own code, and can sign it only while compiling, through its `SignTool`
directive, which needs a local certificate. SignPath signs remotely, so it can't be plugged in there.
Windows doesn't check the uninstaller with SmartScreen (it is never downloaded), so in practice this
doesn't show up for users.

### Signing a local build

With your own certificate in `Cert:\CurrentUser\My`, run
`.\build-installer.ps1 -SignCertThumbprint <thumbprint>`: `build.ps1` signs the exe after publishing and
Inno Setup's `SignTool` directive signs Setup and the uninstaller.

## Package managers

After `release.yml` publishes a release, it runs `packages.yml`:

- **Homebrew and Scoop** (ubuntu-24.04): `packaging/Update-PackageManifests.ps1` downloads the release's
  macOS zips and Windows installers, fills their version and SHA256 into the cask and the Scoop manifest,
  and the job pushes them to the tap and the bucket as "Desk Arcade X.Y.Z". When they already have that
  version, nothing is pushed, but `git push --dry-run` still checks that the token can push.
- **winget** (windows-latest): `packaging/winget/Update-WingetManifests.ps1` stamps the manifests and a
  pinned [wingetcreate](https://github.com/microsoft/winget-create) submits them, opening a pull request
  on microsoft/winget-pkgs from the fork `BokhodirUrinboev/winget-pkgs`. The job skips when winget-pkgs
  doesn't have the package yet, already has the version, or already has an open pull request for it.
- **Chocolatey** (windows-latest): `packaging/chocolatey/Update-ChocolateyPackage.ps1` stamps the version, the
  Setup URLs and their SHA256 into a copy of the package and packs it; the job installs and uninstalls it on the
  runner and pushes it to the community repository, where it waits for moderation.
- **AUR** (ubuntu-24.04): `packaging/aur/Update-AurPackage.ps1` stamps `pkgver` and the .deb digests into the
  PKGBUILD and .SRCINFO; the job builds and installs the package in an Arch Linux container and pushes both files
  to `ssh://aur@aur.archlinux.org/deskarcade-bin.git`.

To publish an existing release again (after a failed push, a renewed token, or once winget accepts the
package), run **Actions → Package managers → Run workflow** with its version.

The jobs write with two repository secrets (Settings → Secrets and variables → Actions). A job whose
secret is missing skips with a warning; an expired token fails it. Give each token an expiry (a year is
fine; GitHub emails before it runs out) and renew it by creating a new one the same way and replacing the
secret.

| Secret | Token | Access |
|---|---|---|
| `PACKAGING_TOKEN` | [Fine-grained](https://github.com/settings/personal-access-tokens/new) | Only the repositories `homebrew-tap` and `scoop-bucket`, with Contents: Read and write |
| `WINGET_TOKEN` | [Classic](https://github.com/settings/tokens/new) | The `public_repo` scope only; wingetcreate doesn't support fine-grained tokens |
| `CHOCOLATEY_API_KEY` | The API key of a free [community.chocolatey.org](https://community.chocolatey.org/account/Register) account | Pushing the packages that account owns |
| `AUR_SSH_KEY` | A private SSH key whose public half is on a free [AUR account](https://aur.archlinux.org/register) | Pushing `deskarcade-bin` |

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

**Submitting to winget-pkgs:** the first submission,
[microsoft/winget-pkgs#437055](https://github.com/microsoft/winget-pkgs/pull/437055) (1.5.0), passed
validation and waits for the Microsoft CLA to be signed (comment `@microsoft-github-policy-service agree`
on the pull request) and for a moderator. Until it is merged, `packages.yml` skips winget. After that,
every release is submitted for you; to bring winget up to date with the releases made in the meantime,
run the Package managers workflow by hand with the latest version.

To submit by hand instead, `wingetcreate submit dist\winget\X.Y.Z` signs in to GitHub in the browser
and opens the pull request from your fork. The pipeline installs the package in a sandbox before a
moderator merges it.

## Homebrew and Scoop

`packaging/homebrew/deskarcade.rb` (a cask for the macOS zips) and `packaging/scoop/deskarcade.json` (the
Inno Setup installers, which Scoop unpacks without running them) are the templates that `packages.yml`
stamps and publishes after each release (see [Package managers](#package-managers)). Their version and
digests are from whichever release was last stamped into them; the script replaces both, so the
templates don't need updating after a release. To stamp them by hand:

```powershell
# Downloads the macOS zips and Windows installers, fills in version, URLs and SHA256,
# and writes dist\homebrew\deskarcade.rb and dist\scoop\deskarcade.json
.\packaging\Update-PackageManifests.ps1 -Version 1.4.0
```

**Published** since 1.7.0:

- **Homebrew:** the tap [`BokhodirUrinboev/homebrew-tap`](https://github.com/BokhodirUrinboev/homebrew-tap)
  holds the cask in `Casks/deskarcade.rb`; users run `brew install --cask bokhodirurinboev/tap/deskarcade`.
  homebrew/cask itself wants a notarized app, which the ad-hoc signed build isn't yet.
- **Scoop:** the bucket [`BokhodirUrinboev/scoop-bucket`](https://github.com/BokhodirUrinboev/scoop-bucket)
  holds the JSON in `bucket/`; users run
  `scoop bucket add deskarcade https://github.com/BokhodirUrinboev/scoop-bucket` and
  `scoop install deskarcade/deskarcade`. `checkver` and `autoupdate` let Scoop's tooling follow new releases.

## Chocolatey

`packaging/chocolatey/` holds the package: the nuspec and the install and uninstall scripts, which run the
release's per-user Setup silently (`-standalone` on x64, `-arm64` on Windows on ARM) and its uninstaller. Nothing is
embedded, so it needs no VERIFICATION.txt. The template keeps the release last stamped into it. To try a release's
package by hand:

```powershell
.\packaging\chocolatey\Update-ChocolateyPackage.ps1 -Version 1.8.6    # stamps and packs dist\chocolatey
choco install deskarcade --source dist\chocolatey -y                     # best in Windows Sandbox
```

**One-time setup:** register at community.chocolatey.org and put the API key from the account page in the
`CHOCOLATEY_API_KEY` secret; the next release publishes by itself. The first version goes through human
moderation, which can take a few days. Users then run `choco install deskarcade`.

## AUR

`packaging/aur/` holds `PKGBUILD` and `.SRCINFO` for `deskarcade-bin`, which unpacks the release's .deb (the
self-contained build in `/opt/deskarcade`, the launcher, the desktop entry and the icon). Checked with 1.8.5 in an
`archlinux:latest` container: `makepkg` built it and `pacman -U` installed it.

**One-time setup:** create an AUR account, add the public half of a new SSH key to it (My Account → SSH Public
Key), put the private half in the `AUR_SSH_KEY` secret, and push the package once by hand so the name is yours:

```bash
git clone ssh://aur@aur.archlinux.org/deskarcade-bin.git && cd deskarcade-bin
cp ../DeskArcade/packaging/aur/PKGBUILD ../DeskArcade/packaging/aur/.SRCINFO .
git add PKGBUILD .SRCINFO && git commit -m "deskarcade-bin 1.8.6" && git push
```

Users then install it with an AUR helper (`yay -S deskarcade-bin`) or with `makepkg -si`.

## The web page

`site/` is the web page: plain HTML and CSS, with the GIFs from `docs/media/`.
[`.github/workflows/pages.yml`](../.github/workflows/pages.yml) publishes it to GitHub Pages on every push to `main`
that touches `site/` or `docs/media/`, and by hand. **One-time setup:** Settings → Pages → Build and deployment →
Source: GitHub Actions. It is then at https://bokhodirurinboev.github.io/DeskArcade/.

The GIFs come from `tools/record-gifs.ps1`, which runs each game's demo with `--record` (numbered frames of the
overlay, over stand-in windows) and turns the frames into looping GIFs with ffmpeg; `tools/record-gifs.csv` lists the
games, how long to record and how to crop.

## The VS Code extension

`integrations/vscode/` is the extension (`ImperiumGames.desk-arcade`). The release workflow's `vscode` job runs its unit
tests, sets its version to the release's (`npm version`) and packages
`desk-arcade-<version>.vsix`, which goes into the GitHub Release with the other files. After the release, the
`vscode-publish` job publishes that `.vsix` to each registry whose token is set, and skips the others with a note in
the log. CI runs the unit tests on every push, and the integration tests in a real VS Code under `xvfb`.

**One-time setup**, both free:

1. **Visual Studio Marketplace.** Sign in at https://marketplace.visualstudio.com/manage with a Microsoft account and
   create the publisher `ImperiumGames` (the id in `package.json`). In Azure DevOps (https://dev.azure.com, the same
   account), User settings → Personal access tokens → New token: organization **All accessible organizations**, scope
   **Marketplace → Manage**. Put it in the `VSCE_PAT` secret.
2. **Open VSX** (VSCodium, Cursor and the other editors that don't use Microsoft's marketplace). Sign in at
   https://open-vsx.org with GitHub, sign the publisher agreement in the profile, create a token under Access Tokens,
   and claim the namespace once: `npx ovsx create-namespace ImperiumGames -p <token>`. Put the token in the `OVSX_PAT`
   secret.

The next tag then publishes the extension to both. To try a build by hand:

```bash
cd integrations/vscode
npm ci && npm test
npx vsce package          # desk-arcade-<version>.vsix; install it with Extensions → ⋯ → Install from VSIX…
```

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

**Autostart and hooks:** the AppImage runs from a temporary mount, so **Start when I sign in** and **Copy
Claude Code hook config** record the AppImage file itself (from `$APPIMAGE`), not the `/tmp/.mount_…`
path. If you move the AppImage later, turn autostart off and on again and re-copy the hook config.

**Limitations:** The AppImage expects the X11, fontconfig and PulseAudio libraries that desktop distributions install.

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
