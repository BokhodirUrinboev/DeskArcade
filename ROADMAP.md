# Roadmap: Desk Arcade, October to December 2026

1.7.0 shipped on 2026-09-21 with natural pet voices and animal behaviour (the 1.6.0 and 1.7.0 roadmap is
in the git history). The next three months are about **getting Desk Arcade in front of people**: package
managers, Flathub, a real Mac test, and the visibility SignPath asked for before it signs Windows builds.
Eight new games came early and shipped in 1.7.1; after that the new work is LAN play for them.

Each item says how it will be verified. "Demo" means copies on one PC (`--profile`) playing by themselves
(`--demo`).

| Release | Target | Theme |
|---|---|---|
| 1.7.1 | 2026-09-22 | Eight new games; Durak with a co-worker joins one room |
| 1.8.0 | late October | Package managers, Fetch for the pet |
| 1.9.0 | late November | Flathub, a real Mac, LAN play for the new games |
| 2.0.0 | mid December | Signed Windows builds, a winter event |

## 1.7.1: eight new games (shipped 2026-09-22)

- [x] **Paper Toss** (`toss`): flick a paper ball into a bin on a window top or the taskbar; a desk fan blows
  a new wind each throw, stronger with the streak. *Verified: unit tests for the flight, the wind and the aim
  solver (35 wind/layout cases); demo run.*
- [x] **Darts** (`darts`): 501 double out, an aim that wobbles more the longer you hold, checkout suggestions.
  *Verified: unit tests for every ring and segment, busts, double-out and checkouts 2–170; demo run.*
- [x] **Bowling** (`bowling`) and **Pool** (`pool`) on one top-down disc physics engine (`DiscTable`).
  Bowling: 10 frames on a lane above the taskbar, LAN race. Pool: clear the table in the fewest shots.
  *Verified: unit tests for the physics (collisions, pockets, no tunnelling) and the bowling score sheet; demo
  runs.*
- [x] **Pinball** (`pinball`): flippers at the bottom, bumpers in the middle and on window tops, the taskbar as
  the drain. *Verified: unit tests for the flipper physics (cradle, launch, no tunnelling) and scoring; demo
  run.*
- [x] **Fishing** (`fishing`): cast, strike on the bite, reel against the line tension; 2-minute rounds, LAN
  race. *Verified: unit tests for the fight model and the bite timing; demo run.*
- [x] **Memory** (`memory`) and **Code Breaker** (`codebreaker`). *Verified: unit tests for the rules, and a
  solver that breaks 300 random codes in 5 guesses or fewer; demo runs.*
- [ ] **A feel pass by hand** on all eight: throw power, wobble, fight length, flipper timing. The demos play
  them, but nobody has played them with a real mouse yet.

## 1.8.0: package managers (October)

- [ ] **winget.** Sign the Microsoft CLA on
  [winget-pkgs#437055](https://github.com/microsoft/winget-pkgs/pull/437055) and see the first submission
  through moderator review, then submit 1.7.0. *Verified: `winget install ImperiumGames.DeskArcade` on a
  clean Windows Sandbox.*
- [ ] **Automatic package updates.** After the GitHub Release is published, the release workflow stamps the
  Homebrew cask and the Scoop manifest and pushes them to
  [homebrew-tap](https://github.com/BokhodirUrinboev/homebrew-tap) and
  [scoop-bucket](https://github.com/BokhodirUrinboev/scoop-bucket), and opens the winget update with
  `wingetcreate update --submit`. Needs a fine-grained token as a repository secret. *Verified: the 1.8.0
  tag updates all three without a hand-made commit.*
- [ ] **Release dry run** with the Node 24 action versions from #18, before the 1.8.0 tag. *Verified:
  Actions → Release → Run workflow builds every package.*
- [x] **Play while a command runs.** `arcade dotnet test` (Windows, `arcade.cmd` on PATH from setup or Scoop)
  or `deskarcade --while make` runs the command in the terminal, shows it on the scoreboard and chimes when
  it passes or fails; the exit code and output come through. *Verified: unit tests for the quoting and the
  label; on Windows 11 the output reached the console, exit codes 0, 3 and 4 came through, and the scoreboard
  dot turned blue, then green. On Ubuntu 24.04 (WSL) arguments kept their spacing, output piped and exit
  codes came through. The installer script compiles.*
- [x] **Solitaire** (`solitaire`): Klondike, draw one, on a felt over the desktop; click a card to send it where
  it fits or drag it, undo, and the last cards go home by themselves. *Verified: unit tests for the deal, every
  move rule, undo and one-click moves, and a simple player that solves some of 300 deals without a card lost;
  a demo run on Windows 11.*
- [ ] **Last Card**, an UNO-style game for 2–4: colours and numbers, Skip, Reverse, +2, Wild and Wild +4, and a
  "Last card!" button to press in time. Against computer players or co-workers in a room, like Durak.
- [ ] **Fetch.** Throw a ball for the pet: it runs after it, jumps between windows to reach it and brings it
  back to the cursor. Dogs fetch eagerly, cats only sometimes, ducks not at all. *Verified: unit test for the
  chase path; demo run.*
- [ ] **Pet voice tuning** after a listen on real speakers, and a volume slider for the pet alone.
  *Verified: listened to on speakers and headphones.*

## 1.9.0: Linux and macOS (November)

- [ ] **LAN for the new games:** Darts and Pool turn by turn (the other player's darts and shots as ghosts),
  Pinball and Paper Toss as score races. *Verified: two copies over loopback, then two PCs.*
- [ ] **Flathub.** Pick the app id (`io.github.BokhodirUrinboev.DeskArcade` unless the `imperiumgames.com`
  domain can be verified), attach a `linux-x64` publish tarball to each release for an `archive` source, add
  screenshots to the metainfo, and replace the `xdg-config/autostart` permission with the Background portal.
  Then open the submission against `flathub/flathub`. *Verified: `flatpak-builder-lint` and `appstreamcli
  validate` pass; the bundle runs on Ubuntu 24.04.*
- [ ] **A real Mac.** Run the macOS build on Apple Silicon and Intel: click-through, the window list, hotkeys,
  sound, the tray. Fix what breaks, and decide on an Apple Developer ID for notarization (needed for
  homebrew/cask itself). *Verified: a checklist run on both Macs, recorded here.*

## 2.0.0: signed and seasonal (December)

- [ ] **Reapply to SignPath Foundation** (declined on 2026-09-18 for too little visibility) once the package
  managers and Flathub listings, a README with screenshots and a short demo GIF, and download numbers from
  1.8 and 1.9 are in place. With the certificate, Windows builds stop tripping SmartScreen. *Verified: the
  2.0.0 installers carry a valid signature.*
- [ ] **Winter event** (from 2026-12-15): snow settling on window tops, a snowball mode for Slingshot and
  scarves for the pets, switched on by the seasonal theme. *Verified: demo run with the date set to
  December.*
- [ ] **Durak across real PCs,** played by people, with any fixes it needs. *Verified: a full game on three
  PCs.*

## Not verified yet

| What | Needs |
|---|---|
| Durak rooms across real PCs, played by people | Two to four PCs on one network |
| How the pet voices sound; the new pet behaviours on screen | Speakers and a look on screen |
| How the eight new games and Solitaire feel with a real mouse (Solitaire's drag and drop was not tried by hand) | Someone playing them |
| macOS: click-through, window list, hotkeys, sound | A Mac |
| The Node 24 action versions in the release workflow | A release dry run |
| The setup's "arcade" PATH option (added, then removed on uninstall); `--while` with the overlay on a Linux desktop and on macOS | Windows Sandbox; a Linux PC and a Mac |

## Ideas for more mini games

Games that suit the overlay: quick to start, played with the mouse (the overlay never takes the keyboard),
and using the windows and taskbar as the playing field. LAN notes say how each could work over the network.

| Idea | How it plays | LAN |
|---|---|---|
| **Curling** | Slide stones along the taskbar toward a target painted on the floor; knock the rival's stones away | Turns with ghost stones, like the golf duel |
| **Window Tetris** | Blocks fall from the top and settle on window tops as well as the taskbar | Race; cleared lines send garbage to the rival |
| **More card games** | Fool's cousins on the same room code: Perevodnoy (pass the attack on), Blackjack against the house, Crazy Eights | Rooms, like Durak |
| **Asteroids** | Rocks drift and bounce around the closed box; steer a ship with the mouse and click to fire | Co-op: two ships, one field |
