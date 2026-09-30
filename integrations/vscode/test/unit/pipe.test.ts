// The pipe client against a real named pipe (Windows) or Unix socket (Linux, macOS), served by FakeOverlay the way
// Desk Arcade serves it: one connection at a time, with no pipe in between.

import * as assert from "node:assert/strict";
import { after, describe, test } from "node:test";
import { sendLine, SignalQueue, writeLine } from "../../src/pipe";
import { FakeOverlay, testPipePath } from "../fakeOverlay";

const running: FakeOverlay[] = [];
async function overlay(pipePath?: string): Promise<FakeOverlay> {
    const o = await FakeOverlay.start(pipePath);
    running.push(o);
    return o;
}
after(() => Promise.all(running.map(o => o.stop())));

const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

describe("writeLine", () => {
    test("writes one line and closes the connection", async () => {
        const o = await overlay();
        assert.deepEqual(await writeLine(o.pipePath, "toggle"), { ok: true });
        assert.deepEqual(await o.waitForLines(1), ["toggle"]);
        assert.equal(o.connections, 1);
    });
    test("a missing pipe is ENOENT, not an exception", async () => {
        const result = await writeLine(testPipePath(), "show");
        assert.equal(result.ok, false);
        assert.equal(result.code, "ENOENT");
    });
    test("keeps the text as it is: UTF-8, capitals and all", async () => {
        const o = await overlay();
        const line = "status:running|Сборка · Qurilish|dotnet build -c Release|vscode";
        assert.equal((await writeLine(o.pipePath, line)).ok, true);
        assert.deepEqual(await o.waitForLines(1), [line]);
    });
});

describe("sendLine", () => {
    test("gives up quickly when Desk Arcade is not running", async () => {
        const started = Date.now();
        const result = await sendLine([testPipePath()], "show");
        assert.equal(result.ok, false);
        assert.equal(result.code, "ENOENT");
        assert.ok(Date.now() - started < 1500, `took ${Date.now() - started} ms`);
    });
    test("tries again while the pipe is not up yet", async () => {
        const pipePath = testPipePath();
        const late = sleep(120).then(() => overlay(pipePath));
        const result = await sendLine([pipePath], "next");
        assert.equal(result.ok, true);
        assert.deepEqual(await (await late).waitForLines(1), ["next"]);
    });
    test("goes on to the next path when the first is missing", async () => {
        const o = await overlay();
        assert.equal((await sendLine([testPipePath(), o.pipePath], "hide")).ok, true);
        assert.deepEqual(await o.waitForLines(1), ["hide"]);
    });
    test("keeps trying until the time given", async () => {
        const pipePath = testPipePath();
        const late = sleep(900).then(() => overlay(pipePath));
        const result = await sendLine([pipePath], "show", { attempts: 1, until: Date.now() + 5000 });
        assert.equal(result.ok, true);
        assert.deepEqual(await (await late).waitForLines(1), ["show"]);
    });
});

describe("SignalQueue", () => {
    test("lines arrive one per connection, in the order they were sent", async () => {
        const o = await overlay();
        const q = new SignalQueue(() => [o.pipePath], () => undefined);
        const lines = Array.from({ length: 12 }, (_, i) => `status:${i % 2 ? "passed" : "running"}|lane ${i}||vscode`);
        const results = await Promise.all(lines.map(line => q.send(line)));
        assert.ok(results.every(r => r));
        assert.deepEqual(await o.waitForLines(lines.length), lines);
        assert.equal(o.connections, lines.length);
    });
    test("with Desk Arcade closed: false, no exception, one line in the log", async () => {
        const log: string[] = [];
        const q = new SignalQueue(() => [testPipePath()], m => log.push(m));
        assert.equal(await q.send("status:running|build||vscode"), false);
        assert.equal(await q.send("status:passed|build||vscode"), false);
        assert.equal(log.length, 1);
        assert.match(log[0], /not answering.*ENOENT/);
    });
    test("says once when Desk Arcade answers again", async () => {
        const pipePath = testPipePath();
        const log: string[] = [];
        const q = new SignalQueue(() => [pipePath], m => log.push(m));
        assert.equal(await q.send("show"), false);
        const o = await overlay(pipePath);
        assert.equal(await q.send("show"), true);
        assert.equal(await q.send("next"), true);
        assert.deepEqual(await o.waitForLines(2), ["show", "next"]);
        assert.equal(log.length, 2);
        assert.match(log[1], /answering again/);
    });
    test("waits for Desk Arcade after starting it", async () => {
        const pipePath = testPipePath();
        const q = new SignalQueue(() => [pipePath], () => undefined);
        q.waitForStart(5000);
        const late = sleep(1000).then(() => overlay(pipePath));
        const sent = [q.send("show"), q.send("status:running|build||vscode")];
        assert.deepEqual(await Promise.all(sent), [true, true]);
        assert.deepEqual(await (await late).waitForLines(2), ["show", "status:running|build||vscode"]);
    });
    test("reads the paths for every line, so a new profile counts at once", async () => {
        const a = await overlay();
        const b = await overlay();
        let current = a.pipePath;
        const q = new SignalQueue(() => [current], () => undefined);
        await q.send("toggle");
        current = b.pipePath;
        await q.send("next");
        assert.deepEqual(await a.waitForLines(1), ["toggle"]);
        assert.deepEqual(await b.waitForLines(1), ["next"]);
    });
    test("idle waits for everything queued", async () => {
        const o = await overlay();
        const q = new SignalQueue(() => [o.pipePath], () => undefined);
        void q.send("a");
        void q.send("b");
        await q.idle();
        assert.deepEqual(await o.waitForLines(2, 1000), ["a", "b"]);
    });
});
