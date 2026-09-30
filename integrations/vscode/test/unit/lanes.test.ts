import * as assert from "node:assert/strict";
import { describe, test } from "node:test";
import { commandText, DebugEnding, LaneBook, laneName, taskCounts, taskEnding } from "../../src/lanes";

describe("which tasks count", () => {
    test("all: every task that ends by itself", () => {
        assert.equal(taskCounts({ group: "build", background: false }, "all"), true);
        assert.equal(taskCounts({ group: "test", background: false }, "all"), true);
        assert.equal(taskCounts({ group: "clean", background: false }, "all"), true);
        assert.equal(taskCounts({ background: false }, "all"), true);
    });
    test("build and test groups only", () => {
        assert.equal(taskCounts({ group: "build", background: false }, "buildAndTest"), true);
        assert.equal(taskCounts({ group: "test", background: false }, "buildAndTest"), true);
        assert.equal(taskCounts({ group: "clean", background: false }, "buildAndTest"), false);
        assert.equal(taskCounts({ group: "rebuild", background: false }, "buildAndTest"), false);
        assert.equal(taskCounts({ background: false }, "buildAndTest"), false);
    });
    test("never a background task: a watcher neither passes nor fails", () => {
        assert.equal(taskCounts({ group: "build", background: true }, "all"), false);
        assert.equal(taskCounts({ group: "build", background: true }, "buildAndTest"), false);
    });
});

describe("lane names", () => {
    test("the task's name, after the prefix", () => {
        assert.equal(laneName("", "build"), "build");
        assert.equal(laneName("VS Code · ", "npm: test"), "VS Code · npm: test");
        assert.equal(laneName(undefined, "build"), "build");
    });
    test("${folder} in the prefix is the workspace folder", () => {
        assert.equal(laneName("${folder} · ", "build", "api"), "api · build");
        assert.equal(laneName("${folder}/${folder}: ", "test", "web"), "web/web: test");
        assert.equal(laneName("${folder} · ", "build"), "· build");
    });
    test("cut to 40 and cleaned; never empty", () => {
        assert.equal(laneName("", "x".repeat(60)).length, 40);
        assert.equal(laneName("a|", "b\nc"), "a b c");
        assert.equal(laneName("", " | "), "VS Code");
    });
});

describe("how a task ends", () => {
    test("0 passed, other codes failed, none (stopped by hand) cleared", () => {
        assert.deepEqual(taskEnding(0), { state: "passed", exitCode: 0 });
        assert.deepEqual(taskEnding(1), { state: "failed", exitCode: 1 });
        assert.deepEqual(taskEnding(-1), { state: "failed", exitCode: -1 });
        assert.deepEqual(taskEnding(3221225786), { state: "failed", exitCode: 3221225786 });
        assert.deepEqual(taskEnding(undefined), { state: "clear" });
    });
});

describe("how a debug session ends", () => {
    const event = (name: string, body?: object) => ({ type: "event", event: name, body });
    const request = (command: string) => ({ type: "request", command });

    test("an adapter's exited event: 0 passed, other codes failed", () => {
        const ok = new DebugEnding("1");
        ok.fromAdapter("1", event("exited", { exitCode: 0 }));
        ok.fromAdapter("1", event("terminated"));
        ok.toAdapter("1", request("disconnect"));
        assert.deepEqual(ok.ending, { state: "passed", exitCode: 0 });
        const bad = new DebugEnding("1");
        bad.fromAdapter("1", event("exited", { exitCode: 134 }));
        assert.deepEqual(bad.ending, { state: "failed", exitCode: 134 });
    });
    test("js-debug: its line on the top session gives the code", () => {
        // what js-debug sent for "node exit.js 2": the child ends, then the launcher's line on the parent
        const e = new DebugEnding("parent");
        e.fromAdapter("child", event("thread", { reason: "exited", threadId: 1 }));
        e.fromAdapter("child", event("terminated"));
        e.toAdapter("child", request("disconnect"));
        e.fromAdapter("parent", event("output", { category: "stderr", output: "Process exited with code 2\r\n" }));
        e.fromAdapter("parent", event("terminated", { restart: false }));
        e.toAdapter("parent", request("disconnect"));
        assert.deepEqual(e.ending, { state: "failed", exitCode: 2 });
    });
    test("js-debug: no line means exit code 0", () => {
        const e = new DebugEnding("parent");
        e.fromAdapter("child", event("terminated"));
        e.toAdapter("child", request("disconnect"));
        e.fromAdapter("parent", event("terminated"));
        assert.deepEqual(e.ending, { state: "passed" });
    });
    test("the program's own output cannot pass for the launcher's line", () => {
        const e = new DebugEnding("parent");
        e.fromAdapter("child", event("output", { category: "stderr", output: "Process exited with code 7\n" }));
        e.fromAdapter("parent", event("output", { category: "stdout", output: "Process exited with code 8\n" }));
        e.fromAdapter("parent", event("output", { category: "stderr", output: "Process exited with code 9 (not really)\n" }));
        assert.deepEqual(e.ending, { state: "passed" });
    });
    test("a real exited event wins over the line, whichever comes first", () => {
        const before = new DebugEnding("1");
        before.fromAdapter("1", event("output", { category: "stderr", output: "Process exited with code 5\n" }));
        before.fromAdapter("1", event("exited", { exitCode: 0 }));
        assert.deepEqual(before.ending, { state: "passed", exitCode: 0 });
        const after = new DebugEnding("1");
        after.fromAdapter("1", event("exited", { exitCode: 0 }));
        after.fromAdapter("1", event("output", { category: "stderr", output: "Process exited with code 5\n" }));
        assert.deepEqual(after.ending, { state: "passed", exitCode: 0 });
    });
    test("stopped by hand: cleared, whatever code follows", () => {
        const e = new DebugEnding("parent");
        e.toAdapter("parent", request("disconnect"));
        e.fromAdapter("parent", event("output", { category: "stderr", output: "Process exited with code 1\r\n" }));
        e.fromAdapter("parent", event("terminated"));
        assert.deepEqual(e.ending, { state: "clear" });
        const child = new DebugEnding("parent");
        child.toAdapter("child", request("terminate"));
        assert.deepEqual(child.ending, { state: "clear" });
    });
    test("a launch the adapter refused has failed", () => {
        const e = new DebugEnding("1");
        e.fromAdapter("1", { type: "response", command: "launch", success: false });
        e.fromAdapter("1", event("terminated"));
        assert.deepEqual(e.ending, { state: "failed" });
        const ok = new DebugEnding("1");
        ok.fromAdapter("1", { type: "response", command: "launch", success: true });
        assert.deepEqual(ok.ending, { state: "passed" });
    });
    test("odd messages change nothing", () => {
        const e = new DebugEnding("1");
        e.fromAdapter("1", event("exited", { exitCode: "3" }));
        e.fromAdapter("1", event("exited"));
        e.fromAdapter("1", undefined as never);
        e.toAdapter("1", undefined as never);
        e.toAdapter("1", request("next"));
        assert.deepEqual(e.ending, { state: "passed" });
    });
});

describe("what a task runs", () => {
    test("a shell command line", () => assert.equal(commandText({ commandLine: "dotnet build -c Release" }), "dotnet build -c Release"));
    test("a shell command with arguments, quoted or not", () => {
        assert.equal(commandText({ command: "npm", args: ["run", "build"] }), "npm run build");
        assert.equal(commandText({ command: { value: "make" }, args: [{ value: "-j8" }, "all targets"] }), 'make -j8 "all targets"');
    });
    test("a process and its arguments", () => assert.equal(commandText({ process: "node", args: ["-e", "process.exit(0)"] }), "node -e process.exit(0)"));
    test("nothing for a custom execution", () => {
        assert.equal(commandText(undefined), "");
        assert.equal(commandText({}), "");
    });
});

describe("LaneBook", () => {
    const failNote = (code: number) => `exit code ${code}`;

    test("a task that passes", () => {
        const book = new LaneBook();
        const key = {};
        assert.equal(book.begin(key, "build", "dotnet build"), "status:running|build|dotnet build|vscode");
        assert.equal(book.size, 1);
        assert.equal(book.end(key, taskEnding(0), failNote), "status:passed|build||vscode");
        assert.equal(book.size, 0);
    });
    test("a task that fails says its exit code", () => {
        const book = new LaneBook();
        book.begin("t", "test");
        assert.equal(book.end("t", taskEnding(3), failNote), "status:failed|test|exit code 3|vscode");
    });
    test("a task stopped by hand is cleared", () => {
        const book = new LaneBook();
        book.begin("t", "serve");
        assert.equal(book.end("t", taskEnding(undefined), failNote), "status:clear|serve||vscode");
    });
    test("a failure with no exit code has no note", () => {
        const book = new LaneBook();
        book.begin("debug:1", "Launch Program");
        assert.equal(book.end("debug:1", { state: "failed" }, failNote), "status:failed|Launch Program||vscode");
    });
    test("nothing for a lane it never started, or one already ended", () => {
        const book = new LaneBook();
        assert.equal(book.end("never", taskEnding(1)), undefined);
        book.begin("k", "build");
        book.end("k", taskEnding(0));
        assert.equal(book.end("k", taskEnding(0)), undefined);
    });
    test("two lanes running at once never share a name", () => {
        const book = new LaneBook();
        assert.equal(book.begin("a", "build"), "status:running|build||vscode");
        assert.equal(book.begin("b", "build"), "status:running|build 2||vscode");
        assert.equal(book.begin("c", "build"), "status:running|build 3||vscode");
        assert.equal(book.end("b", taskEnding(0)), "status:passed|build 2||vscode");
        assert.equal(book.begin("d", "build"), "status:running|build 2||vscode");
        const long = "y".repeat(45);
        book.begin("e", long);
        const [, name] = book.begin("f", long).split("|");
        assert.equal(name.length, 40);
        assert.ok(name.endsWith("… 2"));
    });
    test("the same key twice is one lane", () => {
        const book = new LaneBook();
        book.begin("k", "build");
        assert.equal(book.begin("k", "build"), "status:running|build||vscode");
        assert.equal(book.size, 1);
    });
    test("closing VS Code clears every running lane", () => {
        const book = new LaneBook();
        book.begin("a", "build");
        book.begin("b", "Launch Program");
        assert.deepEqual(book.endAll(), ["status:clear|build||vscode", "status:clear|Launch Program||vscode"]);
        assert.equal(book.size, 0);
        assert.deepEqual(book.endAll(), []);
    });
});
