// Runs in each Vitest worker BEFORE the test environment is built (--import, wired through
// poolOptions.forks.execArgv in each workspace's Vitest config). It exists because two of the browser
// globals a jsdom test needs are decided before any setup file gets a turn, and both of them are
// decided wrongly on a current Node.
//
// 1. WEB STORAGE. Node ships localStorage and sessionStorage as globals (experimental from 22.4,
//    unflagged from 24). Vitest's jsdom environment copies a jsdom window onto globalThis but skips
//    every name that is already there and is not on its own hard-coded key list - and Web Storage is
//    not on that list. So from Node 24 the tests get Node's: localStorage reads back undefined unless
//    the process was started with --localstorage-file, and sessionStorage is one process-wide store
//    shared by every test file in the worker. Deleting them here, while nothing is watching, lets the
//    environment install jsdom's real per-document Storage exactly as it does on Node 22.
//
// 2. ABORT. AbortController IS on that key list, so jsdom's replaces Node's - while fetch, Request,
//    Response and Headers stay Node's, because jsdom implements none of them. From Node 24 undici
//    brand-checks the signal it is handed, so `new Request(url, { signal })` with a jsdom signal
//    throws "Expected signal (AbortSignal {}) to be an instance of AbortSignal". React Router builds
//    exactly that Request on every navigation, so on Node 24 and later a jsdom test cannot navigate.
//    Node's pair is stashed here and put back by the setup file, which is the first code that runs
//    after the environment is built.
//
// Both were invisible on the continuous integration runner, which is Node 22, and turned ninety-seven
// tests red on a developer machine running Node 26.

delete globalThis.localStorage;
delete globalThis.sessionStorage;

globalThis.__devthrottleNodeAbort__ = {
  AbortController: globalThis.AbortController,
  AbortSignal: globalThis.AbortSignal,
};
