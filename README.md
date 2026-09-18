# Desk Arcade

Mini-games that play **on top of your desktop** in a transparent overlay. Shoot hoops while a build, a
deploy or Claude Code is working, and still see everything underneath.

**Platforms:** Windows 10/11 (x64, ARM64) · Ubuntu 22.04/24.04 (amd64, arm64) · other Linux via AppImage
or Flatpak · macOS 14+ (experimental) &nbsp;·&nbsp; **License:** [MIT](LICENSE) &nbsp;·&nbsp;
**Version:** 1.3.0

---

## Contents

- [Highlights](#highlights)
- [Games](#games)
- [Scoreboard, stats and achievements](#scoreboard-stats-and-achievements)
- [Controls](#controls)
- [Language](#language)
- [Installation](#installation)
- [Updating](#updating)
- [Claude Code integration](#claude-code-integration)
- [Building from source](#building-from-source)
- [Project structure](#project-structure)
- [Troubleshooting](#troubleshooting)
- [Third-party software](#third-party-software)
- [License](#license)

## Highlights

- **Your screen is a closed box.** Balls, pucks, bubbles, stones and cans bounce off the left, right and
  top edges, and arrows stick into them. Nothing flies off-screen and vanishes. The floor is the top of
  the taskbar or dock.
- **Clicks pass through.** The overlay covers one monitor, but only the game objects (ball, hoop, bow,
  scoreboard) take the mouse. Clicks anywhere else go straight to your windows.
- **Never steals focus.** You can keep typing in the terminal while you play.
- **Window tops are platforms.** Balls, bugs, pets and towers sit on the top edges of your windows and
  ride along when you drag one. You can turn this off in the tray menu.
- **Idle means idle.** When nothing is moving, rendering stops and CPU use drops to almost zero.
- **17 games, 47 achievements**, play-time stats, and English, Uzbek and Russian text.
- **No assets to download.** Every sound is synthesized and all artwork is drawn in code.

## Games

| Game | How to play |
|---|---|
| 🏀 **Hoops** | Grab the ball, flick it and let go. 2 points, 3 from long range, +1 for a swish, 1 for a dunk. Three in a row set the ball on fire (points ×2); at five the hoop moves. Drag the backboard to move the hoop. |
| 🎯 **Archery** | Press on the bow, drag backwards and release. 10 arrows a round. Rings score 2–10, balloons 5, gold balloons 15. From round 2 there is wind and the targets move. Right-drag the bow to move it. |
| ⚽ **Keepy-Uppy** | Click the ball to kick it up; where you click sets the direction. Don't let it touch the ground or a window top. Stars are +3, and gravity grows the longer you juggle. |
| ⛳ **Mini Golf** | Drag back from the ball and release to putt. The cup sits on the taskbar or a window top and follows that window. Nine holes, each starting where the last ended, scored against par. |
| 🐞 **Bug Squash** | Click the sleeping bug to start a 30-second round. Squash bugs crawling along the taskbar and window tops. Quick squashes build a combo up to ×4, golden bugs are worth 5, and ladybugs are features: squashing one costs 5. |
| 🥫 **Can Knockdown** | Throw the ball from behind the dashed line to knock the pyramid off its shelf. 3 balls a stack, the golden can is worth 3. Clearing a stack earns a bonus and a bigger stack; failing ends the game. |
| 🧱 **Brick Breaker** | Click the paddle to launch; while the ball is in play the paddle follows your mouse. The ball rebounds off the sides and top; the floor costs one of your 3 balls. Gold bricks are worth 10, steel bricks take two hits. |
| 🫧 **Bubble Pop** | Click the "pop me" bubble to start. Clicking a bubble splits it in two, and the smallest pop. Smaller bubbles score more, quick pops build a combo up to ×3, and leftover seconds become bonus points. |
| 🏒 **Air Hockey** | Drag your blue mallet on the left half and hit the puck into the right-hand goal while the CPU defends. The puck bounces off every other edge. First to 7 wins, and each win makes the CPU faster. |
| 🎯 **Clay Shooting** | Click the trap machine to start 15 pulls. Clays arc across the screen and bounce off the edges; click one to break it. Breaking two with one click is a DOUBLE, golden clays are worth 5, and a clay that lands is a miss. |
| 🔨 **Whack-a-Bug** | Bugs peek out from behind your window tops, the taskbar and the screen edges for a moment. Whack them: 1 point, fast bugs 2, golden 5, combo up to ×3. Ladybugs are features again: −5. |
| 🎲 **Plinko** | Click the strip at the top of the board to drop a disc through the pegs. Slots score 10 to 250, and the gold middle slot is the jackpot. Ten discs a round. Right-drag the board's header to move it. |
| 🗼 **Tower Stack** | Click the sliding block to drop it on the tower. The overhang is cut off, so the tower narrows; a perfect drop keeps the full width, and three in a row widen it. Miss completely and the game ends. |
| 🪨 **Slingshot** | Pull the stone back and let go to knock a tower of blocks off a window top. 3 stones a tower, 10 points a block, 50 for each spare stone. Clear the tower for a bigger one. |
| ♟️ **Checkers** | Click one of your pieces, then the square it should move to (for a multi-jump, the last square). Captures are compulsory and a piece reaching the far row is crowned. Play the CPU, or a co-worker over the LAN. Right-drag the board to move it. |
| ♞ **Chess** | Click a piece, then its square. Full rules: castling, en passant, check, mate, stalemate and the 50-move rule; pawns always promote to a queen. Play the CPU or a co-worker over the LAN. Right-drag the board to move it. |
| 🐱 **Desktop Pet** | Not a game: a cat that walks along your taskbar and window tops, follows the cursor, jumps between windows and sleeps when left alone. Click to pet it, drag to carry and throw it. |

> **Brick Breaker note:** while a ball is in play, a strip along the bottom of the screen takes the
> mouse so the paddle can follow it. It goes away as soon as the ball is lost or you switch games.

## Play with a co-worker

**Play over LAN** in the tray menu: one of you picks **Host a game** and the other picks **Join a game**. No
server or account is involved: the joiner finds the host on the local network (UDP port 47820). Two-player
games: **Air Hockey** (your co-worker's mallet replaces the CPU), **Checkers** and **Chess**. Each of you plays from
your own side of the screen.
Windows asks once whether to allow Desk Arcade on private networks; say yes on the hosting PC.

## Scoreboard, stats and achievements

The scoreboard is a small pill showing the game icon, score and best. **Click it** to open the full
board with a tab for every game; it shrinks back shortly after the mouse leaves. Drag it anywhere.

**Stats & achievements** in the tray menu (or `DeskArcade --signal stats`) opens a window with time
played and best score per game, and all 47 achievements with their progress. Stats live in
`stats.json` next to your settings and can be reset from the tray.

## Controls

| Shortcut | Action |
|---|---|
| **Ctrl+Alt+G** | Show or hide the overlay |
| **Ctrl+Alt+N** | Next game |
| **Ctrl+Alt+B** | Bring the ball, bow, paddle or pet to the cursor (a penalty stroke in Mini Golf) |
| Click the scoreboard | Open the game tabs |
| Drag the scoreboard | Move it |
| Tray icon, left-click | Show or hide |
| Tray icon menu | Game, volume, language, Claude Code options, updates, stats, monitor, reset, exit |

On macOS the shortcuts are **Control+Option+G/N/B**.

Every action is also available from the command line, which is useful for custom shortcuts and scripts:

```
DeskArcade --signal toggle|next|summon|show|hide|expand|stats|quit
```

## Language

Desk Arcade speaks **English**, **O'zbekcha** and **Русский**. It follows your system language by
default; pick one in **tray → Language**. The choice applies immediately, menus included.

## Installation

### Windows

Run the installer. It installs for the current user, without an administrator prompt, into
`%LOCALAPPDATA%\Programs\Desk Arcade`.

| File | For |
|---|---|
| `DeskArcade-Setup-<version>.exe` | x64, needs the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (setup offers the download page) |
| `DeskArcade-Setup-<version>-standalone.exe` | x64, runtime bundled |
| `DeskArcade-Setup-<version>-arm64.exe` | Windows on ARM, runtime bundled |

The installers are not code-signed yet, so Windows SmartScreen may warn on first run.

### Ubuntu (22.04 / 24.04)

```bash
sudo apt install ./deskarcade_<version>_amd64.deb   # or _arm64.deb
deskarcade            # or find "Desk Arcade" in the app grid
```

The package bundles its own .NET runtime; apt installs the few X11 libraries it needs. Remove it with
`sudo apt remove deskarcade`.

On Linux the overlay covers the monitor's working area (everything except the top bar and dock) rather
than the whole monitor, because GNOME moves a larger window to another monitor. The games only use the
working area anyway, so nothing changes on screen.

#### Ubuntu notes

| Topic | Xorg session | Wayland session (default) |
|---|---|---|
| Overlay, click-through, sound | ✅ | ✅ through XWayland |
| Stays on the monitor you pick (tray → **Move to next monitor**) | ✅ | ✅ |
| Bouncing on window tops | ✅ all windows | ⚠️ only X11 windows; native Wayland apps are invisible to it |
| Global shortcuts Ctrl+Alt+G/N/B | ✅ X11 grabs | ⚠️ through the desktop portal on GNOME 48+ and KDE Plasma 6: the first run asks you to allow the shortcuts. Untested on a real Wayland session; if they don't arrive, add custom shortcuts running `deskarcade --signal toggle`, `… next` and `… summon` |
| Tray menu | ✅ AppIndicator | ✅ AppIndicator |

### Other Linux distributions

- **AppImage:** `chmod +x DeskArcade-<version>-x86_64.AppImage` and run it. Ubuntu 22.04+ needs
  `libfuse2` (`sudo apt install libfuse2`), or run with `APPIMAGE_EXTRACT_AND_RUN=1`. "Start when I sign in"
  and the Claude Code hook config record the AppImage file itself, so re-run them after moving the file.
- **Flatpak:** a manifest is in [`packaging/flatpak/`](packaging/flatpak/) for building it yourself. It
  is not on Flathub.

### macOS 14+ (experimental)

Unzip `DeskArcade-<version>-macos-arm64.zip` (or `-x64`) and move `DeskArcade.app` to Applications.
The app is ad-hoc signed and not notarized, so the first launch needs **right-click → Open**.

> **Experimental:** the macOS layer compiles and is packaged by CI, but it has not been run on a Mac.
> Click-through, the window list, hotkeys and sound are unverified there. Reports are welcome.

### winget

Manifest templates live in [`packaging/winget/`](packaging/winget/). Desk Arcade is not in the winget
repository yet.

## Updating

Desk Arcade checks GitHub Releases once a day (switch it off in the tray) and offers the download when
a newer version exists. **Check for updates** in the tray menu does it immediately.

| Platform | How | What happens |
|---|---|---|
| Windows | Run the newer setup | Closes the running game, replaces the files in the same folder, keeps your shortcut and autostart choices, and restarts the game after silent updates. Downgrades are refused. |
| Ubuntu | `sudo apt install ./deskarcade_x.y.z_amd64.deb` | apt replaces the older version and closes the running game first. Start it again afterwards. |
| AppImage | Replace the file | Nothing else to do. |

Settings, high scores and stats are never touched by an update. They live in `%APPDATA%\DeskArcade` on
Windows and `~/.config/DeskArcade` on Linux and macOS.

## Claude Code integration

Desk Arcade can show whether Claude Code is working, done or waiting for you, chime when it finishes,
and tell you how long you waited.

1. Right-click the tray icon and choose **Copy Claude Code hook config**.
2. Merge the copied JSON into `~/.claude/settings.json`.

| Hook | Command | Scoreboard |
|---|---|---|
| `UserPromptSubmit` | `--signal working` | Amber, "Claude working · 3:12" counting up |
| `Stop` | `--signal done` | Green, "Claude done after 3:12" and a chime |
| `Notification` | `--signal attention` | Red, "Claude needs you" |

In **tray → Claude Code** you can also let the overlay appear by itself when Claude starts working, and
hide (pausing the game) when Claude finishes or needs you. Playing while Claude works earns the
"Pair programmer" achievement.

## Building from source

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Windows
installer also needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); the Ubuntu package needs
`dpkg-deb` (Ubuntu/Debian, WSL or a container).

```powershell
dotnet run                                  # run from source
dotnet test tests/DeskArcade.Tests          # unit tests

# Windows
.\build.ps1                                 # dist\DeskArcade.exe
.\build-installer.ps1                       # installer\Output\DeskArcade-Setup-<version>.exe
.\build-installer.ps1 -SelfContained        # ...-standalone.exe
.\build-installer.ps1 -Arch arm64 -SelfContained

# Linux
.\build-linux.ps1                           # from Windows: publishes, then packages inside WSL
packaging/linux/build.sh                    # on Ubuntu itself
packaging/linux/build-appimage.sh <publish-dir> <version> x86_64 dist-linux
```

Releases are built and published by GitHub Actions when a `vX.Y.Z` tag is pushed;
[docs/RELEASING.md](docs/RELEASING.md) covers every artifact, code signing, winget and Flathub.

### Development flags

| Flag | Purpose |
|---|---|
| `--game <id>` | Start on a specific game: `hoops`, `archery`, `juggle`, `golf`, `bugs`, `cans`, `bricks`, `bubbles`, `hockey`, `clay`, `whack`, `plinko`, `tower`, `slingshot`, `pet` |
| `--demo` | The current game plays itself, for smoke tests without touching the mouse |
| `--profile <name>` | Run an isolated copy with its own lock, signal channel and settings, alongside the installed game |

## Project structure

| Path | Contents |
|---|---|
| `src/Engine` | Lightweight game engine: `Vec2`, ball physics, window-top platforms, sprites, effects, `MiniGame` |
| `src/Games` | One class per game |
| `src/Platform` | The OS layer behind `IDesktopPlatform`, with `Windows`, `Linux` and `Mac` implementations |
| `src/Loc.cs`, `src/Strings.*.cs` | Translation lookup and the Uzbek and Russian tables |
| `src/Stats.cs`, `src/Achievements.cs`, `src/StatsWindow.cs` | Counters, achievements and the stats window |
| `src/OverlayWindow.cs` | The transparent, topmost window: frame loop, input, signals |
| `tests/DeskArcade.Tests` | Unit tests |
| `installer/`, `packaging/` | Inno Setup script, `.deb`, AppImage, Flatpak, winget and macOS packaging |
| `.github/workflows` | CI on every pull request, releases on version tags |

The UI is built with [Avalonia](https://avaloniaui.net/), so the games contain no OS-specific code.

### Adding a game

1. Subclass `MiniGame` and implement `Layout`, `Update`, `CollectHitShapes`, `PointerDown`, `Summon`
   and `Hud`, drawing into `Layer`.
2. Keep everything inside `Host.Arena`: it is a closed box, and window tops come from `Host.Platforms`.
3. `CollectHitShapes` decides which areas take the mouse; clicks everywhere else pass through.
4. Return `false` from `Update` when nothing moves, so the overlay can stop rendering.
5. Wrap visible text in `L.T` / `L.F` and add it to both translation tables; report counters through
   `Host.Stats`.
6. Register the game in `OverlayWindow.Start()` and add a hint in `HintFor`.

## Troubleshooting

| Problem | What to try |
|---|---|
| Nothing appears | Press **Ctrl+Alt+G** or left-click the tray icon. On a multi-monitor setup, use **Move to next monitor**. |
| A ball or the pet is out of reach | **Ctrl+Alt+B** brings it to the cursor; **Reset positions** restores the defaults. |
| Shortcuts do nothing on Ubuntu | On Wayland, allow them when the portal asks, or set up custom shortcuts as described above. |
| No sound on Ubuntu | Install `libpulse0`. It works with PulseAudio and PipeWire. |
| The game crashed | Details are in `crash.log` in the settings folder. |

## Third-party software

Desk Arcade is built on [Avalonia UI](https://github.com/AvaloniaUI/Avalonia), SkiaSharp, HarfBuzzSharp,
ANGLE, MicroCom.Runtime, Tmds.DBus.Protocol and the .NET runtime, all under permissive licenses. Full
versions, copyright notices and license texts are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which ships with every package.

## License

Desk Arcade is released under the [MIT License](LICENSE).

Copyright © 2026 Imperium Games.
