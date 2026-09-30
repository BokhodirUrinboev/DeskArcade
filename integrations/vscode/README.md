# Desk Arcade for VS Code

[Desk Arcade](https://github.com/BokhodirUrinboev/DeskArcade) is a free, open-source arcade of mini-games that play
in a transparent, click-through window over your desktop while a build, a test run or a coding agent works. This
extension connects it to VS Code:

- **Tasks light a lane on the scoreboard.** Start a build, a test run, an npm script or anything else in
  `tasks.json`, and Desk Arcade's scoreboard shows it running with a timer ("build · 1:12"), then **passed** or
  **failed** with the exit code. A task you stop by hand simply leaves the scoreboard.
- **Debug sessions too**, from start to end: passed or failed when the program's exit code is known, cleared when
  you press Stop.
- **A status-bar button** (the game controller) shows or hides the overlay.
- **Commands**, in the Command Palette under "Desk Arcade":
  - **Play while it builds**: shows the overlay and runs the default build task.
  - **Show or hide the overlay**
  - **Next game**

## Requirements

Desk Arcade itself, version 1.8.6 or newer, installed and running on the same computer: the Windows installer, Scoop,
the Ubuntu `.deb`, the AppImage, the Flatpak or the macOS app from the
[releases page](https://github.com/BokhodirUrinboev/DeskArcade/releases). The extension needs nothing else.

If Desk Arcade is not running, the extension stays quiet: tasks and debug sessions run as usual and nothing pops up.
**Show or hide the overlay**, **Next game** and **Play while it builds** start Desk Arcade when they find it in the
usual place; set **Desk Arcade: Program path** if yours is elsewhere (an AppImage, for example).

## Settings

| Setting | Default | What it does |
|---|---|---|
| `deskArcade.tasks.enabled` | on | A lane for each task that runs a process. Background tasks (watchers) never get one: they never pass or fail |
| `deskArcade.tasks.which` | `all` | `all` tasks, or `buildAndTest`: only tasks in the build and test groups |
| `deskArcade.debug.enabled` | on | A lane for each debug session |
| `deskArcade.lanePrefix` | empty | Text before each lane's name, such as `VS Code · `; `${folder}` becomes the workspace folder's name (`${folder} · ` gives `api · build`) |
| `deskArcade.statusBar` | on | The status-bar button |
| `deskArcade.profile` | empty | The `--profile` name of the Desk Arcade copy to talk to |
| `deskArcade.programPath` | empty | Where Desk Arcade is, if not in the usual place; used only to start it from the commands above |

## How it works, and privacy

The extension writes one line at a time to Desk Arcade's signal pipe on your computer (the same channel as
`deskarcade --signal` and `deskarcade --status`) and closes it. It uses no network, collects nothing and sends
nothing anywhere else. The **Desk Arcade** output channel says when Desk Arcade stops or starts answering.

It asks VS Code to run it on the computer with the desktop, where Desk Arcade runs, also when the workspace is remote
(WSL, SSH, a container). It works in VS Code 1.90 or newer and in editors built on it that install from
[Open VSX](https://open-vsx.org), such as VSCodium and Cursor.

The text is in English, Russian and Uzbek, following VS Code's display language.

## Licence

MIT. Source, issues and the rest of Desk Arcade:
[github.com/BokhodirUrinboev/DeskArcade](https://github.com/BokhodirUrinboev/DeskArcade).
