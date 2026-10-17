# Answers to the review of pull request 4115

The review is `review-4115.md`. It found no code defect, made two findings about the evidence, and listed six
observations. Every point is answered below, in the review's order. The branch was rebased onto origin/main
0702c88ce first, which brought in Track A's license alarm (#4110).

## The rebase

#4110 touched five of the same files: `LicenseStore`, `LicenseDisplayFold`, `GatewayDbContext`, both migration
snapshots, and `contract.md`. How each conflict was resolved:

- **The two log-kind lists** (`LicenseLogKinds` and the fold's kind texts) gained both sides: the alarm kinds and
  the cannot-switch kinds.
- **`contract.md`:** #4110 added its own section 9 (the license alarm), so this pull request's section is now
  **section 10**, "Phase 6 - whether a machine can run a switch". Every code comment and document that cited
  "contract section 9.1" for this change now cites 10.1. Track A's citations of its own section 9 were left alone.
- **The migrations were made again, not merged.** The old `20261017093812` and `20261017093932` migrations were
  deleted, both snapshots were taken as they are on main, and `dotnet ef migrations add AddLicenseMachineCannotSwitch`
  was run again for SQLite and for Postgres. The new `20261017121400_...` (SQLite) and `20261017121456_...`
  (Postgres) come after #4110's alarm migrations. Each adds only the one column (`license_machines.CannotSwitchReason`,
  32 characters). The snapshots gain only those four lines.

## Finding 1 - the Gateway unit suite was not run on the commit that merges

**Agreed, and it was run on the new head.** The gate rule from the Delivery Lead is the default gate plus
`.\scripts\test-local.ps1 -Parked -Filter License`, with TEMP on drive D. Both were run on the rebased head, and the
results are in `tool-health.md`, section "The gate", which replaces the evidence from the earlier commits.

## Finding 2 - the proof does not say what a crash during the swap leaves behind

**Agreed, and it now does.** `tool-health.md` has a new section, "A crash in the middle of the swap".

- **What is on disk:** it gives the state for each place the process can die, and whether the tool is there.
- **Why the next tools refresh repairs it:**
  - the bundle version is recorded only after the swap, so `ToolUpdater` sees the machine behind and runs the
    installer again;
  - `RemoveLeftoverTrees` removes the leftover folders, and it runs only after a verified install, so the old venv
    is never deleted first.
- **What the machine reports meanwhile:** "cannot switch", and the Gateway sends it nothing.
- **What the repair depends on:** the Director being alive, which #4118 and #4117 cover.

I checked the reviewer's reading against the code before writing it (`PythonToolsInstaller.cs` steps 8 to 11,
`ToolUpdater.cs` `versionBehind` and `unhealthy`, `LeftoverSuffixes`), and it holds. As the reviewer allowed, this is
stated rather than tested. Interrupting the process between two directory renames needs a seam in the swap, and that
is new work, not this pull request's.

## Observations

1. **A Director that restarts while the tool is missing reports nothing, so it shows "Not reporting", not "Cannot
   switch".** Agreed, by design and kept. An empty holdings list would tell the Gateway every holding was removed,
   and that is worse than a less specific status. The machine is still skipped, so no wrong switch is sent.
2. **The "not-known" reason showed its code word in the log row's `reasonText`.** **Fixed.**
   `LicenseDisplayFold.ReasonText` now maps `not-known` to "The machine's Director says it cannot run a switch, for
   a reason this Gateway does not know." The test
   `AReasonThisGatewayDoesNotKnow_IsStoredAsNotKnown_AndTheMachineIsStillNotSentASwitch` asserts that sentence on the
   logged row. With the mapping removed, both cases of that test fail.
3. **The relocation matches the staged path byte for byte, ordinal.** Kept. Both strings come from the same
   `InstallLayout` value, and a mismatch fails safe: the update is refused, and the tools are untouched and named in
   the setup log. Making the match case-insensitive on Windows would make the rewrite match text it did not write.
   The measured Windows run went through the real path.
4. **A 46 ms blip can produce two log rows.** Agreed. They are rare, and they are honest about what was seen. Kept.
5. **The install lock finding is real and older than this change.** Agreed. It stays finding 1 in `tool-health.md`,
   for the Tech Lead to schedule.
6. **No real name in either diff.** Checked again after the rebase and the edits: nothing.

## What the review read but did not run

The reviewer ran nothing from this pull request. The gate runs on the new head are mine, as noted under finding 1.

## Round two

The round-two review is `review-4115-round-2.md`. It found the rebase clean:

- the rebuilt migrations;
- the merged log-kind lists;
- the renumbering to contract section 10.

It found round-one finding 2 (a crash mid-swap) answered, and observation 2 (the raw "not-known" word) fixed and
tested. Its points:

1. **Finding 1 - no gate run on the merging code was written down.** **Closed.** The review read 255da59de, before
   the Tech Lead's gate results were added. They are now in `tool-health.md`, section "The gate":
   - the default gate, green at 700cba2be (all 11 suites Completed);
   - `-Parked -Filter License`, green at 255da59de (Gateway unit 872, Gateway 21, Core unit 190).

   The only change between those two commits is the proof document. The earlier pre-rebase runs are replaced, not
   kept beside them.
2. **The pull request body still said "contract section 9.1" and described the pre-rebase gate.** **Fixed.** The body
   now says section 10.1 and gives the gate runs above.
3. **The bin folder section's revert proof is self-testimony.** Agreed, as with every revert proof in this mission.
   The section says where the proof was run (at 700cba2be) and how (main's installer back, with the earlier assertion
   skipped for that one run). One wording difference from the review's summary: the window the section describes is
   the installer deleting every bundle shim from `bin` before pip, on main, not the in-use probe's rename-and-back.
4. **The alarm kind texts in the fold** come from the merge with Track A's work. Agreed: they are not this pull
   request's own change.
5. **No real name.** Checked again: nothing.

## The second rebase, before merging

The branch was rebased again, onto origin/main 9a5032dc2, after the round-two review. Main had moved by eleven
license changes since 0702c88ce, among them Track C's approvers (#4109, #4127, #4140), the hand-switch heal (#4106),
"switch every ready machine at once" (#4105), the readiness check (#4151) and the refused-report and silent-machine
statuses (#4113). Ten files conflicted. The six commits were first squashed into one on their merge base, so each
conflict was resolved once. Each was resolved by keeping both sides:

- **The report message.** It carries both main's `SwitchedByHand` and this pull request's `CanSwitch` and
  `CannotSwitchReason`.
- **The machine state.** It takes both `ReportsRefused` and `CannotSwitchReason`. The snapshot passes both.
- **The fold's status order.** It is now: report refused, not reporting, switching, **cannot switch**, signed out,
  reporting. "Cannot switch" also gained a detail sentence like its neighbours: "It is not sent a switch until its
  Director says it can run one again."
- **The log kinds** are main's eight plus `cannot-switch` and `can-switch-again`, in the store, the fold and
  `cockpit-api.md`.
- **The reporter.** Main's hand-switch handling and this pull request's tool-missing path both stay. The shared
  `SendAsync` now clears a hand switch once it is sent, which main's inline send used to do.
- **The tests.** The new hand-switch tests and this pull request's can-switch tests are both kept. One new test fake
  from main (`LicenseHandSwitchTests.AnswerTool`) gained `IsInstalled => true`.
- **The contract.** Track C took section 10 and Track A section 11, so this pull request's section is now
  **section 12**, "Phase 6 - whether a machine can run a switch". Every citation in code and in `cockpit-api.md` and
  `tool-health.md` now says 12.1. The review files keep the numbers they were written with.

Because this rebase changed code, the gate was run again on the result; see `tool-health.md`, "The gate".
