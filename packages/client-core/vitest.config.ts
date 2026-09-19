import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

// Most of this library is pure logic and tests fine in node, which is why there was no config here at all.
// The shared REACT HOOKS need a document to render into, so .tsx tests get jsdom and the plain .ts tests
// keep running in node - the cheap environment stays cheap, and a hook can still be tested where it lives
// instead of being tested from whichever app happens to import it.
//
// Two browser globals are decided before any setup file gets a turn, and a current Node decides both
// wrongly: Web Storage (Node's own wins over jsdom's, and reads back undefined) and AbortController
// (jsdom's wins over Node's, and Node's Request then refuses the signals it makes). testing/
// nodeBrowserGlobals.js runs inside the worker before the environment is built and carries the full
// explanation; testing/browserGlobals.ts finishes the job once it is. The Cockpit and the mobile app
// load the same two files, so the three test environments cannot drift apart.
//
// --import takes a file URL on every platform; a bare Windows drive path is not a module specifier.
const testing = (name: string) => new URL(`./testing/${name}`, import.meta.url);

export default defineConfig({
  test: {
    environment: "node",
    environmentMatchGlobs: [["**/*.test.tsx", "jsdom"]],
    poolOptions: { forks: { execArgv: ["--import", testing("nodeBrowserGlobals.js").href] } },
    setupFiles: [fileURLToPath(testing("browserGlobals.ts"))],
  },
});
