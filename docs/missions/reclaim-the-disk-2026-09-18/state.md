# Reclaim the Disk: where the mission stands

Kept current by the Delivery Lead. **This file is the one place to look first.** The handover
documents are history; this is the state.

Last updated 19 September 2026 by the third Delivery Lead seat.

## Merged to main

- **Phase 1, scan and report.** Pull requests 3122 and 3129.
- **Phase 2, rules and recommendations.** Pull request 3135, review record 3146.
- **Phase 4, the remaining Windows rules.** Pull request **3171**, merged 20 September. Reviewed and
  approved by GLM 5.3 in Pi, which re-ran all five revert proofs; local gate green, 2,577 tests.
- **Phase 3, removal with holding.** Pull request **3184**, merged 20 September. All ten refusals
  proven red with the refusal deleted; reviewed and approved with no blocking findings, seven revert
  proofs re-run by the Reviewer. Parked run on the phase tip: Core.Tests 4,461 passed,
  Gateway.UnitTests 6,360 passed. Joined with main, Reclaim 309 green.
- **Phase 5 part one, the Launcher hosts the background scan.** Pull request **3187**, merged 20
  September. Approved with no blocking findings; Launcher 208, Reclaim 323, Gateway.UnitTests 6,497.

**The tool is usable from main now:** `recommend`, `reclaim` (dry run by default, `--apply` moves into
holding), and `holding list`, `holding restore`, `holding purge`.

## In flight

The Delivery Lead is session `cca7cc49`. Three Developers, each in a worktree cut from merged main:

- **Phase 5 part two, rules as data** - session `9c1083ea`, `D:/ReposFred/devthrottle-reclaim-phase5b`,
  branch `reclaim/phase5-rules-as-data`.
- **Phase 6, the screen** - session `7d78e3bf`, `D:/ReposFred/devthrottle-reclaim-phase6`, branch
  `reclaim/phase6-the-screen`. Carries one requirement from the phase 5 review: a status record that
  says running but is older than a day reads as failed.
- **The three review follow-ups** - session `ded5cf9f`, `D:/ReposFred/devthrottle-reclaim-followups`,
  branch `reclaim/review-follow-ups`: a holding root inside the fixture for the one test that uses the
  apply flag, a comment that names its guarantee, and a test that fails when the Launcher or the
  background job names a removal type.

Each one is pushed without a pull request, read by a Reviewer running GLM 5.3 in Pi (Codex is out of
usage until 22 September), gated by the Delivery Lead, then merged.

## Carried forward, for any platform that later gains rules

Refusal 4 (links) stands alone where path resolution does not follow links, and the background
scan's cross-process guard is not a guard on macOS and Linux. Both must be proven on that platform
before any removal is offered there. This mission ships Windows rules only.

## Reds that are not this mission's

- **Unset `CC_DIRECTOR_ROOT` before any gate run from inside a session.** It points at the live
  installation there, and that alone fails two `LauncherDeclaredCapabilitiesTests` and
  `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message`.
  All three were proven green on the same tree with it unset.
- `RetiredMessagingWordsTests` in Core.UnitTests is red on main: it names
  `src/CcDirector.Gateway.UnitTests/Drain/DrainMessagesSmartShutdownTests.cs` line 72, another
  mission's file.
- `VoiceServingLoopIsolationTests.Voice_sweep_reaches_only_the_owning_tenants_director` failed on
  plain main before phase 3 merged. Not retried with the variable unset, so its cause is unknown.

## What done looks like

Every phase merged to main. Then the QA report on issue **3120** covering the flow **and the failure
cases**, and one page for the owner. **Nothing is released and no Gateway is deployed** - both are
his decisions and the report says so.

## The laws that have actually bitten, in this mission

- **No removal on this machine, ever.** Proven on fixture trees the tests build. The first real
  removal is the owner's, from the finished tool, after the report.
- **A revert proof runs against the whole suite, never under a filter.** Phase 2 reported two red
  tests where there were three, purely because it ran that proof under a two-name filter. Commit
  before you mutate, and never `--no-build` on the restore run.
- **Switching a check off with `if (false)` is not a revert proof here** - warnings are errors, so it
  becomes a build failure, and a build that did not run tells you nothing about a test. Delete the
  check instead.
- **A snoozed seat never receives a message.** It is queued and never typed in. Read its screen,
  check its tree is clean, stop it with a reason, and seat a fresh one.
- **A check whose pass condition is an absence fails open.** Three times in this seat alone a quick
  shell check was wrong because a pipeline's exit code was `head`'s and not the program's, or a
  `grep` matched another mission's folder. State checks as a positive verdict and sanity-check the
  instrument.

## Litter this mission has left on the machine, for the owner to decide about

Two worktree directories were deregistered from git but could not be deleted because build output
remained in them: `D:/ReposFred/devthrottle-reclaim-review2` and `D:/ReposFred/devthrottle-reclaim-gate`.
**Nothing was deleted** - this mission does not remove anything on this machine, and that includes
its own leftovers. They are named here so the decision is the owner's.
