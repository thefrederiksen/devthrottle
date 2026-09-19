// Puts back the Node globals that Vitest's jsdom environment replaced with jsdom's, for the two cases
// where jsdom's copy cannot work with the rest of the environment. Runs as a Vitest setup file, once
// per test file, in all three workspaces - one copy so the environments cannot drift apart.
//
// testing/nodeBrowserGlobals.js, which runs before the environment is built, carries the full
// explanation and does the Web Storage half of the job. This file does the abort half: fetch, Request
// and Response are Node's in every jsdom test (jsdom implements none of them), and from Node 24 they
// refuse a signal that is not Node's own AbortSignal - which is what React Router hands them on every
// navigation. Restoring Node's pair makes the two halves of the environment agree again.
//
// Both files can go the day Vitest's jsdom environment stops overwriting AbortController and starts
// installing Web Storage over Node's.
const stashed = (globalThis as Record<string, unknown>).__devthrottleNodeAbort__ as
  | { AbortController: typeof AbortController; AbortSignal: typeof AbortSignal }
  | undefined;

if (stashed === undefined) {
  throw new Error(
    "The Vitest worker did not preload testing/nodeBrowserGlobals.js. Check poolOptions.forks.execArgv " +
      "in this workspace's Vitest config: without it the browser globals are wrong and the failures " +
      "look like product defects.",
  );
}

// Only the DOM environments were touched; a node-environment test still has Node's own.
if (typeof document !== "undefined") {
  Object.defineProperty(globalThis, "AbortController", {
    value: stashed.AbortController,
    writable: true,
    configurable: true,
    enumerable: true,
  });
  Object.defineProperty(globalThis, "AbortSignal", {
    value: stashed.AbortSignal,
    writable: true,
    configurable: true,
    enumerable: true,
  });
}
