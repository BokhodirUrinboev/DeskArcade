import * as assert from "node:assert/strict";
import { describe, test } from "node:test";
import { cleanField, cleanProfile, dotnetTempDir, pipeName, pipePaths, statusLine } from "../../src/protocol";

describe("cleanProfile", () => {
    test("keeps ASCII letters, digits, - and _ as Program.cs does", () => {
        assert.equal(cleanProfile("a3"), "a3");
        assert.equal(cleanProfile("my test_copy-2"), "mytest_copy-2");
        assert.equal(cleanProfile("dév/../x"), "dvx");
    });
    test("cuts to 32 characters", () => assert.equal(cleanProfile("x".repeat(40)), "x".repeat(32)));
    test("nothing is no profile", () => {
        assert.equal(cleanProfile(undefined), "");
        assert.equal(cleanProfile("   "), "");
    });
});

describe("cleanField", () => {
    test("takes out the separator, line breaks and control characters", () => {
        assert.equal(cleanField("build | test", 40), "build test");
        assert.equal(cleanField("line one\r\nline two\n", 40), "line one line two");
        assert.equal(cleanField("a\u0000b\u0007c\u001bd\u007fe\u0085f", 40), "a b c d e f");
        assert.equal(cleanField("para graph end", 40), "para graph end");
        assert.equal(cleanField("\t tabs \t", 40), "tabs");
    });
    test("leaves text within the limit alone", () => {
        assert.equal(cleanField("npm: test", 40), "npm: test");
        assert.equal(cleanField("x".repeat(40), 40), "x".repeat(40));
        assert.equal(cleanField("Сборка · ishga tushirish", 40), "Сборка · ishga tushirish");
    });
    test("cuts longer text with an ellipsis, at most the limit", () => {
        const cut = cleanField("x".repeat(41), 40);
        assert.equal(cut, "x".repeat(39) + "…");
        assert.equal(cut.length, 40);
        assert.equal(cleanField("word ".repeat(30), 20), "word word word word…");
    });
    test("never cuts a character in half", () => {
        const cut = cleanField("a" + "😀".repeat(30), 10);
        assert.ok(cut.length <= 10);
        assert.equal(cut, "a😀😀😀😀…");
        assert.doesNotMatch(cut, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])/);
    });
    test("nothing is empty", () => {
        assert.equal(cleanField(undefined, 40), "");
        assert.equal(cleanField("|||", 40), "");
    });
});

describe("statusLine", () => {
    test("state, name, note and source", () => {
        assert.equal(statusLine("running", "build", "dotnet build"), "status:running|build|dotnet build|vscode");
        assert.equal(statusLine("passed", "build"), "status:passed|build||vscode");
        assert.equal(statusLine("failed", "test", "exit code 1"), "status:failed|test|exit code 1|vscode");
        assert.equal(statusLine("clear", "debug"), "status:clear|debug||vscode");
    });
    test("always four fields on one line, whatever the name and note hold", () => {
        const line = statusLine("running", "a|b\nc", "x|y\r\nz|");
        assert.equal(line, "status:running|a b c|x y z|vscode");
        assert.equal(line.split("|").length, 4);
        assert.doesNotMatch(line, /[\r\n]/);
    });
    test("name at most 40 characters, note at most 80", () => {
        const [, name, note] = statusLine("running", "n".repeat(100), "m".repeat(200)).split("|");
        assert.equal(name.length, 40);
        assert.equal(note.length, 80);
    });
});

describe("pipe paths", () => {
    test("the name gets the profile's suffix", () => {
        assert.equal(pipeName(""), "DeskArcade.Signal.v1");
        assert.equal(pipeName("a3"), "DeskArcade.Signal.v1.a3");
        assert.equal(pipeName("a 3!"), "DeskArcade.Signal.v1.a3");
    });
    test("Windows: a named pipe", () => {
        assert.deepEqual(pipePaths("", "win32", {}), ["\\\\.\\pipe\\DeskArcade.Signal.v1"]);
        assert.deepEqual(pipePaths("a3", "win32", { TMPDIR: "/x" }), ["\\\\.\\pipe\\DeskArcade.Signal.v1.a3"]);
    });
    test("Linux: .NET's socket in $TMPDIR or /tmp, then the Flatpak's", () => {
        assert.deepEqual(pipePaths("", "linux", { XDG_RUNTIME_DIR: "/run/user/1000" }), [
            "/tmp/CoreFxPipe_DeskArcade.Signal.v1",
            "/run/user/1000/app/com.imperiumgames.DeskArcade/CoreFxPipe_DeskArcade.Signal.v1",
        ]);
        assert.deepEqual(pipePaths("a3", "linux", { TMPDIR: "/var/tmp/" }, 1001), [
            "/var/tmp/CoreFxPipe_DeskArcade.Signal.v1.a3",
            "/run/user/1001/app/com.imperiumgames.DeskArcade/CoreFxPipe_DeskArcade.Signal.v1.a3",
        ]);
        assert.deepEqual(pipePaths("", "linux", {}), ["/tmp/CoreFxPipe_DeskArcade.Signal.v1"]);
    });
    test("macOS: .NET's socket in the per-user $TMPDIR", () => {
        assert.deepEqual(pipePaths("", "darwin", { TMPDIR: "/var/folders/ab/cd/T/", XDG_RUNTIME_DIR: "/r" }), [
            "/var/folders/ab/cd/T/CoreFxPipe_DeskArcade.Signal.v1",
        ]);
    });
    test("the temporary folder as .NET reads it", () => {
        assert.equal(dotnetTempDir({}), "/tmp");
        assert.equal(dotnetTempDir({ TMPDIR: "" }), "/tmp");
        assert.equal(dotnetTempDir({ TMP: "/elsewhere", TEMP: "/elsewhere" }), "/tmp");
        assert.equal(dotnetTempDir({ TMPDIR: "/scratch//" }), "/scratch");
        assert.equal(dotnetTempDir({ TMPDIR: "/" }), "/");
    });
});
