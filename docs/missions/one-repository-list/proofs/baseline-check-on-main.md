# The mission check, measured on a clean `origin/main` before phase 1

Run by the Delivery Lead on 19 September 2026, on macOS (Darwin 25.5.0, Apple silicon), against
`origin/main` at `74485174f`, in a worktree cut fresh from it and with `npm ci` run first.

This exists because section 7 of the mission document forbids quoting a baseline of known-red tests,
and because a check that is red before anyone touches it gates nothing. The numbers below are what
the check reported *before* this mission changed a line, so that no phase can be blamed for them and
none can hide behind them.

| Command from section 7 | Result |
|---|---|
| `npm run typecheck` | green |
| `npm test --workspaces --if-present` | **74 failed**, 484 passed, 558 total |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **7 failed**, 6345 passed, 8 skipped, 6360 total |
| `dotnet test src/CcDirector.Core.Tests` | green - 0 failed, 4445 passed, 18 skipped, 4463 total |
| `dotnet test src/CcDirector.Avalonia.Tests` | **7 failed**, 543 passed, 550 total |

88 failures out of roughly 11,500 tests, in four root-cause groups rather than 88 separate problems.

`dotnet` is not on the path on this machine; it is at `~/.dotnet/dotnet`. A fresh worktree has no
`node_modules`, and `tsc: command not found` means exactly that.

## The web failures - 74

38 of them are one cause: `[vitest] No "gatewayFetch" export is defined on the
"@devthrottle/client-core/api/client" mock.` A hand-written mock that never followed the product when
the export changed. The rest are a mix - an undefined `.clear()`, unimplemented `window.scrollTo` and
`HTMLCanvasElement.getContext` under the test DOM, an `AbortSignal` instance check, a missing colour
legend, and four genuine route assertions (`/report/...` where `/signin?next=...` was expected).

`apps/mobile/src/pages/NewSession.test.tsx` is among them, 11 of 11 failing. That is the phone's New
Session screen, which phase 5 of this mission changes.

## The .NET failures - 14

`Gateway.UnitTests`, seven, in three groups:

- Symbolic-link path comparison - `RulePrimitivesTests.IsPathInside_follows_a_link_that_stays_inside_the_root`
  and `RuleCandidateFilterTests.A_rule_scoped_to_this_sessions_repository_is_a_candidate`. On macOS
  `/tmp` is a link to `/private/tmp` and the temporary directory is reached through a link, so a
  comparison of a resolved path against an unresolved one passes on Linux and fails here. **This is
  the mission's own subject matter** - identifying one repository from three surfaces is path
  comparison - so whether this is a test assumption or a real product defect about links was called
  out as the thing to get right rather than merely green.
- Process liveness - the three `SessionCommandExecutorLivenessTests.Kill_WithNoInjectedCheck_*`.
- Rename-failure simulation - `WorkListStorePersistenceTests.Import_RenameAsideFails_*` and
  `CronJobStoreTests.LegacyJson_RenameFailsAfterImport_*`, which make a rename fail in order to test
  the recovery from it; macOS permission semantics may let the rename succeed, so the test never
  reaches the state it is testing.

`Avalonia.Tests`, seven. Six are audio - two `MicCaptureConstructionQueriesNoDeviceTests`, one
`SpeakDialogCloseDuringStartupTests`, three cases of `SpeakDialogReadyCueBlankingTests` - and one is
`LegacyWorkspaceImportTests`. The easy reading is "no microphone on an unattended Mac". The two named
`QueriesNoDevice` assert that *construction does not touch an audio device*, so their failing on macOS
points at construction doing exactly that on this platform, which would be a product defect and not a
missing microphone. That was flagged to be proved from the failure output rather than assumed.

## What was done about it

Two Developers, each on its own worktree and its own pull request, one for the web suites and one for
the .NET suites, each required to name for every failure whether the **test** or the **product** is at
fault and to fix it there. `[Skip]`, a deleted assertion, a loosened assertion and a platform guard
that merely stops a test running on macOS were all ruled out as fixes in their mandates.

`Core.Tests` is green and was left alone. A memory carried into this session claimed 65 macOS
failures in it as of 18 September; that is no longer true and was corrected rather than worked around.

---

# Correction, same day, after the work was done

**Most of what is written above about the web failures is wrong, and it was wrong in my favour - it
made `main` look broken when it was not.** It is left standing rather than edited, because a record
that quietly rewrites itself teaches nothing. What follows replaces it.

The Developer who fixed the web suites disproved the framing I gave it, and proved the replacement in
one line: **pristine `origin/main` at `74485174f`, untouched, run under Node 22.20.0 - the version
continuous integration pins - is green. 2117 passed, exit 0.** The only difference from my red run was
the Node binary: mine was v26. `main` was never broken. Continuous integration was right to be green
throughout.

Three things I got wrong:

1. **The count was 97, not 74.** I read the tail of an `npm test --workspaces` run and missed
   `client-core`'s 23 failures, because that workspace runs first and had scrolled past. The split was
   client-core 23, cockpit 38, mobile 36.
2. **The `gatewayFetch` group was never a failure.** The 38 `No "gatewayFetch" export is defined`
   lines, the 4 colour-legend lines, the 9 `window.scrollTo` and the 2 canvas `getContext` lines are
   **stderr written by tests that PASS**, before this mission and after it. They print on a fully green
   run today. I counted them because they sat next to the real failures in the output, which is
   adjacency mistaken for causation. I then briefed a second Developer on that same wrong framing and
   had to correct it.
3. **The cause was the runtime, not stale tests.** From Node 24, `localStorage` and `sessionStorage`
   ship as globals. Vitest's jsdom environment skips any `window` key already present on `globalThis`
   that is not on its own hard-coded list, and Web Storage is not on that list - so Storage read back
   `undefined` and 91 tests fell. Separately, jsdom's `AbortController` *is* on that list and replaces
   Node's, while `fetch`/`Request` remain Node's because jsdom implements neither; from Node 24 undici
   brand-checks the signal, so `new Request(url, {signal})` threw. React Router builds that Request on
   every navigation, so the authentication gate's redirect threw inside the router and the test saw the
   address it started at. The 5 "route" assertions were not telling anyone anything true about routes.

The Delivery Lead verified the fix independently rather than accepting the report: `npm run typecheck`
green and `npm test --workspaces --if-present` at **2117 passed, 0 failed, exit 0**, run on Node v26 in
a separate checkout of the branch.

## What this changes about the mission

The web half of the "red baseline" was an artefact of the machine the mission happens to be running
on. The fix is still worth having - it makes the suites run the same on every version of Node, and the
repository will meet Node 24+ again - but it repaired the test environment, not the product. **No
product code was touched.**

The fourteen .NET failures are a separate question and are not explained by Node. They were still
being worked when this correction was written.

## Deliberately left undone

Ten Cockpit roster test files mock `client-core/api/client` with hand-written factories that predate
the colour-legend reader, so `attentionOrder.test.tsx` opens a legend with no colours in it and proves
less than its name claims. `FleetMapView.test.tsx` holds the pattern to copy; roughly twenty minutes of
work. The Delivery Lead ruled it **out of scope**: it is the roster, not the New Session tab, and a
mission that absorbs every adjacent defect it walks past stops being a mission. Recorded here so it is
found rather than lost.
