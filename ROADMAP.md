# Roadmap: Desk Arcade, October to December 2026

1.7.0 shipped on 2026-09-21 with natural pet voices and animal behaviour (the 1.6.0 and 1.7.0 roadmap is
in the git history). The next three months are about **getting Desk Arcade in front of people**: package
managers, Flathub, a real Mac test, and the visibility SignPath asked for before it signs Windows builds.
New games come along the way, one or two per release.

Each item says how it will be verified. "Demo" means copies on one PC (`--profile`) playing by themselves
(`--demo`).

| Release | Target | Theme |
|---|---|---|
| 1.8.0 | late October | Package managers, Fetch for the pet |
| 1.9.0 | late November | Flathub, a real Mac, Paper Toss and Darts |
| 2.0.0 | mid December | Signed Windows builds, Bowling, a winter event |

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
- [ ] **Fetch.** Throw a ball for the pet: it runs after it, jumps between windows to reach it and brings it
  back to the cursor. Dogs fetch eagerly, cats only sometimes, ducks not at all. *Verified: unit test for the
  chase path; demo run.*
- [ ] **Pet voice tuning** after a listen on real speakers, and a volume slider for the pet alone.
  *Verified: listened to on speakers and headphones.*

## 1.9.0: Linux and macOS (November)

- [ ] **Flathub.** Pick the app id (`io.github.BokhodirUrinboev.DeskArcade` unless the `imperiumgames.com`
  domain can be verified), attach a `linux-x64` publish tarball to each release for an `archive` source, add
  screenshots to the metainfo, and replace the `xdg-config/autostart` permission with the Background portal.
  Then open the submission against `flathub/flathub`. *Verified: `flatpak-builder-lint` and `appstreamcli
  validate` pass; the bundle runs on Ubuntu 24.04.*
- [ ] **A real Mac.** Run the macOS build on Apple Silicon and Intel: click-through, the window list, hotkeys,
  sound, the tray. Fix what breaks, and decide on an Apple Developer ID for notarization (needed for
  homebrew/cask itself). *Verified: a checklist run on both Macs, recorded here.*
- [ ] **Paper Toss.** Flick a paper ball into a bin on a window top; a desk fan blows a different wind each
  throw. LAN: race. *Verified: unit tests for the flight and the wind; demo run.*
- [ ] **Darts.** A board on the screen, the aim wobbles while you hold, 501 down to a double. LAN: turns.
  *Verified: unit tests for the scoring and checkouts; demo run.*

## 2.0.0: signed and seasonal (December)

- [ ] **Reapply to SignPath Foundation** (declined on 2026-09-18 for too little visibility) once the package
  managers and Flathub listings, a README with screenshots and a short demo GIF, and download numbers from
  1.8 and 1.9 are in place. With the certificate, Windows builds stop tripping SmartScreen. *Verified: the
  2.0.0 installers carry a valid signature.*
- [ ] **Bowling.** Roll a ball along the taskbar at pins on a window top; 10 frames with spares and strikes.
  LAN: frame by frame, pins as ghosts. *Verified: unit tests for the scoring; demo run.*
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
| macOS: click-through, window list, hotkeys, sound | A Mac |
| The Node 24 action versions in the release workflow | A release dry run |

## Ideas for more mini games

Games that suit the overlay: quick to start, played with the mouse (the overlay never takes the keyboard),
and using the windows and taskbar as the playing field. LAN notes say how each could work over the network.

| Idea | How it plays | LAN |
|---|---|---|
| **Curling** | Slide stones along the taskbar toward a target painted on the floor; knock the rival's stones away | Turns with ghost stones, like the golf duel |
| **Pool** | A table drawn over the screen, cue by dragging back from the white ball (Air Hockey's physics, with friction and pockets) | Turns; host runs the balls |
| **Pinball** | Flippers in the bottom corners, bumpers on window tops, the taskbar as the drain | Score race |
| **Window Tetris** | Blocks fall from the top and settle on window tops as well as the taskbar | Race; cleared lines send garbage to the rival |
| **Fishing** | Cast into a pond along the taskbar and reel in with well-timed clicks; rare fish are worth more | Race for the biggest catch |
| **Memory** | Pairs of cards laid over the screen; flip two at a time | Turns, both see every flipped card |
| **More card games** | Fool's cousins on the same room code: Perevodnoy (pass the attack on), Blackjack against the house, Crazy Eights | Rooms, like Durak |
| **Code Breaker** | Guess a hidden four-colour code from black and white pegs (Mastermind) | Each sets a code for the other |
| **Asteroids** | Rocks drift and bounce around the closed box; steer a ship with the mouse and click to fire | Co-op: two ships, one field |
