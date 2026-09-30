// Starts a real VS Code (downloaded into .vscode-test the first time) with the extension loaded and the fixture
// folder open, and runs suite.ts inside it. On Linux run it under xvfb-run.
//   npm run test:integration
//   DESKARCADE_VSCODE_VERSION=1.90.0 npm run test:integration    (the oldest VS Code the extension supports)

import * as path from "path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
    // From a terminal inside VS Code these variables point at that VS Code, and ELECTRON_RUN_AS_NODE would start
    // the test copy as a bare Node instead of an editor.
    delete process.env.ELECTRON_RUN_AS_NODE;
    for (const key of Object.keys(process.env)) if (key.startsWith("VSCODE_")) delete process.env[key];

    const root = path.resolve(__dirname, "../../.."); // out/test/integration -> integrations/vscode
    await runTests({
        version: process.env.DESKARCADE_VSCODE_VERSION || "stable",
        extensionDevelopmentPath: root,
        extensionTestsPath: path.join(__dirname, "suite.js"),
        launchArgs: [path.join(root, "test", "fixture")],
    });
}

main().catch(err => {
    console.error(err);
    process.exit(1);
});
