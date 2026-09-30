// Desk Arcade for VS Code: tasks and debug sessions light a lane on the overlay's scoreboard, and a status-bar
// button shows or hides the overlay. Everything goes through Desk Arcade's signal pipe on this PC; the extension
// uses no network.

import { spawn } from "child_process";
import * as fs from "fs";
import * as os from "os";
import * as vscode from "vscode";
import { commandText, DapMessage, DebugEnding, ExecutionFacts, LaneBook, laneName, TaskFilter, taskCounts, taskEnding } from "./lanes";
import { SignalQueue } from "./pipe";
import { findProgram, launchArgs, launchEnv } from "./program";
import { pipePaths, Signal } from "./protocol";

/** How long to keep trying the pipe after starting Desk Arcade: a cold .NET start can take a few seconds. */
const StartPatienceMs = 20_000;

interface Settings {
    tasks: boolean;
    which: TaskFilter;
    debug: boolean;
    prefix: string;
    statusBar: boolean;
    profile: string;
    programPath: string;
}

/** The settings, read fresh each time so a change counts from the next task on. */
function settings(): Settings {
    const c = vscode.workspace.getConfiguration("deskArcade");
    return {
        tasks: c.get<boolean>("tasks.enabled", true),
        which: c.get<TaskFilter>("tasks.which", "all"),
        debug: c.get<boolean>("debug.enabled", true),
        prefix: c.get<string>("lanePrefix", ""),
        statusBar: c.get<boolean>("statusBar", true),
        profile: c.get<string>("profile", ""),
        programPath: c.get<string>("programPath", ""),
    };
}

/** The folder a task belongs to, for ${folder} in the lane prefix. */
function folderOf(scope: vscode.Task["scope"]): string | undefined {
    return typeof scope === "object" ? scope.name : vscode.workspace.name;
}

/** The session at the top: js-debug starts a child session per process, which is part of its parent's lane. */
function rootOf(session: vscode.DebugSession): vscode.DebugSession {
    while (session.parentSession) session = session.parentSession;
    return session;
}

const debugKey = (session: vscode.DebugSession) => `debug:${session.id}`;

function isFile(file: string): boolean {
    try {
        return fs.statSync(file).isFile();
    } catch {
        return false;
    }
}

let queue: SignalQueue | undefined;
let lanes: LaneBook | undefined;

/** What the extension hands back: only the integration tests use it, to wait for the lines to go out. */
export interface DeskArcadeApi {
    idle(): Promise<void>;
}

export function activate(context: vscode.ExtensionContext): DeskArcadeApi {
    const output = vscode.window.createOutputChannel("Desk Arcade", { log: true });
    const log = (message: string) => output.info(message);
    const q = (queue = new SignalQueue(() => pipePaths(settings().profile, process.platform, process.env, process.getuid?.()), log));
    const book = (lanes = new LaneBook());
    const send = (line: string | undefined) => {
        if (line) void q.send(line);
    };
    const failNote = (exitCode: number) => vscode.l10n.t("exit code {0}", exitCode);

    // Tasks: build, test, npm scripts, anything in tasks.json that runs a process.
    context.subscriptions.push(
        vscode.tasks.onDidStartTaskProcess(e => {
            const s = settings();
            const task = e.execution.task;
            if (!s.tasks || !taskCounts({ group: task.group?.id, background: task.isBackground }, s.which)) return;
            const name = laneName(s.prefix, task.name, folderOf(task.scope));
            send(book.begin(e.execution, name, commandText(task.execution as ExecutionFacts | undefined)));
        }),
        // the exit code is undefined when the task was stopped by hand: the lane is cleared, not failed
        vscode.tasks.onDidEndTaskProcess(e => send(book.end(e.execution, taskEnding(e.exitCode), failNote))),
    );

    // Debug sessions: a lane from start to end, passed or failed by what the debug adapter says (see DebugEnding).
    const endings = new Map<string, DebugEnding>();
    const endingOf = (session: vscode.DebugSession) => {
        const root = rootOf(session);
        let ending = endings.get(root.id);
        if (!ending) endings.set(root.id, (ending = new DebugEnding(root.id)));
        return ending;
    };
    context.subscriptions.push(
        vscode.debug.onDidStartDebugSession(session => {
            const s = settings();
            if (!s.debug || session.parentSession) return;
            const name = laneName(s.prefix, session.name, session.workspaceFolder?.name ?? vscode.workspace.name);
            send(book.begin(debugKey(session), name, vscode.l10n.t("debugging")));
        }),
        vscode.debug.onDidTerminateDebugSession(session => {
            if (session.parentSession) return;
            const ending = endings.get(session.id);
            endings.delete(session.id);
            send(book.end(debugKey(session), ending?.ending ?? { state: "clear" }, failNote));
        }),
        vscode.debug.registerDebugAdapterTrackerFactory("*", {
            createDebugAdapterTracker: session => {
                const ending = endingOf(session);
                return {
                    onDidSendMessage: (message: DapMessage) => ending.fromAdapter(session.id, message),
                    onWillReceiveMessage: (message: DapMessage) => ending.toAdapter(session.id, message),
                };
            },
        }),
    );

    /**
     * Sends a plain signal. When nothing answers and `start` is set, starts Desk Arcade instead (it comes up
     * shown) and gives it time to open its pipe before the lines that follow.
     */
    const signal = async (sig: Signal, start: boolean) => {
        if ((await q.send(sig)) || !start) return;
        const s = settings();
        const program = findProgram(s.programPath, process.platform, process.env, os.homedir(), isFile);
        if (!program) {
            log("Desk Arcade was not found. Install it from https://github.com/BokhodirUrinboev/DeskArcade/releases, or set deskArcade.programPath.");
            vscode.window.setStatusBarMessage(vscode.l10n.t("Desk Arcade is not running"), 5000);
            return;
        }
        log(`Starting ${program}`);
        try {
            const child = spawn(program, launchArgs(s.profile), { detached: true, stdio: "ignore", env: launchEnv(process.env) });
            child.on("error", e => log(`Could not start ${program}: ${e.message}`));
            child.unref();
            q.waitForStart(StartPatienceMs);
        } catch (e) {
            log(`Could not start ${program}: ${e instanceof Error ? e.message : String(e)}`);
        }
    };

    context.subscriptions.push(
        vscode.commands.registerCommand("deskArcade.toggle", () => signal("toggle", true)),
        vscode.commands.registerCommand("deskArcade.next", () => signal("next", true)),
        vscode.commands.registerCommand("deskArcade.playWhileItBuilds", async () => {
            await signal("show", true);
            // the default build task, or VS Code's own prompt to pick or configure one; its lane comes from the task events
            await vscode.commands.executeCommand("workbench.action.tasks.build");
        }),
    );

    // The status-bar button.
    const button = vscode.window.createStatusBarItem("deskArcade.toggle", vscode.StatusBarAlignment.Right, 10);
    button.name = "Desk Arcade";
    button.text = "$(game)";
    button.tooltip = vscode.l10n.t("Desk Arcade: show or hide the overlay");
    button.accessibilityInformation = { label: button.tooltip };
    button.command = "deskArcade.toggle";
    const placeButton = () => (settings().statusBar ? button.show() : button.hide());
    placeButton();
    context.subscriptions.push(
        button,
        output,
        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration("deskArcade.statusBar")) placeButton();
        }),
    );

    return { idle: () => q.idle() };
}

/** VS Code is closing this window: clear the lanes still running, rather than leave them "running" until they go grey. */
export async function deactivate(): Promise<void> {
    if (!queue || !lanes) return;
    for (const line of lanes.endAll()) void queue.send(line);
    await Promise.race([queue.idle(), new Promise(resolve => setTimeout(resolve, 1500))]);
}
