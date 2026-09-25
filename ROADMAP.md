# Roadmap: Desk Arcade, October to December 2026

1.7.0 shipped on 2026-09-21 with natural pet voices and animal behaviour (the 1.6.0 and 1.7.0 roadmap is
in the git history). The next three months are about **getting Desk Arcade in front of people**: package
managers, Flathub, a real Mac test, and the visibility SignPath asked for before it signs Windows builds.
Eight new games came early and shipped in 1.7.1, four more with `--while` and the first LAN races in 1.7.2, and
1.8.0 turned every game into a two-player game: a computer rival or a co-worker in each of them, twelve pets,
animation everywhere, and an overlay that behaves on a real Ubuntu desktop.

Each item says how it will be verified. "Demo" means copies on one PC (`--profile`) playing by themselves
(`--demo`).

| Release | Target | Theme |
|---|---|---|
| 1.7.1 | 2026-09-22 | Eight new games; Durak with a co-worker joins one room |
| 1.7.2 | 2026-09-24 | `--while`, Solitaire, Last Card, Interns, Blockfall; Pinball and Paper Toss races |
| 1.8.0 | 2026-09-24 | Somebody to play against in every game, twelve pets, animation, thirteen themes, the Ubuntu overlay and updates, the ☰ menu |
| 1.8.1 | late September | Five new games: Marble Run, Sheep Herding, Cannon Castles, Reversi, Gomoku |
| 1.9.0 | late October | Package managers, a feel pass by hand, Flathub |
| 2.0.0 | mid December | Signed Windows builds, a real Mac, a winter event |

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

## 1.7.2: play while it builds, four more games (shipped 2026-09-24)

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
- [x] **Last Card** (`lastcard`), an UNO-style game for 2–4: colours and numbers, Skip, Reverse, +2, Wild and
  Wild +4, and a "Last card!" button to press in time. Against computer players, or co-workers in a room like
  Durak's; the room window is now shared, and each room names its game so the lists stay apart (Durak rooms
  keep the old format, so 1.7.1 copies still see them). *Verified: unit tests for the deck, every card's
  effect, drawing, the call and the catch, and computer players finishing 300 games of 2, 3 and 4; two copies
  on one PC (`--profile`, `--demo`) played a room of host, guest and a computer through two games, and both
  saw the same games end.*
- [x] **Interns** (`interns`): lead office interns from a trapdoor to the exit on the taskbar, over window tops and
  past manholes, with umbrellas, blockers and builders; dragging a window carries everyone on it. *Verified:
  unit tests for walking, turning, manholes, high falls and umbrellas, stairs over a manhole, blockers,
  riding a moving window and running out of tools; a demo run on Windows 11 cleared level 1 and bridged
  both manholes of level 2.*
- [x] **Blockfall** (`blockfall`), the "Window Tetris" idea: falling blocks steered with the mouse (the piece
  follows the pointer's column; click turns, hold drops faster, right-click drops), a well that stands on a
  window top and rides along with it, and a LAN score race. *Verified: unit tests for the bags, turning and
  wall nudges, drops, line clears and scoring, levels and game over, and 5,000 random moves that never lose a
  cell; a demo run on Windows 11 cleared four lines at once.*
- [x] **Pinball and Paper Toss as LAN score races** (came early): a three-ball game, or a run until the first
  miss, is one race. *Verified: two copies over loopback playing by themselves: two Pinball games each and five
  Paper Toss races decided on both sides.*

## 1.8.0: somebody to play against, everywhere (shipped 2026-09-24)

- [x] **Race the computer.** Every round-based game (21 of them: Bubble Pop, Whack-a-Bug, Tower Stack, Bowling, Fishing,
  Pinball, Paper Toss, Blockfall, Keepy-Uppy, Bug Squash, Can Knockdown, Brick Breaker, Clay Shooting, Plinko,
  Slingshot, Darts, Pool, Memory, Code Breaker, Solitaire, Interns) races a computer rival when nobody is on the LAN:
  it plays a round of its own at the game's CPU level, aiming at a fair round or your best, paced over a typical
  round, with red rings where it scores; the level moves up after two wins in a row and down after two losses.
  Fewer-is-better games (Darts, Pool, Memory, Code Breaker) count the other way. *Verified: unit tests for the
  rival's pacing, levels and fewer-is-better; demo runs of every race game with `race.cpuwins` in the stats.*
- [x] **LAN races for the thirteen games that had none** (Keepy-Uppy, Bug Squash, Can Knockdown, Brick Breaker, Clay
  Shooting, Plinko, Slingshot, Darts, Pool, Memory, Code Breaker, Solitaire, Interns), with the rival's live score
  and rings. *Verified: two copies over loopback on each, playing by themselves, with `lan.wins` on both sides.*
- [x] **CPU difficulty in every game with a computer opponent**, one setting per game (tray → CPU difficulty, or the ☰
  menu): the board games as before, Sea Battle with four new shooters (random, hunt-and-target, checkerboard, density),
  Air Hockey and Pong with four base strengths under the session ratchet, and every race game. *Verified: unit tests
  for the shooters, the table strengths and the level steps.*
- [x] **The scoreboard shows who you play and whose turn it is**: a chip with "CPU · Hard", "vs Alice", "Your turn" or
  "Alice's turn" (the dot breathes while you wait) on the pill and the board; the score pops when it changes; a
  **☰ menu** on the scoreboard with the essentials of the tray menu. *Verified: screenshots under Xvfb of the chips,
  the menu and a live CPU race.*
- [x] **Boards and tables move**: a grip above chess, checkers, Connect Four, Tic-tac-toe, Sea Battle, Durak, Last Card,
  Plinko, Darts, Pool, Memory, Code Breaker and Solitaire (right-drag still works), remembered per game and cleared by
  Reset positions. *Verified: an xdotool drag under Xvfb, the place kept across a restart.*
- [x] **Animation in every game** on a small tween engine (`Engine/Anim.cs`): games rise into place when switched to;
  pieces slide and captures fade on the boards; cards deal, fly and re-fan; discs bounce, bricks fall in, cans drop,
  pins wobble, bumpers pulse, rows collapse, shells arc, clays shatter, and wins celebrate. Reduced motion jumps
  every tween to its end and keeps the timing. *Verified: demo runs of all 34 games without a crash; unit tests for
  the tween list, the easing curves, the board move planner and the deal plans.*
- [x] **Twelve pets**: hamster, turtle, parrot, frog, owl and a small dragon join the six, each with its own art,
  voices, walk, habits and trick; and for all of them fetch (a ball, thrown and brought back, each animal its own
  way), a treat jar and begging, a thought bubble, night-time drowsiness and a morning stretch, keeping you company
  on the window nearest the cursor, and **pet visits over the LAN** (the co-worker's pet walks your desktop as a
  ghost and the two say hello). Three new achievements. *Verified: unit tests for the tables and the visit
  messages; demo runs as six kinds and a loopback visit with `pet.visits` counted.*
- [x] **Thirteen themes that dress everything**: the scoreboard, grips, boards, felts and card backs, pieces, popup gold
  and the pet's accessories follow the theme; Ocean, Forest, Sunset, Candy, Mono, Spring, Autumn and Midnight join the
  five, Seasonal walks the calendar month by month, and most themes drift light decorations (snow that settles on
  window tops, leaves, petals, bubbles, fireflies, stars, embers, confetti) only while a game is already moving, so an
  idle overlay still costs nothing. *Verified: unit tests for every palette's contrast, the calendar for every day of
  2026 and the decor model staying in its box and going quiet; screenshots under Xvfb of three themes; 0 % idle CPU.*
- [x] **Updates install themselves on Linux**: a `.deb` through PolicyKit's password prompt (apt, with dpkg as the
  fallback, and a terminal running sudo where there is no pkexec), an AppImage replaced in place, the game closed and
  started again by a wrapper that outlives it; progress on the scoreboard; the file's SHA-256 checked against GitHub's
  digest. *Verified: 65 unit tests that run the generated shell for real with fake pkexec, apt-get, dpkg and sudo. Not
  yet run on a real Ubuntu install (see below).*
- [x] **The overlay on a real Ubuntu desktop**: EWMH states (above, sticky, skip taskbar and pager, all workspaces)
  set as properties and sent as client messages on every show, keyboard focus handed straight back if a window
  manager gives it to us, a session-bus check for the tray with a one-time notice pointing at the ☰ menu when stock
  GNOME has none, a cheaper window enumeration, a stale-pointer rule under XWayland, and the .deb's missing X11
  dependencies. *Verified: the hints read back from the live X window under Xvfb; unit tests for the message
  bodies and the packaging files. Not yet run on a real GNOME session (see below).*

## 1.8.1: five new games from the ideas list

- [x] **Marble Run** (`marble`): place up to three ramps and bumpers, then drop a marble down the window tops into
  a cup on the taskbar; fewer pieces score more; five courses a round, raced by score. *Verified: unit tests for
  rolling, edges, pieces, the cup and scoring (`MarbleTests`). Not played by hand yet.*
- [x] **Sheep Herding** (`sheep`): the cursor is the sheepdog; the flock flees it, hops off and onto window tops,
  and is driven into a pen on the taskbar before the clock runs out; click to bark. *Verified: unit tests for
  fleeing, flocking, the pen and the clock (`SheepTests`). Not played by hand yet.*
- [x] **Cannon Castles** (`cannons`): castles on two window tops, drag back to aim, wind between them; four CPU
  levels and a LAN duel. *Verified: unit tests for the ballistics, the blocks, the CPU's aim and the duel
  messages (`CannonTests`). Not played by hand or across two PCs yet.*
- [x] **Reversi** (`reversi`) and **Gomoku** (`gomoku`) on the board-game table: flip animations, four CPU levels
  and LAN turns. *Verified: unit tests for the rules, each CPU level against the one below, and think time
  (`ReversiTests`, `GomokuTests`). Not played by hand or across two PCs yet.*
- [ ] The Russian and Uzbek text for the five games, read by native speakers.

## 1.9.0: package managers and a feel pass (October)

- [ ] **A feel pass by hand** on the 1.8.0 work, on Windows and Ubuntu: the computer rival's pacing and levels in each
  race game, the pets' fetch and treats, the grips, the animations at 60 Hz. *Verified: an afternoon with a real
  mouse, notes here.*
- [ ] **The Ubuntu overlay on real desktops**: GNOME on Wayland and Xorg, KDE Plasma 6, XFCE: always on top, every
  workspace, focus never taken, the tray notice on stock GNOME. *Verified: a checklist run on each, recorded here.*
- [ ] **winget.** Sign the Microsoft CLA on
  [winget-pkgs#437055](https://github.com/microsoft/winget-pkgs/pull/437055) and see the first submission
  through moderator review, then submit 1.8.0. *Verified: `winget install ImperiumGames.DeskArcade` on a
  clean Windows Sandbox.*
- [ ] **Automatic package updates.** After the GitHub Release is published, the release workflow stamps the
  Homebrew cask and the Scoop manifest and pushes them to
  [homebrew-tap](https://github.com/BokhodirUrinboev/homebrew-tap) and
  [scoop-bucket](https://github.com/BokhodirUrinboev/scoop-bucket), and opens the winget update with
  `wingetcreate update --submit`. Needs a fine-grained token as a repository secret. *Verified: the 1.9.0
  tag updates all three without a hand-made commit.*
- [ ] **Release dry run** with the Node 24 action versions from #18, before the 1.9.0 tag. *Verified:
  Actions → Release → Run workflow builds every package.*
- [ ] **Pet voice tuning** after a listen on real speakers, and a volume slider for the pet alone.
  *Verified: listened to on speakers and headphones.*

## 2.0.0: Linux, macOS and the season (November to December)

- [ ] **Flathub.** Pick the app id (`io.github.BokhodirUrinboev.DeskArcade` unless the `imperiumgames.com`
  domain can be verified), attach a `linux-x64` publish tarball to each release for an `archive` source, add
  screenshots to the metainfo, and replace the `xdg-config/autostart` permission with the Background portal.
  Then open the submission against `flathub/flathub`. *Verified: `flatpak-builder-lint` and `appstreamcli
  validate` pass; the bundle runs on Ubuntu 24.04.*
- [ ] **A real Mac.** Run the macOS build on Apple Silicon and Intel: click-through, the window list, hotkeys,
  sound, the tray. Fix what breaks, and decide on an Apple Developer ID for notarization (needed for
  homebrew/cask itself). *Verified: a checklist run on both Macs, recorded here.*
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
| Durak and Last Card rooms across real PCs, played by people | Two to four PCs on one network |
| How the pet voices sound (twelve of them now); fetch, treats and visits on screen | Speakers and a look on screen |
| The 1.8.0 overlay hints on real GNOME, KDE and XFCE sessions; the tray notice on stock GNOME; an in-app `.deb` and AppImage update end to end | An Ubuntu desktop with the 1.8.0 package installed |
| The computer rival's pacing and levels with a real mouse; the grips and animations at full frame rate | Someone playing |
| How the eight new games, Solitaire, Last Card and Interns feel with a real mouse (none of the last three was played by hand) | Someone playing them |
| macOS: click-through, window list, hotkeys, sound | A Mac |
| The Node 24 action versions in the release workflow | A release dry run |
| The setup's "arcade" PATH option (added, then removed on uninstall); `--while` with the overlay on a Linux desktop and on macOS | Windows Sandbox; a Linux PC and a Mac |

## Ideas for more mini games

Games that suit the overlay: quick to start, played with the mouse (the overlay never takes the keyboard),
and using the windows and taskbar as the playing field. LAN notes say how each could work over the network.

| Idea | How it plays | LAN |
|---|---|---|
| **Curling** | Slide stones along the taskbar toward a target painted on the floor; knock the rival's stones away | Turns with ghost stones, like the golf duel |
| **More card games** | Fool's cousins on the same room code: Perevodnoy (pass the attack on), Blackjack against the house, Crazy Eights | Rooms, like Durak |
| **Asteroids** | Rocks drift and bounce around the closed box; steer a ship with the mouse and click to fire | Co-op: two ships, one field |
| **Window Jenga** | A tower of blocks stands on a window top; pull one block out with a drag and set it on top; the tower leans with every window move | Turns, ghost hands |
| **Paper Planes** | Fold (click) and throw a paper plane from the corner; it glides through gaps between windows to a landing strip on the taskbar; thermals rise off warm (busy) windows | Distance race |
| **Kite** | Fly a kite from the taskbar on a string held by the cursor; the wind gusts with the desk fan; catch the clouds and dodge the windows | Two kites, tangle to cut the other's string |
| **Ping-Pong Cups** | Beer-pong with water cups on a window top; flick the ball from the bottom edge; the cups go down one by one | Turns, ghost balls |
| **Rope Bridge** | Interns need a bridge between two windows; drag planks into place before they walk off the edge | Co-op |
| **Snakes on Windows** | A snake crawls along the edges of windows; steer it with the cursor to apples; do not cross your own tail | Two snakes, one desktop |
| **Bingo of Work** | A card of everyday desk events ("a build passes", "Claude needs you", "10 minutes without a click"); the overlay ticks them off; first line wins | Office board |
| **Backgammon** | Dice and checkers; doubling cube optional | Turns |
| **Dominoes** | Tiles laid along the taskbar in a line that bends up the window edges | Rooms for 2–4 |
| **Sudoku / Minesweeper** | Puzzle pads for the quiet minutes; a mouse-only number pad | Daily challenge race: same puzzle, first to finish |
| **Mancala** | Seeds in pits along the taskbar; a calm sowing game | Turns |
| **Battleship Salvo** | Sea Battle with three shots a turn, bigger fleets, a fog of war that lifts | Rooms for 3 |

## Ideas for the pets

| Idea | What happens |
|---|---|
| **Pet families** | Two pets at once (a cat and a dog, or two hamsters); they play together, share the treat jar and squabble over the ball |
| **Costumes** | Hats and scarves per season (the winter scarf from 2.0.0, a pumpkin hat in October, a party hat on the day the stats say you first ran Desk Arcade) |
| **Pet mail** | A co-worker on the LAN can send your pet a treat or a toy; a little parcel drops from the top of the screen |
| **Growing up** | A pet that is played with a lot over weeks gets a slightly bigger body and a new trick; the stats window shows its age |
| **Pet cam** | The pet takes a "photo" (a PNG of the overlay) when it does a trick and you have not looked at it for a while |
| **Pet garden** | A flowerpot on a window top that the pet waters; flowers bloom over days of play |
| **Pet vs games** | The pet joins in: it bats a Hoops ball back, chases the Pong ball, hides from the Whack bugs, and steals a fish in Fishing |
| **Sound reactions** | The pet reacts to the game sounds (a buzzer scares it, a "best" cheer makes it dance) |
| **Nap spots** | The pet remembers its favourite window (by title) and goes back to sleep on it |
| **Voice tuning** | A volume slider for the pet alone, and quieter calls in the evening |

## Ideas for the overlay

| Idea | What happens |
|---|---|
| **Tournaments** | Best-of-N series across several games over the LAN, with a bracket in the lobby |
| **Ghost replays** | Your own best run of a race game plays back as a ghost the next time |
| **Spectator mode** | A third copy on the LAN watches two players' Pong or Air Hockey |
| **Achievements over LAN** | A pop-up on the co-worker's screen when you unlock one |
| **Themes from the wallpaper** | Pick the theme colours from the desktop wallpaper's dominant colours |
| **Native Wayland** | A layer-shell overlay once Avalonia grows a Wayland backend, for window tops of native Wayland apps too |
| **Controller support** | A gamepad for the paddle games |
| **A tiny level editor** | Place bumpers, cups and targets by hand and share the layout as a code |
