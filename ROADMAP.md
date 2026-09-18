# Roadmap: Desk Arcade 1.4.0

1.3.0 shipped on 2026-09-16 (its roadmap is in the git history). 1.4.0 is about **playing with the person at
the next desk**: two copies of Desk Arcade on the same local network find each other and share a game. No
server and no account are involved.

Work happens on `feature/roadmap-1.4`. Each item says how it was verified; the last section lists what this
machine could not check. "Demo" means two copies on one PC (`--profile`) playing by themselves (`--demo`).

## Multiplayer over the local network

How it works: one player **hosts** and the other **joins** from the tray (**Play over LAN**). Messages are
short UDP datagrams on port 47820. Real-time games (Air Hockey) have the host simulate and the guest send
its input; turn-based games number their moves and re-send them until the other side confirms, so a lost
packet never puts the two screens out of step. Positions travel as fractions of the screen, so the screens
may differ in size.

- [x] **LAN link.** Discovery, pairing, heartbeat, a 3 s timeout, and the guest following the host's game
  switches (`src/Net/LanLink.cs`). *Verified: loopback unit tests (pairing, messages, emotes, leaving); every
  demo below.*
- [x] **Air Hockey 1-vs-1.** *Verified: 45 s demo; the guest's goal counted in the host's simulation.*
- [x] **Hoops H-O-R-S-E.** Each player shoots on their own screen; only results and the spot to match cross
  the link. *Verified: `HorseMatch` rule tests; 90 s demo with turns passing both ways.*
- [x] **Checkers (draughts).** English rules, forced captures, multi-jumps, a draw after 40 quiet moves each.
  *Verified: 6 rule tests; a full demo game.*
- [x] **Chess.** Castling, en passant, promotion (always a queen), check, mate, stalemate, 50-move rule;
  threefold repetition isn't tracked. *Verified: perft matches the published counts from the start
  (depth 3), Kiwipete (depth 3) and an en-passant endgame (depth 4); a full demo game.*
- [x] **Connect Four and Tic-tac-toe.** On the shared `BoardGame`, now any size and with "place a piece"
  moves. *Verified: rule and CPU tests; checked on screen.*
- [x] **Sea Battle.** Hidden fleets that never touch; a hit shoots again; the CPU hunts around its hits.
  *Verified: fleet and CPU tests; CPU and demo games to a win; checked on screen.*
- [x] **Lobby.** **Find games / join by address…** lists every host with its player and game, joins by IP
  where broadcasts are blocked, and the host's tray shows its address; emotes ("gg", "One more?"…).
  *Verified: loopback tests; the lobby listed a hosting copy on screen.*
- [x] **Race modes.** Bubble Pop and Whack-a-Bug: starting a round starts the rival's, both see the live
  score, and each side's n-th round is compared. *Verified: demo race produced a result.*
- [x] **Achievements.** One per board game, Sea Battle, and "Office rival" for any LAN win.

## Fix known gaps

- [x] **AppImage paths.** Autostart and the Claude hook config use `$APPIMAGE`, not the temporary mount
  (`Program.LaunchPath`). *Verified: unit test; not yet run inside a real AppImage.*
- [x] **Real-hardware smoke tests in CI.** The `smoke` job runs `tests/smoke.sh` (start in demo mode, open
  the stats window, quit over `--signal`) on `windows-11-arm`, `ubuntu-24.04-arm` and `macos-14`.
  *Verified: passes on all three runners (CI run 35307933851), so the ARM64 builds and the macOS app have
  now actually run.*

## Distribution

- [x] **Homebrew cask and Scoop manifest.** `packaging/homebrew`, `packaging/scoop`, stamped by
  `packaging/Update-PackageManifests.ps1`. *Verified: stamped from the real 1.3.0 release files.*
- [x] **Install updates.** Windows installs download the matching installer, check GitHub's SHA-256 digest and
  run it silently; the installer restarts the game. Other platforms open the download page. *Verified:
  unit test for the installer name; GitHub reports the digests; the install itself needs a newer release.*
- [ ] **Submissions: winget, Flathub, a Homebrew tap, a Scoop bucket.** Everything is prepared (see
  docs/RELEASING.md); submitting needs your GitHub account and, for Flathub, an app-id review.

## Features

- [x] **Rebindable shortcuts.** **Tray → Shortcuts…**: the modifier keys and a letter per action. Windows
  applies them at once; X11, the Wayland portal and macOS from the next start. *Verified: unit tests; the
  window on screen; builds for Linux and macOS.*
- [x] **More Claude Code integration.** "Pause the game when Claude finishes or needs you" (a click resumes),
  and the done notice sums up the session. *Verified: on screen ("Claude worked 0:09, you played 0:09").*
- [x] **Daily challenge and streaks.** In the tray and the stats window; two achievements. *Verified: unit
  tests (streaks, every challenge counts something the games record).*
- [x] **More pets.** A dog and a duck, picked in **tray → Pet**. *Verified: on screen.*
- [x] **Accessibility.** Reduce motion; colour-blind friendly colours (plus a shape cue in Connect Four).
  *Verified: unit test for the colour mapping.*

## Not verified here

| What | Needs |
|---|---|
| LAN games between two real PCs, played by two people; office networks that block broadcast | Two PCs on one network |
| Rebinding shortcuts on X11, Wayland and macOS | Those desktops |
| The silent in-place update | A release newer than the installed version |
| AppImage autostart with `$APPIMAGE` | A Linux desktop |
