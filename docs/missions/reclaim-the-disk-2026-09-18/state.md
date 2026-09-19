# Reclaim the Disk: where the mission stands

Kept current by the Delivery Lead. **This file is the one place to look first.** The handover
documents are history; this is the state.

Last updated 19 September 2026 by the third Delivery Lead seat.

## Merged to main

- **Phase 1, scan and report.** Pull requests 3122 and 3129. The engine project, the scanner, the
  saved index, the report, and the `scan` and `report` commands. Three review rounds.
- **Phase 2, rules and recommendations.** Pull request **3135**, merged 19 September. The rule
  contract, the fold, the classifier, the `recommend` command and six Windows rules. Its review
  record is pull request **3146**: the first review returned not approved on one blocking finding -
  the fold accepted a rule whose every control was declared may-be-empty, and such a rule can never
  report broken - and the fix-round review returned approved after re-running the finding's own
  reproduction, both revert proofs, the gate and the counts itself.

## In flight

- **Phase 3, removal with holding.** Worktree `D:/ReposFred/devthrottle-reclaim-phase3`, branch
  `reclaim/phase3-removal-and-holding`. A Tech Lead is seated, as the mission requires for this
  phase. Its plan is committed as `phase-3-plan.md` and accepted; my rulings on it are
  `phase-3-rulings.md`.

  **The ruling that matters:** the plan proposed finding refusal 1's protected paths by reflecting
  over `CcStorage` for method names containing vault, credential or secret. Overruled. Checked
  against `CcStorage` on main, that name test misses `Config()`, whose own documentation comment
  reads "Tool settings, OAuth tokens, credentials, app state", and misses `keyvault.json` which
  `KeyVault` assembles at `CcStorage.Root()`. Refusal 1 would have protected two paths by luck of
  naming and left the OAuth tokens and the key vault exposed. It is instead one explicit enumeration
  declared in `CcStorage` beside the paths it names, with a test that fails when a new storage path
  is neither protected nor explicitly classified. **Because that changes Core, phase 3 must run
  `-Parked`** - phase 2's reason for declining it held only because phase 2 touched no Core source.

- **Phase 4, the remaining Windows rules.** Worktree `D:/ReposFred/devthrottle-reclaim-phase4`,
  branch `reclaim/phase4-remaining-windows-rules`. A Developer is seated. Runs beside phase 3
  because it adds rules and touches no removal code.

## Ready to start, mandates written

The remaining mandates are committed on this branch, `mission/reclaim-the-disk`:

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

## Litter this mission has left on the machine, for the owner to decide about

Two worktree directories were deregistered from git but could not be deleted because build output
remained in them: `D:/ReposFred/devthrottle-reclaim-review2` and `D:/ReposFred/devthrottle-reclaim-gate`.
**Nothing was deleted** - this mission does not remove anything on this machine, and that includes
its own leftovers. They are named here so the decision is the owner's.
