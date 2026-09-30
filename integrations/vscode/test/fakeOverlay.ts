// A stand-in for Desk Arcade's pipe server (src/Ipc.cs) for the unit tests: it listens on a real named pipe
// (Windows) or Unix socket, takes one connection at a time, reads its lines, and only then listens again, so a
// client that comes in between finds no pipe, as it would with the real overlay.

import * as fs from "fs";
import * as net from "net";
import * as os from "os";
import * as path from "path";

let counter = 0;

/** A pipe path of its own for each test. */
export function testPipePath(): string {
    const name = `deskarcade-test-${process.pid}-${++counter}`;
    return process.platform === "win32" ? `\\\\.\\pipe\\${name}` : path.join(os.tmpdir(), `${name}.sock`);
}

export class FakeOverlay {
    readonly lines: string[] = [];
    connections = 0;
    private server: net.Server | undefined;
    private stopped = false;
    private waiters: Array<() => void> = [];

    private constructor(readonly pipePath: string) {}

    /** Starts listening on `pipePath` (a fresh one by default). */
    static async start(pipePath = testPipePath()): Promise<FakeOverlay> {
        const overlay = new FakeOverlay(pipePath);
        await overlay.listen();
        return overlay;
    }

    /** Resolves once `count` lines have come in, or rejects after `timeoutMs`. */
    waitForLines(count: number, timeoutMs = 5000): Promise<string[]> {
        return new Promise((resolve, reject) => {
            const check = () => {
                if (this.lines.length >= count) {
                    clearTimeout(timer);
                    resolve(this.lines);
                    return true;
                }
                return false;
            };
            const timer = setTimeout(() => reject(new Error(`only ${this.lines.length} of ${count} lines: ${JSON.stringify(this.lines)}`)), timeoutMs);
            if (!check()) this.waiters.push(() => void check());
        });
    }

    async stop(): Promise<void> {
        this.stopped = true;
        const server = this.server;
        this.server = undefined;
        if (server) await new Promise<void>(resolve => server.close(() => resolve()));
    }

    private listen(): Promise<void> {
        if (process.platform !== "win32") fs.rmSync(this.pipePath, { force: true });
        return new Promise((resolve, reject) => {
            const server = net.createServer(socket => this.serve(server, socket));
            server.once("error", reject);
            server.listen(this.pipePath, () => resolve());
            this.server = server;
        });
    }

    /** One client: stop listening, read its lines to the end, then listen again. */
    private serve(server: net.Server, socket: net.Socket): void {
        this.connections++;
        server.close();
        this.server = undefined;
        let text = "";
        socket.setEncoding("utf8");
        socket.on("data", chunk => (text += chunk));
        socket.on("close", () => {
            for (const line of text.split(/\r?\n/)) if (line.trim().length > 0) this.lines.push(line.trim());
            this.waiters.forEach(w => w());
            if (!this.stopped) this.listen().catch(() => undefined);
        });
    }
}
