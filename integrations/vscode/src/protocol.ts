// The lines Desk Arcade's signal pipe understands, and where that pipe is. Plain Node, no VS Code, so the unit
// tests can check every rule.
//
// The overlay reads one line per connection from the .NET named pipe "DeskArcade.Signal.v1" (src/Ipc.cs):
//   status:<state>|<name>|<note>|<source>   a lane on the scoreboard (the same as deskarcade --status)
//   show, hide, toggle, next               the plain signals (the same as deskarcade --signal)

import * as path from "path";

/** The pipe's name in src/Ipc.cs; a --profile copy adds ".NAME". */
export const PipeBase = "DeskArcade.Signal.v1";

/** Who lit the lane, the last field of a status line. */
export const Source = "vscode";

/** The longest lane name and note the overlay takes, in UTF-16 units as .NET counts them. */
export const MaxName = 40;
export const MaxNote = 80;

export type LaneState = "running" | "passed" | "failed" | "clear";
export type Signal = "show" | "hide" | "toggle" | "next";

/** The Flatpak's app id: its copy of Desk Arcade keeps the pipe in a folder of its own (packaging/flatpak/deskarcade.sh). */
export const FlatpakId = "com.imperiumgames.DeskArcade";

/**
 * A --profile name as Desk Arcade reads it (Program.cs): ASCII letters, digits, "-" and "_", at most 32 of them.
 * Cleaning it the same way here means the pipe name always matches.
 */
export function cleanProfile(profile: string | undefined): string {
    return Array.from(profile ?? "")
        .filter(c => /^[A-Za-z0-9_-]$/.test(c))
        .slice(0, 32)
        .join("");
}

/**
 * Text made safe for one field of a status line: "|" (the separator), line breaks and every other control
 * character become spaces, runs of spaces become one, and the ends are trimmed. Longer text is cut to `max`
 * UTF-16 units with an ellipsis, never in the middle of a character.
 */
export function cleanField(text: string | undefined, max: number): string {
    const flat = (text ?? "")
        .replace(/[|\p{Cc}\p{Zl}\p{Zp}]/gu, " ")
        .replace(/\s+/gu, " ")
        .trim();
    if (flat.length <= max) return flat;
    let cut = "";
    for (const c of flat) {
        if (cut.length + c.length > max - 1) break;
        cut += c;
    }
    return cut.trimEnd() + "…";
}

/** A status line: status:<state>|<name>|<note>|vscode. */
export function statusLine(state: LaneState, name: string, note = ""): string {
    return `status:${state}|${cleanField(name, MaxName)}|${cleanField(note, MaxNote)}|${Source}`;
}

/** The pipe's name with the profile's suffix, if any. */
export function pipeName(profile: string | undefined): string {
    const p = cleanProfile(profile);
    return p.length > 0 ? `${PipeBase}.${p}` : PipeBase;
}

/**
 * Where .NET puts a named pipe's Unix socket: Path.GetTempPath(), which is $TMPDIR or /tmp. (Node's os.tmpdir()
 * also looks at TMP and TEMP, which .NET does not, so it is read here the way .NET reads it.)
 */
export function dotnetTempDir(env: NodeJS.ProcessEnv): string {
    const tmp = env.TMPDIR && env.TMPDIR.length > 0 ? env.TMPDIR : "/tmp";
    return tmp.length > 1 ? tmp.replace(/\/+$/, "") : tmp;
}

/**
 * The paths to try for the pipe, the usual one first.
 *   Windows:         \\.\pipe\DeskArcade.Signal.v1[.profile]
 *   Linux and macOS: <tmp>/CoreFxPipe_DeskArcade.Signal.v1[.profile]
 * On Linux the Flatpak's copy keeps its socket in $XDG_RUNTIME_DIR/app/<id>, shared by every instance of the
 * sandbox, so that path comes second.
 */
export function pipePaths(profile: string | undefined, platform: NodeJS.Platform, env: NodeJS.ProcessEnv, uid?: number): string[] {
    const name = pipeName(profile);
    if (platform === "win32") return [`\\\\.\\pipe\\${name}`];
    const socket = `CoreFxPipe_${name}`;
    const paths = [path.posix.join(dotnetTempDir(env), socket)];
    if (platform === "linux") {
        const runtime = env.XDG_RUNTIME_DIR || (uid !== undefined ? `/run/user/${uid}` : "");
        if (runtime) paths.push(path.posix.join(runtime, "app", FlatpakId, socket));
    }
    return paths;
}
