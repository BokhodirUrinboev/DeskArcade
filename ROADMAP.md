# Roadmap: Desk Arcade 1.3.0

Everything planned for 1.3.0 is implemented and committed on `feature/roadmap-1.3`. Each item says how
it was verified; the last section lists what this machine could not check.

## Quick wins

- [x] **PR build check.** `.github/workflows/ci.yml` builds on Windows, Ubuntu and macOS, packages and
  install-checks the `.deb`, runs the unit tests and lints the workflows on every pull request.
  *Verified: actionlint passes; not yet run on GitHub.*
- [x] **Security fix.** Avalonia 11.3.22 brings Tmds.DBus.Protocol 0.21.3, the patched release for
  GHSA-xrw6-gwf8-vvr9. *Verified: `dotnet list package --vulnerable` reports nothing.*
- [x] **Update checker.** Tray item plus one automatic check a day against GitHub Releases, with an
  offer to download when a newer version exists. *Verified: version parsing is unit-tested.*
- [x] **Volume control.** 25/50/75/100% in the tray menu. *Verified: on screen.*

## Features

- [x] **Claude Code integration.** Optional "show the overlay when Claude starts" and "hide it when
  Claude finishes or needs you", plus a live "Claude working · 3:12" timer and "waited 0:07" on the
  notice. *Verified: on screen with `--signal working` / `done`.*
- [x] **Desktop pet.** A cat that walks the taskbar and window tops, follows the cursor, can be petted,
  carried and thrown, and sleeps when idle. *Verified: demo run, petted and carried 4 times each.*
- [x] **Stats and achievements.** Per-game counters, time played, 39 achievements with unlock popups,
  and a stats window (tray, or `--signal stats`). *Verified: unit tests and on screen.*
- [x] **Uzbek and Russian.** Every string in the games, menus, notices and achievements, with a language
  menu. *Verified: 282 strings covered in both languages, unit-tested; the Russian UI checked on screen.*

## New games

- [x] **Tower Stack.** *Verified: demo reached height 15 with 11 perfect drops.*
- [x] **Slingshot.** *Verified: demo run.*
- [x] **Whack-a-Bug.** *Verified: demo run, 8 bugs whacked.*
- [x] **Clay Shooting.** *Verified: demo run, 4 clays hit.*
- [x] **Plinko.** *Verified: demo run, 7 discs dropped; the board position is saved.*

## Platforms and distribution

- [x] **macOS.** Platform layer (click-through, window list, Control+Option hotkeys, AudioQueue sound,
  LaunchAgent autostart) and `.app` packaging. *Verified: compiles for osx-arm64 and osx-x64. Never run
  on a Mac.*
- [x] **Wayland global shortcuts.** Registered through the XDG GlobalShortcuts portal, falling back to
  X11 grabs. *Verified: 9 of 9 D-Bus scenarios against a mock portal. Never run on a real Wayland session.*
- [x] **ARM64 builds.** Windows ARM64 installer, arm64 `.deb` and aarch64 AppImage.
  *Verified: packages build and carry ARM64 binaries; the binaries were never executed.*
- [x] **AppImage.** x86_64 and aarch64. *Verified: built and `--signal quit` works in WSL.*
- [x] **Flatpak.** Manifest, AppStream metainfo and launcher. *Verified: builds and runs in a
  flatpak-builder container. Not submitted to Flathub.*
- [x] **winget.** Manifest templates and a script to stamp a release. *Verified: `winget validate` passes.
  Not submitted.*
- [x] **Windows signing.** signtool for the exe and Inno Setup's SignTool for the installers, skipped
  cleanly without secrets. *Verified with a throwaway self-signed certificate.*

## Not verified here

These need hardware, an account or a service this machine doesn't have:

| What | Needs |
|---|---|
| macOS overlay, click-through, hotkeys, sound, `.app` bundle, Gatekeeper wording | A Mac |
| Wayland shortcut dialog, whether the portal accepts the `deskarcade` app id, shortcut conflicts | A GNOME 48+ / KDE Wayland session |
| ARM64 binaries actually running | ARM64 hardware |
| The CI and release workflows on real runners | A push to GitHub |
| `signtool verify /pa` with a trusted certificate | A code-signing certificate |
| Flatpak tray icon and autostart on a real desktop; Flathub submission | A Linux desktop, a Flathub app id |
| AppImage autostart and the Claude Code hook config, which record the temporary mount path | A fix in `src/` (documented in docs/RELEASING.md) |
