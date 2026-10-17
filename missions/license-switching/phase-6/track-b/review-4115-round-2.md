# Review of pull request 4115, round two - the rebase over the alarm work

Reviewer: a separate review session, a different agent family from the Developer, opened by the Track B Tech Lead. Read only. Nothing in the worktree was edited, built or committed.

## Scope

**What I read.** Only what changed between the head I reviewed in round one (c15d3892b) and the new head (255da59de), with the base now 0702c88ce (the alarm work, #4110). Because origin/main moved under the branch, a plain `git diff c15d3892b 255da59de` is mostly the alarm work; I restricted it to the files this pull request owns and read every line of that: `tool-health.md`, `review-4115-answers.md`, `LicenseStore.cs`, `LicenseDisplayFold.cs`, `LicenseCannotSwitchTests.cs`, both rebuilt migrations, `contract.md`, `cockpit-api.md`, and the smaller files that moved with the rebase. The two migration Designers and both model snapshots I checked by searching for the migration id, the new column and the alarm table rather than reading the generated text. The pull request body as it stands now.

**What I ran.** Nothing from this pull request. I did not build, did not run the gate, did not run the window proof. I swept the new head's diff against its base for real names, machine names, addresses, email domains and user paths.

**What I could not reach.** Any run the Developer made but did not write down. The question in finding 1 below is exactly that.

## Verdict

**The rebase is clean. One finding remains open, and it is the same one as round one: no gate run on the code that merges is recorded anywhere I can read.** The three things the Tech Lead asked me to check - the migration, the log-kind lists, the contract renumbering - are all right. Round-one finding 2 (the crash in the middle of the swap) is answered in the proof and the answer matches my own reading of the code. Round-one observation 2 (the raw "not-known" word) is fixed and tested.

## The three things asked

### The migration

The two migrations from the first round (dated 2026101709xx) are gone. In their place are `20261017121400_AddLicenseMachineCannotSwitch` (SQLite) and `20261017121456_AddLicenseMachineCannotSwitch` (Postgres), both dated after the alarm migration so they apply after it. The content is unchanged: one nullable `CannotSwitchReason` column on `license_machines`, `TEXT` with max length 32 on SQLite, `character varying(32)` in the `gateway` schema on Postgres, and a `DropColumn` in `Down`. Each Designer carries the new id in its `[Migration]` attribute and its model includes both the alarm table and the new column; both model snapshots include both. No file still carries the old migration ids. Correct.

### The log-kind lists

`LicenseLogKinds` now reads switch, spare-added, spare-removed, no-room, renewal-failed, alarm-opened, alarm-closed, cannot-switch, can-switch-again: the two alarm kinds from Track A and the two from this pull request, none lost, none doubled. `LicenseDisplayFold` has a kind text for every one of the nine, and `cockpit-api.md` lists the same nine on its kinds line and keeps the rule that a client never branches on a kind it does not know. The three lists agree. Correct.

### The contract renumbering

Track A took section 9 for the alarm (9.1 to 9.5). This pull request's section is now `## 10. Phase 6 - whether a machine can run a switch` with `### 10.1` beneath it, and `cockpit-api.md` cites 10.1 for the `cannot-switch` status. I searched both documents for a stale "9.1" that still meant this change and found none. Correct.

One stale reference remains OUTSIDE the worktree: the pull request body on GitHub still says "Contract section 9.1 and `cockpit-api.md` are updated". The body is not part of the merge, but it is the tracking record and should say 10.

## Round-one findings, re-read

### Finding 1 - the Gateway unit suite on the commit that merges: STILL OPEN

`review-4115-answers.md` says: the gate rule from the Delivery Lead is the default gate plus `.\scripts\test-local.ps1 -Parked -Filter License` with TEMP on drive D; "both were run on the rebased head, and the results are in `tool-health.md`, section 'The gate', which replaces the evidence from the earlier commits."

The section "The gate" in `tool-health.md` at 255da59de is unchanged from round one. It records the default gate at c4855f2ea, the `-Parked` run at 2dd094eef with six failures, and the filtered run of 972 tests after the rebase onto 6c783e3d4. It records no run at 255da59de, none at any commit on the new base 0702c88ce, and no `-Filter License` run at all. The pull request body says the same as the old section. The only mention of a commit on the new base in the whole proof is the bin folder revert proof at 700cba2be, which is a two-test revert run, not a gate.

So either the runs were made and not written down, in which case the answer file points at evidence that is not there, or they were not made. From what I can read, the latter is what the proof says. The harm expected is still low (the rebase brought in the alarm work, which touches the same `LicenseStore` and fold the pull request edits, so this rebase is the one where a conflict would hide), but the rule for a Gateway change is `-Parked`, and the written claim and the written evidence disagree. The fix is one paragraph in "The gate": the commit, the two commands, the counts. I confirmed `test-local.ps1` has a `-Filter` parameter, so the command the answers describe exists.

### Finding 2 - what a crash during the swap leaves behind: ANSWERED

`tool-health.md` gains a section "A crash in the middle of the swap". It says what each of the three stopping places leaves on disk, that the bundle version is written only after the swap so `ToolUpdater` runs the installer again on the next hourly refresh, that `RemoveLeftoverTrees` deletes a backup only after a verified install, and that the Director's new field reports the gap in the meantime. That is the same answer I derived from the code in round one. No test was added and I did not ask for one.

### Observation 2 - the raw "not-known" word: FIXED

`LicenseDisplayFold.ReasonText` now maps `LicenseStore.CannotSwitchReasonNotKnown` to "The machine's Director says it cannot run a switch, for a reason this Gateway does not know." and `LicenseCannotSwitchTests` asserts that sentence for a row written from an unknown reason word.

## New in this head

- **"The bin folder window seen on 17 October 2026"**, a new proof section. It says the installer's bin folder rename-and-back (the in-use probe) is the window a 17 October log showed, that this pull request removes it, and that a revert proof at 700cba2be fails the two named tests on the old code. I did not run it; as with every revert proof in this mission, it is self-testimony. The section also says plainly that "the update that installs this fix has the old window one last time", which is true and worth having in writing.
- **`LicenseDisplayFold` kind texts** for `alarm-opened` and `alarm-closed` ("Alarm opened", "Alarm closed"). These come from the merge with Track A's work, not from this pull request's own change, and they are consistent with the alarm contract section.

## Observations (no action required)

1. The pull request body's gate paragraph describes the pre-rebase runs and should be refreshed with the new section when finding 1 is answered.
2. No real name, machine name, address, email domain or user path in the new head's diff against its base. Placeholders only.
