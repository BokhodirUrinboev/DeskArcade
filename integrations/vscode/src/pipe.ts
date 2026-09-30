// Writes lines to Desk Arcade's signal pipe: one line per connection, then the connection is closed, just as
// "deskarcade --signal" does. Plain Node, no VS Code.

import * as net from "net";

/**
 * Errors that mean "not there right now": the pipe does not exist (Desk Arcade is closed, or it is between two
 * connections: it takes one at a time and opens the pipe again after each), nobody is listening yet, or the
 * socket's queue is full. These are tried again; anything else is not.
 */
const RetryCodes = new Set(["ENOENT", "ECONNREFUSED", "EAGAIN"]);

export interface SendResult {
    ok: boolean;
    /** The error code of the last try when it failed: ENOENT, ECONNREFUSED, ETIMEDOUT... */
    code?: string;
}

export interface SendOptions {
    /** How many rounds over the paths before giving up (default 4). */
    attempts?: number;
    /** The pause after the first failed round, doubled after each one up to a quarter of a second (default 40 ms). */
    retryDelayMs?: number;
    /** How long one connection may take before it is dropped (default 2 s). */
    timeoutMs?: number;
    /** Keep trying until this time (Date.now() milliseconds), however many rounds that takes. */
    until?: number;
}

const sleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

/** Connects to one pipe path, writes the line and a line break, and closes. Never throws. */
export function writeLine(pipePath: string, line: string, timeoutMs = 2000): Promise<SendResult> {
    return new Promise(resolve => {
        let settled = false;
        const socket = net.createConnection({ path: pipePath });
        const settle = (result: SendResult) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            socket.destroy();
            resolve(result);
        };
        const timer = setTimeout(() => settle({ ok: false, code: "ETIMEDOUT" }), timeoutMs);
        // end() flushes the line and half-closes; its callback runs once the line has left this process
        socket.once("connect", () => socket.end(line + "\n", () => settle({ ok: true })));
        socket.on("error", (e: NodeJS.ErrnoException) => settle({ ok: false, code: e.code ?? "EUNKNOWN" }));
    });
}

/**
 * Sends one line to the first of `paths` that takes it. A path that is missing or busy is tried again a few
 * times, briefly, since the overlay reopens its pipe after every message.
 */
export async function sendLine(paths: readonly string[], line: string, options: SendOptions = {}): Promise<SendResult> {
    const attempts = options.attempts ?? 4;
    let delay = options.retryDelayMs ?? 40;
    let last: SendResult = { ok: false, code: "ENOENT" };
    for (let round = 0; round < attempts || Date.now() < (options.until ?? 0); round++) {
        if (round > 0) {
            await sleep(delay);
            delay = Math.min(delay * 2, 250);
        }
        for (const p of paths) {
            last = await writeLine(p, line, options.timeoutMs);
            if (last.ok || !RetryCodes.has(last.code ?? "")) return last;
        }
    }
    return last;
}

/**
 * Sends lines one at a time, in the order they were queued, so a quick task's "passed" can never overtake its
 * "running". When Desk Arcade is not running the lines are dropped quietly: one line in the log when it stops
 * answering, and one when it answers again.
 */
export class SignalQueue {
    private tail: Promise<unknown> = Promise.resolve();
    private reachable: boolean | undefined;
    private patientUntil = 0;

    /**
     * @param paths where the pipe may be, read for every line so a changed profile setting counts at once
     * @param log   a line for the output channel
     */
    constructor(
        private readonly paths: () => string[],
        private readonly log: (message: string) => void,
        private readonly options: SendOptions = {},
    ) {}

    /** Queues a line; resolves with whether Desk Arcade took it. */
    send(line: string): Promise<boolean> {
        const run = this.tail.then(() => this.deliver(line));
        this.tail = run.catch(() => undefined);
        return run;
    }

    /** Desk Arcade was just started: keep trying for up to `ms` milliseconds until its pipe is up. */
    waitForStart(ms: number): void {
        this.patientUntil = Date.now() + ms;
    }

    /** Resolves once every line queued so far has been tried. */
    async idle(): Promise<void> {
        await this.tail;
    }

    private async deliver(line: string): Promise<boolean> {
        const paths = this.paths();
        const result = await sendLine(paths, line, { ...this.options, until: this.patientUntil });
        if (result.ok) {
            if (this.reachable === false) this.log("Desk Arcade is answering again.");
            this.reachable = true;
            this.patientUntil = 0;
            return true;
        }
        if (this.reachable !== false)
            this.log(`Desk Arcade is not answering on ${paths.join(" or ")} (${result.code}); is it running? Nothing is sent until it is.`);
        this.reachable = false;
        return false;
    }
}
