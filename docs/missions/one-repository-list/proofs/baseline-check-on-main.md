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
