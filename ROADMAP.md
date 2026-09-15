# Roadmap: Desk Arcade 1.3.0

Work in progress on the `feature/roadmap-1.3` branch. Each item is checked off once it is implemented
and verified; the note says how it was verified and what still needs a person.

## Quick wins

- [ ] **PR build check.** CI workflow that builds on Windows, Ubuntu and macOS, runs the unit tests and lints the workflows on every pull request.
- [ ] **Security fix.** Remove the known vulnerability in `Tmds.DBus.Protocol` (via the Avalonia 11.3 upgrade).
- [ ] **Update checker.** "Check for updates" in the tray, plus an automatic daily check against GitHub Releases.
- [ ] **Volume control.** Volume levels in the tray menu.

## Features

- [ ] **Claude Code integration.** Optionally show the overlay when Claude starts working, hide it when Claude finishes or needs you, and show how long you waited.
- [ ] **Desktop pet.** A creature that walks along window tops and the taskbar, reacts to the cursor, can be carried and petted, and sleeps when idle.
- [ ] **Stats and achievements.** Per-game counters, time played, and achievements with unlock popups and a stats window.
- [ ] **Uzbek and Russian.** All game and menu text translatable, with a language menu (automatic, English, O'zbekcha, Русский).

## New games

- [ ] **Tower Stack.** Drop swinging blocks to build a tower on a window top or the taskbar.
- [ ] **Slingshot.** Knock block towers off your window tops.
- [ ] **Whack-a-Bug.** Bugs peek out from behind window edges; click them fast.
- [ ] **Clay Shooting.** Clay targets fly across the screen and bounce inside the closed box.
- [ ] **Plinko.** Drop discs through a peg board into scoring slots.

## Platforms and distribution

- [ ] **macOS.** Platform layer (click-through, window tops, hotkeys, sound, autostart), `.app` packaging, and a CI launch test.
- [ ] **Wayland global shortcuts.** Register Ctrl+Alt+G/N/B through the XDG GlobalShortcuts portal on newer GNOME and KDE.
- [ ] **ARM64 builds.** Windows, Ubuntu and macOS packages for ARM64 in the release workflow.
- [ ] **AppImage.** A single-file Linux build for other distributions.
- [ ] **Flatpak.** A Flatpak manifest.
- [ ] **winget.** A winget manifest for `winget install`.
- [ ] **Windows signing.** Signing step in the release workflow, enabled when a certificate is configured.
