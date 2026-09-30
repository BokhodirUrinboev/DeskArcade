// Runs inside the test copy of VS Code (see runTest.ts). A fake overlay listens on the pipe of a profile of its
// own, so a real Desk Arcade on the same PC hears nothing. The tests run real tasks and real debug sessions in the
// fixture folder and check the lines that reach the pipe.

import * as assert from "node:assert/strict";
import * as path from "path";
import * as vscode from "vscode";
import type { DeskArcadeApi } from "../../src/extension";
import { pipePaths } from "../../src/protocol";
import { FakeOverlay } from "../fakeOverlay";

const tests: Array<[string, () => Promise<void>]> = [];
const test = (name: string, fn: () => Promise<void>) => void tests.push([name, fn]);

let overlay: FakeOverlay;
let api: DeskArcadeApi;
let folder: vscode.WorkspaceFolder;

const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

function within<T>(promise: Promise<T>, ms: number, what: string): Promise<T> {
    let timer: NodeJS.Timeout | undefined;
    const late = new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new Error(`${what}: no end after ${ms} ms`)), ms)));
    return Promise.race([promise, late]).finally(() => clearTimeout(timer));
}

/** Changes a setting for the test run (undefined takes it out again). */
async function set(key: string, value: unknown): Promise<void> {
    await vscode.workspace.getConfiguration("deskArcade").update(key, value, vscode.ConfigurationTarget.Global);
}

/** The lines that reach the pipe while `action` runs: `expected` of them, or none within a moment when 0. */
async function linesDuring(expected: number, action: () => Promise<void>): Promise<string[]> {
    const from = overlay.lines.length;
    await action();
    await api.idle();
    if (expected > 0) await overlay.waitForLines(from + expected, 15_000);
    await sleep(expected > 0 ? 100 : 400);
    await api.idle();
    return overlay.lines.slice(from);
}

/** Resolves when the task's process ends. */
function taskEnd(label: string): Promise<void> {
    return new Promise(resolve => {
        const d = vscode.tasks.onDidEndTaskProcess(e => {
            if (e.execution.task.name !== label) return;
            d.dispose();
            resolve();
        });
    });
}

/** Runs a task from the fixture's tasks.json to its end, or with `stop`, stops it by hand once its process runs. */
async function runTask(label: string, stop = false): Promise<void> {
    const task = (await vscode.tasks.fetchTasks()).find(t => t.name === label);
    assert.ok(task, `no task "${label}" in the fixture`);
    const ended = taskEnd(label);
    const started = new Promise<void>(resolve => {
        const d = vscode.tasks.onDidStartTaskProcess(e => {
            if (e.execution.task.name !== label) return;
            d.dispose();
            resolve();
        });
    });
    const execution = await vscode.tasks.executeTask(task);
    if (stop) {
        await within(started, 30_000, `task "${label}" starting`);
        execution.terminate();
    }
    await within(ended, 30_000, `task "${label}"`);
}

/**
 * Debugs exit.js with js-debug, the Node debugger built into VS Code: until it exits with `code` after a moment,
 * or, with `stop`, until the test stops it by hand as the Stop button would, once the program runs.
 */
async function runDebug(name: string, code: number, stop = false): Promise<void> {
    let root: vscode.DebugSession | undefined;
    let programRuns: () => void = () => undefined;
    const running = new Promise<void>(resolve => (programRuns = resolve));
    const startListener = vscode.debug.onDidStartDebugSession(s => {
        if (!s.parentSession && s.name === name) root = s;
        if (s.parentSession?.name === name) programRuns(); // js-debug's child session: the process is up
    });
    const ended = new Promise<void>(resolve => {
        const d = vscode.debug.onDidTerminateDebugSession(s => {
            if (s.parentSession || s.name !== name) return;
            d.dispose();
            resolve();
        });
    });
    try {
        const started = await vscode.debug.startDebugging(folder, {
            type: "node",
            request: "launch",
            name,
            program: path.join(folder.uri.fsPath, "exit.js"),
            args: [String(code), stop ? "60000" : "300"],
            console: "internalConsole",
        });
        assert.ok(started, `debug session "${name}" did not start`);
        if (stop) {
            await within(running, 30_000, `debug session "${name}" starting its program`);
            assert.ok(root, `debug session "${name}" not seen`);
            await vscode.debug.stopDebugging(root);
        }
        await within(ended, 60_000, `debug session "${name}"`);
    } finally {
        startListener.dispose();
    }
}

// ---------------------------------------------------------------------------------------------------------- tasks

test("a task that passes: running, then passed", async () => {
    assert.deepEqual(await linesDuring(2, () => runTask("pass")), [
        "status:running|pass|node exit.js 0 300|vscode",
        "status:passed|pass||vscode",
    ]);
});

test("a task that fails: failed, with its exit code", async () => {
    assert.deepEqual(await linesDuring(2, () => runTask("fail")), [
        "status:running|fail|node exit.js 3 300|vscode",
        "status:failed|fail|exit code 3|vscode",
    ]);
});

test("a shell task in the build group", async () => {
    assert.deepEqual(await linesDuring(2, () => runTask("compile")), [
        "status:running|compile|node exit.js 0 300|vscode",
        "status:passed|compile||vscode",
    ]);
});

test("a task stopped by hand is cleared", async () => {
    assert.deepEqual(await linesDuring(2, () => runTask("long", true)), [
        "status:running|long|node exit.js 0 60000|vscode",
        "status:clear|long||vscode",
    ]);
});

test("a background task lights no lane", async () => {
    assert.deepEqual(await linesDuring(0, () => runTask("watch")), []);
});

test("build and test groups only", async () => {
    await set("tasks.which", "buildAndTest");
    try {
        assert.deepEqual(await linesDuring(0, () => runTask("pass")), []);
        assert.deepEqual(await linesDuring(2, () => runTask("unit tests")), [
            "status:running|unit tests|node exit.js 0 300|vscode",
            "status:passed|unit tests||vscode",
        ]);
    } finally {
        await set("tasks.which", undefined);
    }
});

test("tasks switched off", async () => {
    await set("tasks.enabled", false);
    try {
        assert.deepEqual(await linesDuring(0, () => runTask("pass")), []);
    } finally {
        await set("tasks.enabled", undefined);
    }
});

test("the lane prefix, with ${folder}", async () => {
    await set("lanePrefix", "${folder} · ");
    try {
        assert.deepEqual(await linesDuring(2, () => runTask("pass")), [
            "status:running|fixture · pass|node exit.js 0 300|vscode",
            "status:passed|fixture · pass||vscode",
        ]);
    } finally {
        await set("lanePrefix", undefined);
    }
});

// ------------------------------------------------------------------------------------------------ debug sessions

test("a debug session that exits with 0: running, then passed", async () => {
    assert.deepEqual(await linesDuring(2, () => runDebug("Debug ok", 0)), [
        "status:running|Debug ok|debugging|vscode",
        "status:passed|Debug ok||vscode",
    ]);
});

test("a debug session that exits with 2: failed, with the exit code", async () => {
    assert.deepEqual(await linesDuring(2, () => runDebug("Debug fails", 2)), [
        "status:running|Debug fails|debugging|vscode",
        "status:failed|Debug fails|exit code 2|vscode",
    ]);
});

test("a debug session stopped by hand is cleared", async () => {
    assert.deepEqual(await linesDuring(2, () => runDebug("Debug stopped", 0, true)), [
        "status:running|Debug stopped|debugging|vscode",
        "status:clear|Debug stopped||vscode",
    ]);
});

test("debug sessions switched off", async () => {
    await set("debug.enabled", false);
    try {
        assert.deepEqual(await linesDuring(0, () => runDebug("Debug quiet", 0)), []);
    } finally {
        await set("debug.enabled", undefined);
    }
});

// ------------------------------------------------------------------------------------------------------ commands

test("the commands are there", async () => {
    const all = await vscode.commands.getCommands(true);
    for (const id of ["deskArcade.playWhileItBuilds", "deskArcade.toggle", "deskArcade.next"]) assert.ok(all.includes(id), id);
});

test("Show or hide the overlay (the status-bar button) and Next game", async () => {
    assert.deepEqual(await linesDuring(1, async () => void (await vscode.commands.executeCommand("deskArcade.toggle"))), ["toggle"]);
    assert.deepEqual(await linesDuring(1, async () => void (await vscode.commands.executeCommand("deskArcade.next"))), ["next"]);
});

test("Play while it builds: show, then the default build task's lane", async () => {
    const lines = await linesDuring(3, async () => {
        const ended = taskEnd("compile");
        await vscode.commands.executeCommand("deskArcade.playWhileItBuilds");
        await within(ended, 30_000, "the default build task");
    });
    assert.deepEqual(lines, ["show", "status:running|compile|node exit.js 0 300|vscode", "status:passed|compile||vscode"]);
});

// ------------------------------------------------------------------------------------------ Desk Arcade not running

test("with Desk Arcade closed, tasks run as usual and lanes resume when it is back", async () => {
    const pipePath = overlay.pipePath;
    await overlay.stop();
    await runTask("fail");
    await api.idle();
    overlay = await FakeOverlay.start(pipePath);
    assert.deepEqual(await linesDuring(2, () => runTask("pass")), [
        "status:running|pass|node exit.js 0 300|vscode",
        "status:passed|pass||vscode",
    ]);
});

// --------------------------------------------------------------------------------------------------------- runner

export async function run(): Promise<void> {
    const extension = vscode.extensions.getExtension<DeskArcadeApi>("BokhodirUrinboev.desk-arcade");
    assert.ok(extension, "the extension is not loaded");
    api = await extension.activate();
    folder = vscode.workspace.workspaceFolders![0];

    const profile = `itest${process.pid}`;
    await set("profile", profile);
    overlay = await FakeOverlay.start(pipePaths(profile, process.platform, process.env, process.getuid?.())[0]);
    console.log(`Desk Arcade integration tests, VS Code ${vscode.version} on ${process.platform}, pipe ${overlay.pipePath}`);

    let failed = 0;
    for (const [name, fn] of tests) {
        const started = Date.now();
        try {
            await within(fn(), 120_000, name);
            console.log(`  ok   ${name} (${Date.now() - started} ms)`);
        } catch (e) {
            failed++;
            console.log(`  FAIL ${name}\n       ${e instanceof Error ? (e.stack ?? e.message).replace(/\n/g, "\n       ") : String(e)}`);
        }
    }
    await overlay.stop();
    await set("profile", undefined);
    console.log(`${tests.length - failed} passed, ${failed} failed`);
    if (failed > 0) throw new Error(`${failed} of ${tests.length} integration tests failed`);
}
