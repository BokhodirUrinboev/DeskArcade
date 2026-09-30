import * as assert from "node:assert/strict";
import { describe, test } from "node:test";
import { findProgram, installedPaths, launchArgs, launchEnv, pathCandidates } from "../../src/program";

describe("where the installers put Desk Arcade", () => {
    test("Windows: the per-user setup, the all-users setup, Scoop", () => {
        const env = { LOCALAPPDATA: "C:\\Users\\me\\AppData\\Local", ProgramFiles: "C:\\Program Files" };
        assert.deepEqual(installedPaths("win32", env, "C:\\Users\\me"), [
            "C:\\Users\\me\\AppData\\Local\\Programs\\Desk Arcade\\DeskArcade.exe",
            "C:\\Program Files\\Desk Arcade\\DeskArcade.exe",
            "C:\\Users\\me\\scoop\\apps\\deskarcade\\current\\DeskArcade.exe",
        ]);
        assert.equal(installedPaths("win32", { SCOOP: "D:\\scoop" }, "C:\\Users\\me")[0], "D:\\scoop\\apps\\deskarcade\\current\\DeskArcade.exe");
    });
    test("macOS: DeskArcade.app in Applications", () => {
        assert.deepEqual(installedPaths("darwin", {}, "/Users/me"), [
            "/Applications/DeskArcade.app/Contents/MacOS/DeskArcade",
            "/Users/me/Applications/DeskArcade.app/Contents/MacOS/DeskArcade",
        ]);
    });
    test("Linux: the .deb, then the Flatpak for the user or the system", () => {
        assert.deepEqual(installedPaths("linux", {}, "/home/me"), [
            "/usr/bin/deskarcade",
            "/home/me/.local/share/flatpak/exports/bin/com.imperiumgames.DeskArcade",
            "/var/lib/flatpak/exports/bin/com.imperiumgames.DeskArcade",
        ]);
        assert.equal(installedPaths("linux", { XDG_DATA_HOME: "/data" }, "/home/me")[1], "/data/flatpak/exports/bin/com.imperiumgames.DeskArcade");
    });
});

describe("the PATH", () => {
    test("Windows: DeskArcade.exe in each folder, quotes and blanks skipped", () => {
        assert.deepEqual(pathCandidates("win32", { PATH: 'C:\\Windows;;"C:\\Program Files\\Desk Arcade"; ' }), [
            "C:\\Windows\\DeskArcade.exe",
            "C:\\Program Files\\Desk Arcade\\DeskArcade.exe",
        ]);
    });
    test("Linux and macOS: deskarcade", () => {
        assert.deepEqual(pathCandidates("linux", { PATH: "/usr/local/bin:/usr/bin" }), ["/usr/local/bin/deskarcade", "/usr/bin/deskarcade"]);
        assert.deepEqual(pathCandidates("darwin", {}), []);
    });
});

describe("findProgram", () => {
    const env = { LOCALAPPDATA: "C:\\L", PATH: "C:\\bin" };
    const setup = "C:\\L\\Programs\\Desk Arcade\\DeskArcade.exe";

    test("the setting first, when the file is there", () => {
        const exists = (f: string) => f === "D:\\Games\\DeskArcade.exe" || f === setup;
        assert.equal(findProgram("D:\\Games\\DeskArcade.exe", "win32", env, "C:\\Users\\me", exists), "D:\\Games\\DeskArcade.exe");
    });
    test("a setting that points nowhere falls back to the installed copy", () => {
        assert.equal(findProgram("D:\\Gone.exe", "win32", env, "C:\\Users\\me", f => f === setup), setup);
    });
    test("a ~ in the setting is the home folder", () => {
        const appImage = "/home/me/Apps/DeskArcade-1.8.6-x86_64.AppImage";
        assert.equal(findProgram("~/Apps/DeskArcade-1.8.6-x86_64.AppImage", "linux", {}, "/home/me", f => f === appImage), appImage);
    });
    test("the PATH last", () => {
        assert.equal(findProgram("", "win32", env, "C:\\Users\\me", f => f === "C:\\bin\\DeskArcade.exe"), "C:\\bin\\DeskArcade.exe");
    });
    test("nothing when it is not installed", () => {
        assert.equal(findProgram(undefined, "linux", { PATH: "/usr/bin" }, "/home/me", () => false), undefined);
    });
});

describe("starting it", () => {
    test("the profile's copy when a profile is set", () => {
        assert.deepEqual(launchArgs(""), []);
        assert.deepEqual(launchArgs(undefined), []);
        assert.deepEqual(launchArgs("a3"), ["--profile", "a3"]);
        assert.deepEqual(launchArgs("a 3"), ["--profile", "a3"]);
    });
    test("without VS Code's own variables", () => {
        const env = launchEnv({ PATH: "/usr/bin", ELECTRON_RUN_AS_NODE: "1", VSCODE_PID: "42", VSCODE_IPC_HOOK: "x", DISPLAY: ":0" });
        assert.deepEqual(env, { PATH: "/usr/bin", DISPLAY: ":0" });
    });
});
