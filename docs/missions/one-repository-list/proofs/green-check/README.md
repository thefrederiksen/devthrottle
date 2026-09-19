# The mission check was red before the mission started - what was wrong and what was done

Mission: One repository list, held on the Gateway. This record covers the pre-work that made the
mission's own check (section 7 of the mission document) trustworthy: the web half of that check was
red on a clean `origin/main` before anybody touched it, so it gated nothing.

Base commit: `74485174f` (`release: v2.8.0`), the tip of `origin/main` on 19 September 2026.
Machine: Sorens Mac mini, macOS 25.5, Node **v26.5.0**, npm 11.17.0.

---

## 1. What was actually red

The Delivery Lead reported 74 failures in `npm test --workspaces --if-present`. The real number on a
clean tree, with a fresh `npm ci`, is **97**, because the `@devthrottle/client-core` workspace runs
first and its 23 failures were missed in the earlier count.

| Workspace | Failing tests | Failing files |
|---|---|---|
| `@devthrottle/client-core` | 23 of 1,453 | 3 |
| `@devthrottle/cockpit` | 38 of 457 | 7 |
| `@devthrottle/mobile` | 36 of 101 | 5 |
| `@devthrottle/cc-assistant` | 0 of 106 | 0 |

`npm run typecheck` was green throughout and stayed green.

## 2. The single fact that decides every one of them: it is the Node version

The same tree, the same `node_modules`, run under **Node 22.20.0** instead of Node 26.5.0:

```
Test Files  123 passed (123)     Tests  1453 passed (1453)   client-core
Test Files    8 passed   (8)     Tests   106 passed  (106)   cc-assistant
Test Files   55 passed  (55)     Tests   457 passed  (457)   cockpit
Test Files   19 passed  (19)     Tests   101 passed  (101)   mobile
exit 0
```

Nothing was changed between those two runs but the Node binary. That is why continuous integration,
which pins Node 22 (`.github/workflows/ci.yml`), has been reporting the web job green on `main` the
whole time.

So **all 97 were the tests' fault, not the product's.** Not one of them was the product behaving
differently - in every case the test environment handed the code under test a browser that a real
browser never is. The two ways it did that are below. Nothing was skipped, nothing was deleted, and
no assertion was loosened; the product was not touched at all.

## 3. Failure one: 91 tests - the test environment had no Web Storage

**Symptom:** `TypeError: Cannot read properties of undefined (reading 'clear')`, on the first line of
a `beforeEach` that calls `localStorage.clear()`. Also the 5 route assertions and 1 abort error that
looked like separate problems in the baseline report but were this one cascading (see section 4).

**Which is at fault: the test environment.**

**Why it happens.** Node ships Web Storage as a global - `globalThis.localStorage` and
`globalThis.sessionStorage` exist before any test environment is built (experimental from Node 22.4,
unflagged from Node 24). Vitest's jsdom environment copies a jsdom window's properties onto
`globalThis`, but `getWindowKeys` skips every name that is already there and is not on Vitest's own
hard-coded key list - and Web Storage is not on that list. (`AbortController` is; see section 4.)

The consequence on Node 24 and later:

- `localStorage` reads back `undefined` unless the process was started with `--localstorage-file`,
  which is why `localStorage.clear()` throws rather than clearing anything.
- `sessionStorage` is one process-wide store that every test file in the worker shares, so test
  files can see each other's leftovers.

**The fix.** `packages/client-core/testing/nodeBrowserGlobals.js`, loaded with `--import` into each
Vitest worker before the environment is built (`poolOptions.forks.execArgv`), deletes Node's two
Web Storage globals. With the names gone, Vitest's jsdom environment installs jsdom's real,
per-document `Storage` exactly as it does on Node 22 - so both runtimes now run the same object. It
is deleted at the root rather than patched afterwards, and no Storage is hand-rolled anywhere.

**Proof it can fail.** With the two `delete` lines commented out and everything else left in place:
client-core 22 failed, cockpit 36 failed, mobile 33 failed - the same files, the same message.
Restored: all green.

## 4. Failure two: 5 tests - jsdom's AbortSignal in Node's `Request`

**Symptom, in `apps/cockpit/src/reportAddressRoute.test.tsx` and
`apps/mobile/src/reportLandingRoute.test.tsx`:**

```
RequestInit: Expected signal ("AbortSignal {}") to be an instance of AbortSignal.
```

and, from the same cause, three assertions that read like genuine routing defects:

```
expected '/report/{id}'        to be '/signin?next=%2Freport%2F{id}'
expected '/mobile/report/{id}' to be '/mobile/session/{id}/reports/{id}'
```

**Which is at fault: the test environment. The routing is correct.**

**Why it happens.** `AbortController` *is* on Vitest's key list, so jsdom's replaces Node's - while
`fetch`, `Request`, `Response` and `Headers` stay Node's, because jsdom implements none of them.
From Node 24 the bundled undici brand-checks the signal it is handed, so `new Request(url, { signal })`
with a jsdom signal throws. React Router builds exactly that Request on **every** navigation
(`createClientSideRequest`, called unconditionally from `startNavigation`), so on Node 24 and later a
jsdom test cannot navigate at all.

That is also why the three route assertions failed: the auth gate answers a signed-out browser with
`<Navigate replace to="/signin?next=...">`, that navigation threw inside the router, the redirect
never completed, and the test saw the address it started at. The two unhandled rejections Vitest
reported at the end of the mobile run were the same throw. The product's redirect is right; it was
never allowed to run.

**The fix.** The same preload stashes Node's `AbortController` and `AbortSignal`, and
`packages/client-core/testing/browserGlobals.ts` - a setup file, so the first code to run after the
environment is built - puts them back in the DOM environments. The two halves of the environment then
agree again, as they do in a real browser where there is only ever one implementation. The setup file
throws a named error if the preload did not run, so a misconfigured pool fails loudly instead of
returning the confusing failures above.

**Proof it can fail.** With the restore disabled and everything else left in place: cockpit 2 failed,
mobile 3 failed - the same five tests, the same messages. Restored: all green.

## 5. Failure three: 1 test - a premise that stopped being true

`packages/client-core/src/auth/enrollRequest.test.ts`, "returns null (the shell default) when storage
is unavailable": `expected '/fleet' to be null`.

**Which is at fault: the test.**

It proved "storage is unavailable" by calling `vi.unstubAllGlobals()` and relying on the node test
environment having no `sessionStorage` at all - an absence borrowed from the runtime rather than
stated by the test. Node then shipped its own Web Storage, unstubbing restored a *working*
process-wide store, the route was remembered, and the assertion failed.

Fixed in the test: it now stubs a `sessionStorage` whose every call throws `SecurityError`, which is
what a browser with Web Storage denied actually does (a sandboxed frame, cookies disabled, Safari's
private mode). The product is unchanged - `rememberEnrollNext` / `takeEnrollNext` already degrade to
the shell default, and that is still exactly what is being asserted.

This one is **not load-bearing for the green run**: the section 3 preload deletes Node's
`sessionStorage`, so the old form would pass again by accident. It was changed anyway because a check
whose pass condition is an absence certifies nothing the day the absence goes away - which is
precisely what happened here. Evidence that the new form stands on its own: with the preload's
deletes disabled, client-core failed 22, not 23 - this test held while the other 22 fell.

## 6. Introduced by this work, and fixed here: the Cockpit build stamp

`apps/cockpit` had no `vitest.config.ts`, so Vitest was loading `vite.config.ts` - which shells out to
`git rev-parse` on every test run and supplies `__COCKPIT_COMMIT__` / `__COCKPIT_BUILD_TIME__` through
Vite's `define`. Adding a test-only config (needed to wire the setup file, and matching what the
mobile workspace already does for the reasons its own comment gives) took those two constants away,
and the six `AboutView` tests died on `__COCKPIT_COMMIT__ is not defined`.

The new config defines both, as fixed strings rather than the live commit: no test asserts on the
value, only that the page reports a build at all, and a unit test should not change its input every
time somebody commits. A unit-test run no longer asks git anything.

## 7. What was reported as failing and is not: the `gatewayFetch` group

The baseline report listed **38 failures** of:

```
[vitest] No "gatewayFetch" export is defined on the "@devthrottle/client-core/api/client" mock.
```

together with 4 of `The Gateway's colour legend is missing its list of colours`, 9 of
`Not implemented: window.scrollTo` and 2 of `HTMLCanvasElement.prototype.getContext`.

**None of these is a failure.** They are lines the product writes to stderr and then carries on from,
and they appear in tests that pass - before this change and after it. They were counted because they
sit next to the real failures in the run output. They still print today, on a fully green run.

What they are: ten Cockpit roster test files mock `@devthrottle/client-core/api/client` with a
hand-written factory that lists only the exports the file needed when it was written. The colour
legend reader (`sessionColours.ts`, `GET /gateway/session-colours`) was added later and reaches for
`gatewayFetch`, which those factories do not supply, so the legend read fails and is logged. The two
client-core files mock nothing at all and reach for a Gateway that a unit test does not have.

This is a real, if small, defect in those tests - `attentionOrder.test.tsx`'s "offers the legend under
the ordering toggle, and opens it" opens a legend with no colours in it, so it proves less than its
name claims - but it is **not** what was red, and fixing twelve files of mocks is a different piece of
work from making the check trustworthy. `apps/cockpit/src/fleet/FleetMapView.test.tsx` already has the
pattern to copy (it stubs `gatewayFetch` with a real legend payload). Left undone deliberately, and
named here so it is not lost.

Also left alone, and pre-existing: `npx eslint .` reports 3 errors, all of the form "Definition for
rule '...' was not found" - inline `eslint-disable` comments naming rules the minimal config does not
load (`apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx`,
`apps/mobile/src/pages/VoiceMode.tsx`). Lint is not part of the mission check and no test is red
because of it.

## 8. The proof

Worktree `/Users/soren/ReposFred/devthrottle-repo-list-green`, branch
`mission/one-repo-list-green-check`, cut from `origin/main` at `74485174f`.

**Node 26.5.0 (this machine), after the change:**

```
> npm run typecheck                       exit 0
> npm test --workspaces --if-present      exit 0
  client-core    Test Files  123 passed (123)   Tests  1453 passed (1453)
  cc-assistant   Test Files    8 passed   (8)   Tests   106 passed  (106)
  cockpit        Test Files   55 passed  (55)   Tests   457 passed  (457)
  mobile         Test Files   19 passed  (19)   Tests   101 passed  (101)
                                                TOTAL  2117 passed, 0 failed
```

**Node 22.20.0 (what continuous integration runs), after the change:** identical - 2,117 passed, 0
failed, exit 0. The change is therefore not a Node-26-only patch that trades one runtime for the
other; both now run the same browser globals.

**What this proof does not cover.** The three .NET suites in the mission check
(`CcDirector.Gateway.UnitTests`, `CcDirector.Core.Tests`, `CcDirector.Avalonia.Tests`) were not run
here - this task was the web half, and nothing in it touches .NET. It also says nothing about Node
versions other than 22 and 26, and nothing about Windows or Linux, where only continuous integration
has run it; the one platform-specific thing this change does is pass `--import` a **file URL** rather
than a path, which is exactly what a Windows drive letter would otherwise break on.

## 9. Files changed

| File | Why |
|---|---|
| `packages/client-core/testing/nodeBrowserGlobals.js` | New. Worker preload: drops Node's Web Storage so jsdom's is installed, stashes Node's abort pair. |
| `packages/client-core/testing/browserGlobals.ts` | New. Setup file: restores Node's `AbortController` / `AbortSignal` in the DOM environments. |
| `packages/client-core/vitest.config.ts` | Loads both. |
| `apps/mobile/vitest.config.ts` | Loads both. |
| `apps/cockpit/vitest.config.ts` | New. Loads both, and supplies the build stamp `vite.config.ts` used to. |
| `packages/client-core/src/auth/enrollRequest.test.ts` | States its own "storage refuses" premise instead of borrowing the runtime's. |

No product code was changed.
