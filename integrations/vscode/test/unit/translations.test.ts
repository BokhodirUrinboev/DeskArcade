// Every text the extension shows exists in English, Russian and Uzbek: the manifest's %keys% in the package.nls
// files, and every vscode.l10n.t text in the l10n bundles.

import * as assert from "node:assert/strict";
import * as fs from "fs";
import * as path from "path";
import { describe, test } from "node:test";

const root = path.resolve(__dirname, "../../.."); // out/test/unit -> integrations/vscode
const read = (file: string) => fs.readFileSync(path.join(root, file), "utf8");
const json = (file: string) => JSON.parse(read(file)) as Record<string, string>;
const languages = ["ru", "uz"];

describe("translations", () => {
    test("every %key% in package.json is in package.nls.json, and nothing more", () => {
        const used = new Set([...read("package.json").matchAll(/"%([^%"]+)%"/g)].map(m => m[1]));
        const english = Object.keys(json("package.nls.json"));
        assert.deepEqual([...used].sort(), [...english].sort());
    });
    for (const lang of languages) {
        test(`package.nls.${lang}.json has every key, none empty or left in English`, () => {
            const english = json("package.nls.json");
            const translated = json(`package.nls.${lang}.json`);
            assert.deepEqual(Object.keys(translated).sort(), Object.keys(english).sort());
            for (const [key, text] of Object.entries(translated)) {
                assert.ok(text.trim().length > 0, `${key} is empty`);
                assert.notEqual(text, english[key], `${key} is still English`);
            }
        });
    }

    const runtime = [...read("src/extension.ts").matchAll(/l10n\.t\("((?:[^"\\]|\\.)*)"/g)].map(m => JSON.parse(`"${m[1]}"`) as string);
    test("the extension shows some translated text", () => assert.ok(runtime.length >= 4, `found ${runtime.length}`));
    for (const lang of languages) {
        test(`l10n/bundle.l10n.${lang}.json has every vscode.l10n.t text, with its placeholders`, () => {
            const bundle = json(`l10n/bundle.l10n.${lang}.json`);
            assert.deepEqual(Object.keys(bundle).sort(), [...new Set(runtime)].sort());
            for (const [english, text] of Object.entries(bundle)) {
                assert.ok(text.trim().length > 0, `"${english}" is empty`);
                const holes = (s: string) => (s.match(/\{\d+\}/g) ?? []).sort().join();
                assert.equal(holes(text), holes(english), `"${english}" has other placeholders`);
            }
        });
    }
});
