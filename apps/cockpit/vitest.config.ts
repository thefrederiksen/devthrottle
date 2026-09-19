import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Vitest-only configuration, matching the mobile workspace's. It exists so the test runner does NOT
// load vite.config.ts, which shells out to git for the build stamp - a unit-test run has no business
// asking git anything. Tests declare their own environment per file (the @vitest-environment pragma).
//
// The two browser-global files below are client-core's and are loaded by all three workspaces, so the
// test environments cannot drift apart. They put back the globals a current Node and Vitest's jsdom
// environment settle wrongly between them - Web Storage and AbortController; the files say how and why.
//
// --import takes a file URL on every platform; a bare Windows drive path is not a module specifier.
const shared = (name: string) => new URL(`../../packages/client-core/testing/${name}`, import.meta.url);

export default defineConfig({
  plugins: [react()],
  // The build stamp vite.config.ts compiles in. The About page reads these two constants while it
  // renders, so without them every test that renders it dies on "__COCKPIT_COMMIT__ is not defined".
  // Fixed strings rather than the real commit: a unit test must not change its input every time
  // somebody commits, and no test asserts on the value - only that the page reports a build at all.
  define: {
    __COCKPIT_COMMIT__: JSON.stringify("test-build"),
    __COCKPIT_BUILD_TIME__: JSON.stringify("2026-01-01T00:00:00.000Z"),
  },
  test: {
    poolOptions: { forks: { execArgv: ["--import", shared("nodeBrowserGlobals.js").href] } },
    setupFiles: [fileURLToPath(shared("browserGlobals.ts"))],
  },
});
