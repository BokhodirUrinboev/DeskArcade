// Which tasks and debug sessions light a lane, what the lane is called, and how it ends. Plain Node, no VS Code:
// extension.ts hands over the few facts these need.

import { cleanField, LaneState, MaxName, statusLine } from "./protocol";

/** The deskArcade.tasks.which setting. */
export type TaskFilter = "all" | "buildAndTest";

/** What the lanes need to know about a task. */
export interface TaskFacts {
    /** The id of the task's group in tasks.json ("build", "test", "clean", "rebuild"), if it has one. */
    group?: string;
    /** A watcher or server that runs until it is stopped ("isBackground" in tasks.json). */
    background: boolean;
}

/**
 * Whether a task lights a lane. A background task never does: a watcher neither passes nor fails, so its lane
 * would say "running" all day. With "buildAndTest" only the build and test groups count.
 */
export function taskCounts(task: TaskFacts, filter: TaskFilter): boolean {
    if (task.background) return false;
    if (filter === "buildAndTest") return task.group === "build" || task.group === "test";
    return true;
}

/**
 * A lane's name: the prefix setting, with ${folder} replaced by the workspace folder's name, then the name of the
 * task or debug session ("build", "npm: test", "Launch Program").
 */
export function laneName(prefix: string | undefined, name: string, folder?: string): string {
    const lead = (prefix ?? "").replace(/\$\{folder\}/g, folder ?? "");
    return cleanField(lead + name, MaxName) || "VS Code";
}

/** How a lane ends, and the exit code when there is one (a failed lane's note says it). */
export interface Ending {
    state: LaneState;
    exitCode?: number;
}

/** How a task ends: exit code 0 passed, any other code failed, and no code (stopped by hand) cleared. */
export function taskEnding(exitCode: number | undefined): Ending {
    if (exitCode === undefined) return { state: "clear" };
    return { state: exitCode === 0 ? "passed" : "failed", exitCode };
}

/** A Debug Adapter Protocol message, as far as DebugEnding reads it. */
export interface DapMessage {
    type?: string;
    event?: string;
    command?: string;
    success?: boolean;
    body?: { exitCode?: unknown; category?: string; output?: string };
}

/**
 * How a debug session ended, read from the Debug Adapter Protocol messages of the session and of its children
 * (js-debug starts a child session for each process):
 *   - stopped by hand: VS Code asked the adapter to disconnect or terminate before the adapter said the session
 *     had ended. The lane is cleared, like a task stopped by hand.
 *   - an exit code: from the "exited" event most adapters send (Python, C#, C++), or from the line js-debug, the
 *     Node debugger, writes instead ("Process exited with code 2", always in English, and only for codes above 0).
 *   - neither: the program ended by itself and nothing reported an error, which with js-debug means exit code 0.
 *   - a launch or attach that the adapter refused has failed.
 */
export class DebugEnding {
    private exitCode: number | undefined;
    private exitEvent = false;
    private stopped = false;
    private launchFailed = false;
    private readonly ended = new Set<string>();

    /** @param rootId the id of the session at the top, whose lane this is */
    constructor(private readonly rootId: string) {}

    /** A message from the debug adapter to VS Code, in the session `sessionId`. */
    fromAdapter(sessionId: string, message: DapMessage): void {
        if (message?.type === "response" && (message.command === "launch" || message.command === "attach") && message.success === false) {
            this.launchFailed = true;
        } else if (message?.type !== "event") {
            return;
        } else if (message.event === "terminated") {
            this.ended.add(sessionId);
        } else if (message.event === "exited" && typeof message.body?.exitCode === "number") {
            this.exitCode = message.body.exitCode;
            this.exitEvent = true;
        } else if (message.event === "output" && message.body?.category === "stderr" && sessionId === this.rootId && !this.exitEvent) {
            // js-debug's launcher writes this on the top session; the program's own output goes to the child
            const m = /^Process exited with code (-?\d+)\s*$/.exec(message.body.output ?? "");
            if (m) this.exitCode = Number(m[1]);
        }
    }

    /** A message from VS Code to the debug adapter, in the session `sessionId`. */
    toAdapter(sessionId: string, message: DapMessage): void {
        if (message?.type === "request" && (message.command === "disconnect" || message.command === "terminate") && !this.ended.has(sessionId))
            this.stopped = true;
    }

    get ending(): Ending {
        if (this.stopped) return { state: "clear" };
        if (this.exitCode !== undefined) return { state: this.exitCode === 0 ? "passed" : "failed", exitCode: this.exitCode };
        return { state: this.launchFailed ? "failed" : "passed" };
    }
}

/** A command or an argument as VS Code's ShellExecution and ProcessExecution hold it. */
type Part = string | { value: string };

/** The parts of a task's execution that say what it runs. */
export interface ExecutionFacts {
    /** ShellExecution: a whole command line... */
    commandLine?: string;
    /** ...or a command and its arguments. */
    command?: Part;
    /** ProcessExecution: the program. */
    process?: string;
    args?: Part[];
}

/** What a task runs, for the running lane's note: "dotnet build -c Release". Empty for a custom execution. */
export function commandText(execution: ExecutionFacts | undefined): string {
    if (!execution) return "";
    if (execution.commandLine) return execution.commandLine;
    const program = execution.command ?? execution.process;
    if (program === undefined) return "";
    const text = (p: Part) => (typeof p === "string" ? p : p.value);
    const quote = (s: string) => (/\s/.test(s) ? `"${s}"` : s);
    return [text(program), ...(execution.args ?? []).map(a => quote(text(a)))].join(" ");
}

/**
 * The lanes this window has lit and not yet ended, by key: a task's execution, or "debug:" and a debug session's
 * id. It turns starts and ends into status lines, and keeps two lanes running at once from sharing a name.
 */
export class LaneBook {
    private readonly lanes = new Map<unknown, string>();

    get size(): number {
        return this.lanes.size;
    }

    /** A lane starts; returns its "running" line. */
    begin(key: unknown, name: string, note = ""): string {
        this.lanes.delete(key);
        const lane = this.unique(name);
        this.lanes.set(key, lane);
        return statusLine("running", lane, note);
    }

    /**
     * A lane ends: returns its last line, "passed", "failed" (with `failNote` of the exit code as the note) or
     * "clear"; nothing for a key this book never started (a task that began before the extension did, or one that
     * did not count).
     */
    end(key: unknown, ending: Ending, failNote?: (exitCode: number) => string): string | undefined {
        const lane = this.lanes.get(key);
        if (lane === undefined) return undefined;
        this.lanes.delete(key);
        const note = ending.state === "failed" && ending.exitCode !== undefined && failNote ? failNote(ending.exitCode) : "";
        return statusLine(ending.state, lane, note);
    }

    /** "clear" lines for every lane still running, for when VS Code closes. */
    endAll(): string[] {
        const lines = [...this.lanes.values()].map(lane => statusLine("clear", lane));
        this.lanes.clear();
        return lines;
    }

    /** The name, or "name 2", "name 3"... while another running lane has it. */
    private unique(name: string): string {
        const taken = new Set(this.lanes.values());
        const base = cleanField(name, MaxName);
        if (!taken.has(base)) return base;
        for (let i = 2; ; i++) {
            const suffix = ` ${i}`;
            const candidate = cleanField(name, MaxName - suffix.length) + suffix;
            if (!taken.has(candidate)) return candidate;
        }
    }
}
