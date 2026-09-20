# Wingman error and retry - the Delivery Lead's record

Merged to main on 20 September 2026. **Not deployed.** The owner ruled that the deploy waits so it can
ship together with the work of the Wingman investigation session (session 151, "why it invents menus and
wrong verdicts"), because every deploy costs a measured live outage and two changes in one image cost one
outage instead of two.

## What was delivered

Sections 5.1, 5.2 and 5.3 of the mission document, in one branch:

- **One retry schedule.** A failed reading is asked again after 1, 1, 1, 5, 5, 5, 30 and 30 minutes, then
  stops. The schedule is written on the stored reading, so it survives a restart, and the idle sweep
  carries it - no timer per session. It ends on the first success, on a new turn, when the session goes
  back to work, and is withdrawn when no reading is owed. A rate limit's named wait is honoured.
- **The old retry ladder is deleted**, not left beside the new one. There is exactly one retry mechanism,
  which the mission document made a condition of being done.
- **A bad button list costs the buttons, not the reading.** Every option-list rule now drops the whole
  list, keeps the state and label so the narration runs, and records why. Nothing is repaired or guessed.
  Refusals for reasons outside the option list are still failures and are still retried.
- **The card.** The Gateway stamps the failure, a plain reason, which retry is next, its absolute time,
  how many remain, and whether the schedule is used up. One shared component in `packages/client-core`
  draws the "Wingman error" tag, the countdown and the Ask again button on both the Cockpit and the phone.
  The client's only arithmetic is turning an absolute time into "in 40 seconds". It does not depend on
  voice mode.

## Simpler, as the owner asked

- `notNarrated` is gone. It and the failed reading are one state. Two near-identical silent states with
  different buttons were part of what made this hard to see.
- The "explain once" marker is gone: a person asking always makes one attempt.

## The five things the Developer put up, and the answers

Method law 11 - these were answered, not left open.

1. **The tag rides the same account switch as the Wingman's colours.** Accepted. With the Wingman off
   there are no readings at all, so there is no error to show. It is coherent, not a gap.
2. **A write-up call that fails after the judge answered is a failed reading** - same tag, same schedule.
   Accepted; it matches goal 1, which is about a reading that failed, not about which step failed.
3. **Every speech failure that is not an account condition goes on the schedule**, including ones the old
   ladder did not retry. Accepted; the owner ruled one schedule for every kind of failure.
4. **An offline computer's due retry reads "due now" for as long as the computer is away.** Accepted, and
   recorded here as the known rough edge. It does not break goal 6 - the retry really is booked, so no
   card claims an attempt that is not booked - but it can sit there a while. The Reviewer saw it
   independently and also declined to raise it as a finding.
5. **`schema.ts` was not regenerated** (it needs a live Gateway on a fixed port). Accepted; the new fields
   are typed beside their reader, which is how the delivery fields already work.

## What proves it, and what does NOT

Checked by the Delivery Lead, not taken from the Developer's report.

- **Merges onto main with no conflicts**, twice, despite main moving 33 commits during the work.
- **Default local gate: green** on all nine suites except two in `CcDirector.Launcher.Tests`. Those are
  environmental, and the proof is structural rather than a re-run: the change touches ZERO Launcher files,
  and both failures are the "unarmed launcher" precondition returning true because a real armed launcher
  runs on this machine.
- **`Gateway.UnitTests`: 6,664 executed, 0 failed**, TRX outcome Completed. The two not executed are
  explicit skips.
- **Independent review by a different model family** (the mission document's named alternative, after the
  first reviewer hit a usage limit): **no material findings**, written up in `REVIEW.md` with its scope.
- **The one unexplained test result is settled and is not ours.**
  `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message` is a
  pre-existing test isolation race. The Reviewer reproduced it failing at the same assertion on the merge
  base, where none of this change exists.
- **A revert proof**, committed before the mutation: with the root-cause change removed, seven tests went
  red on a full rebuild, the root-cause test among them, and green again after restoring and rebuilding.

**What the evidence does NOT cover, stated plainly:**

- **`CcDirector.Gateway.Tests` has no verdict.** The hour-long parked run was killed at 34 minutes when
  the machine went critically low on memory - three worktrees were running that same lock-held suite at
  once. Eleven of twelve suites had already written their result; this one had not. A suite that did not
  finish is not a pass, and it is not recorded as one. Its three known failures are all reproduced on the
  base commit, but the rest of the suite is unverified against this change.
- **One failure seen in that killed run's partial output is unexplained**:
  `FleetOutcomeStopIdentityPostgresTests.AddFleetOutcomeStopIdentity_FromEmpty_AddsTheColumnsAndKeepsARecordFiledBefore`.
  It does not look like ours - the change touches no migration and no `Data/` file - but that is reasoning,
  not a base-commit run, so it is an open question rather than a cleared one.
- **No live proof exists yet.** Nothing has been deployed, so the before-and-after fleet measurement in
  section 7 of the mission document is half done: the before pass was taken on 20 September (27 sessions,
  3 of 6 red sessions silent, all showing "Turn not narrated"). The after pass cannot be taken until the
  deploy happens.
- The screenshots are from a local Gateway whose model call really failed. `proof/PROOF.md` says which two
  of the twelve rest on a seated record rather than a produced one.

## What the deploy seat still owes

1. **Settle `Gateway.Tests`** - run `.\scripts\test-local.ps1 -Gateway` on merged main when no other
   worktree holds the machine-wide lock, and settle the `FleetOutcomeStopIdentityPostgresTests` question
   by running it on the base commit if it fails again.
2. **Deploy** through the `deploy-hosted-gateway` skill and nothing else. The owner's production grant for
   this mission is in section 4 of the mission document. Read the measured `external_outage_seconds`; a
   watch job that fails on the outage budget usually means the deploy LANDED.
3. **The after measurement.** `docs/missions/wingman-error-and-retry-2026-09-19/qa/measure.py` in
   `devthrottle_internal`, run as `python measure.py after`. The before pass is beside it. The goal: every
   red session either has voice, or shows the tag with a retry booked, or shows the tag with "nothing more
   is scheduled" - and no other state.
4. **The live QA report** - real cards by screenshot from the Cockpit and the phone, the flow and the
   failure cases, saying which shots are live and which are local.
5. **One page for the owner**, published with `cc-dev-reports open <file.html>`.
