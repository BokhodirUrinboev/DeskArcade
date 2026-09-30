// Where Desk Arcade is installed, so "Show or hide the overlay", "Next game" and "Play while it builds" can start it
// when it is not running. Lanes never start it. Plain Node, no VS Code.

import * as path from "path";
import { cleanProfile, FlatpakId } from "./protocol";

/**
 * Where the installers put Desk Arcade, in the order to look:
 *   Windows: the setup's per-user folder (installer/DeskArcade.iss, {autopf} without admin rights), the same setup
 *            run for all users, and Scoop's
 *   macOS:   DeskArcade.app in /Applications or ~/Applications (the zip and the Homebrew cask)
 *   Linux:   the .deb's /usr/bin/deskarcade, and the Flatpak's launchers for a per-user or system-wide install
 * An AppImage can be anywhere: set deskArcade.programPath for it.
 */
export function installedPaths(platform: NodeJS.Platform, env: NodeJS.ProcessEnv, home: string): string[] {
    if (platform === "win32") {
        const w = path.win32;
        const list: string[] = [];
        if (env.LOCALAPPDATA) list.push(w.join(env.LOCALAPPDATA, "Programs", "Desk Arcade", "DeskArcade.exe"));
        if (env.ProgramFiles) list.push(w.join(env.ProgramFiles, "Desk Arcade", "DeskArcade.exe"));
        list.push(w.join(env.SCOOP || w.join(home, "scoop"), "apps", "deskarcade", "current", "DeskArcade.exe"));
        return list;
    }
    const p = path.posix;
    if (platform === "darwin") {
        const inApp = "DeskArcade.app/Contents/MacOS/DeskArcade";
        return [p.join("/Applications", inApp), p.join(home, "Applications", inApp)];
    }
    const data = env.XDG_DATA_HOME || p.join(home, ".local", "share");
    return [
        "/usr/bin/deskarcade",
        p.join(data, "flatpak", "exports", "bin", FlatpakId),
        p.join("/var/lib/flatpak/exports/bin", FlatpakId),
    ];
}

/** Desk Arcade on the PATH: the setup's "arcade" option puts its folder there on Windows. */
export function pathCandidates(platform: NodeJS.Platform, env: NodeJS.ProcessEnv): string[] {
    const p = platform === "win32" ? path.win32 : path.posix;
    const names = platform === "win32" ? ["DeskArcade.exe"] : ["deskarcade"];
    const dirs = (env.PATH ?? env.Path ?? "")
        .split(platform === "win32" ? ";" : ":")
        .map(d => d.trim().replace(/^"(.*)"$/, "$1"))
        .filter(d => d.length > 0);
    return dirs.flatMap(d => names.map(n => p.join(d, n)));
}

/**
 * The program to start: the deskArcade.programPath setting (a leading ~ is the home folder) if it is there, else
 * the first installed copy found, else the PATH's.
 */
export function findProgram(
    configured: string | undefined,
    platform: NodeJS.Platform,
    env: NodeJS.ProcessEnv,
    home: string,
    exists: (file: string) => boolean,
): string | undefined {
    const set = (configured ?? "").trim().replace(/^~(?=$|[\\/])/, home);
    const candidates = [...(set ? [set] : []), ...installedPaths(platform, env, home), ...pathCandidates(platform, env)];
    return candidates.find(file => exists(file));
}

/** The arguments to start it with: the profile's copy when a profile is set. */
export function launchArgs(profile: string | undefined): string[] {
    const p = cleanProfile(profile);
    return p.length > 0 ? ["--profile", p] : [];
}

/**
 * The environment to start it in: the extension host's, less the variables VS Code sets for itself. Desk Arcade
 * passes its environment on to what it runs (its own updates, a browser), and ELECTRON_RUN_AS_NODE there would
 * turn any Electron app into a bare Node.
 */
export function launchEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
    const clean: NodeJS.ProcessEnv = {};
    for (const [key, value] of Object.entries(env))
        if (key !== "ELECTRON_RUN_AS_NODE" && !key.startsWith("VSCODE_")) clean[key] = value;
    return clean;
}
