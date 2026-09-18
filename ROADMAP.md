# Roadmap: Desk Arcade 1.4.0

1.3.0 shipped on 2026-09-16 (its roadmap is in the git history). 1.4.0 is about **playing with the person at
the next desk**: two copies of Desk Arcade on the same local network find each other and share a game. No
server and no account are involved.

Work happens on `feature/roadmap-1.4`, one small step at a time. Each item says how it was verified.

## Multiplayer over the local network

How it works: one player **hosts** and the other **joins** from the tray (**Play over LAN**). The joiner
broadcasts on UDP port 47820 and the first host that answers is paired with them. The host runs the game
and the guest sends only its own input. Positions are sent as fractions of the screen, so the two screens
can be different sizes. The guest's view is mirrored, so each player sees themselves on the left.

- [x] **LAN link.** UDP discovery, pairing, heartbeat and a disconnect after 3 s of silence
  (`src/Net/LanLink.cs`). Host, join and leave are in the tray and on `--signal lan-host|lan-join|lan-leave`.
  *Verified: two `--profile` copies on one machine paired, and the guest switched to the host's game.*
- [x] **Air Hockey 1-vs-1.** The other player's mallet replaces the CPU. The host runs the physics and
  sends the puck, mallets and score about 60 times a second. Grabbing your mallet after a match starts
  a rematch. *Verified: 45 s demo match between two local copies; the guest's goal was scored in the
  host's simulation and counted in the guest's stats. Not yet tried between two real PCs or by two people.*
- [ ] **Hoops H-O-R-S-E.** Turn-based: you take a shot, then your co-worker has to make the same shot. Only
  shot results and turns cross the network, so lag doesn't matter.
- [x] **Checkers (draughts).** A new game, against the CPU or over the LAN. English rules: forced
  captures, multi-jumps, a man crowned on the far row, and a draw after 40 moves each with no capture
  and no man moving. The rules live in `Draughts.cs`, free of UI, and the CPU searches 4 plies ahead.
  The host keeps the real board and sends it every 0.4 s; the guest re-sends its move until the host's
  board includes it, so a lost packet can't put the boards out of step. *Verified: 6 rule tests; a full
  LAN demo game between two local copies ended in a win on the guest's side; a demo game against the
  CPU. Not yet played by two people.*
- [x] **Board game base.** `BoardGame.cs` holds the board, input, CPU turn and LAN sync shared by
  Checkers and Chess; each game only supplies its rules (`IBoardRules`) and how its pieces look.
- [x] **Chess.** Castling, en passant, promotion (always to a queen), check, mate, stalemate, the 50-move
  rule and bare-minor-piece draws; threefold repetition isn't tracked. The CPU searches 3 plies plus
  captures. *Verified: perft matches the published counts from the start (depth 3), Kiwipete (depth 3)
  and an en-passant endgame (depth 4); fool's mate, promotion and CPU tests; a full LAN demo game
  between two local copies; the pieces checked on screen.*
- [ ] **Connect Four / Tic-tac-toe.** Cheap extras once the turn-based framework exists.
- [ ] **Lobby.** Pick which host to join when several are on the network, show the other player's
  name, and add a chat-free set of emotes ("gg", "one more?").
- [ ] **Race modes.** Both players play the same Bubble Pop or Whack-a-Bug round at once, with a live
  score for each.
- [ ] **Tests.** The message format and a host/guest round trip over loopback.

## Fix known gaps

- [x] **AppImage paths.** Autostart and the Claude hook config use `$APPIMAGE` (the AppImage file), not
  the temporary mount (`Program.LaunchPath`). *Verified: unit test; not yet run inside a real AppImage.*
- [ ] **Real-hardware smoke tests in CI.** `windows-11-arm`, `ubuntu-24.04-arm` and `macos-14` runners
  start the app, send `--signal stats` and quit it with `--signal quit`, which clears most of the 1.3
  "Not verified here" table.
  *Written: the `smoke` job in `ci.yml` runs `tests/smoke.sh`, which passes locally on Windows x64, and
  actionlint is clean. Not ticked until it has run on the GitHub runners.*

## Distribution

- [ ] **winget and Flathub.** Submit the manifests (winget already passes `winget validate`; the Flatpak
  manifest builds).
- [ ] **Homebrew cask** for macOS, and possibly **Scoop** for Windows.
- [ ] **Install updates.** The update checker only offers a download; let it download and run the
  installer.

## Features

- [ ] **Rebindable shortcuts.** Ctrl+Alt+G/N/B can clash with other apps, especially on Wayland.
- [ ] **More Claude Code integration.** Pause the game or show a notice when Claude needs permission, and
  a session summary ("played 4 min while Claude worked 12 min").
- [ ] **Daily challenge and streaks,** built on the stats and achievements.
- [ ] **More pets** (a dog, a duck), reusing most of `PetGame.cs`.
- [ ] **Accessibility.** Colour-blind-safe palettes and a reduced-motion setting.
- [ ] **Board game achievements** for Checkers and Chess.

## Known limits

- Firewalls: the first time you host, Windows asks whether to allow Desk Arcade on private networks.
  Joining needs UDP port 47820 open on the host's machine.
- Office networks that block broadcast (client isolation, some Wi-Fi) will need "join by IP", which is
  planned with the lobby.
