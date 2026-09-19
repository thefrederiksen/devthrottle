# Reclaim the Disk: where the mission stands

Kept current by the Delivery Lead. **This file is the one place to look first.** The handover
documents are history; this is the state.

Last updated 19 September 2026 by the third Delivery Lead seat.

## Merged to main

- **Phase 1, scan and report.** Pull request 3122, plus its record in 3129. The engine project, the
  scanner, the saved index, the report, and the `scan` and `report` commands. Three review rounds.

## In flight

- **Phase 2, rules and recommendations.** Branch `reclaim/phase2-rules-and-recommendations`, pull
  request **3135**, mergeable and clean onto current main. The rule contract, the fold, the
  classifier, the `recommend` command and six Windows rules.

  Reviewed and returned **not approved** with one blocking finding - the fold accepted a rule whose
  every control was declared may-be-empty, and such a rule can never report broken. **Fix round one
  is built, pushed and answers all five findings**, none declined; see `review-phase-2-answers.md`.
  Local gate green at 2,466 tests, all nine suites `outcome=Completed`. Awaiting the fix-round
  Reviewer's verdict on the delta, then merge.

- **Phase 3, removal with holding.** Tech Lead seated (the mission requires one for this phase),
  worktree `D:/ReposFred/devthrottle-reclaim-phase3`, branch `reclaim/phase3-removal-and-holding`
  cut from origin/main. It is reading and writing a technical plan to `phase-3-plan.md`. **It is
  told to write no code until phase 2 merges**, and is waiting on word from the Delivery Lead.

## Ready to start, mandates written

All four remaining mandates are committed on this branch, `mission/reclaim-the-disk`:

- `mandate-phase-3.md` - removal as a move into holding, dry run the default, the ten refusals each
  a numbered test proven red with its refusal removed.
- `mandate-phase-4.md` - the remaining Windows rules. **May run beside phase 3 once phase 2 merges**,
  because it adds rules and touches no removal code. Seat a Developer for it at that moment.
- `mandate-phase-5.md` - the background scan in the Launcher, and rules as data. Two pull requests.
  **Ends at merged**; the Gateway deploy is the owner's decision.
- `mandate-phase-6.md` - the screen on both surfaces. Renders only; no remove button anywhere.

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
