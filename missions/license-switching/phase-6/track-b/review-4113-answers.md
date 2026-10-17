# Answers to the review of pull request 4113

The review is `review-4113.md`, beside this file, reviewed at `0b1f49b33`.

## Finding 1 - a refusal was keyed and cleared by Director, but the Director behind a machine changes: ACCEPTED, fixed

The reviewer is right. One machine and user can be reported by several Directors in turn, because the key holder
changes when it stops (contract section 2.4). Keying a refusal by Director left the page with one machine shown
twice, an open "Refused" row that nothing could ever end, and a refused machine that could also be called silent.

**The fix: a refusal belongs to the machine and its user.** One rule, `LicenseStore.IsRefusalOf`, is now used by
every reader: the store (to count, end and open), the page, the decision and the silence check. A refusal row that
names its machine is about that machine and user, compared exactly as a machine row is keyed (trimmed, lower case),
whichever Director sent it. Only a row that names no machine is matched by its Director. That happens only when the
report names no machine (or no user, or one too long) AND the Director has no machine row that would name it.

- `RecordRefusal` names the machine from the report when it can, else from the Director's last machine row. It then
  finds the open refusal by that machine and user. The same refusal from the next Director is counted on the same
  row, and the row takes the Director that is sending now.
- `RecordAccepted` ends every open refusal of the accepted report's machine and user, whichever Director sent it.
- `LicensePicture.RefusalOf(machine)`, the snapshot's `ReportsRefused` and `WatchSilence` all take the machine and
  use the same rule. A stand-in row for a machine with no machine row is keyed by machine and user, else by Director,
  so it is one row per machine.
- Found while doing this: a report with a null user was named by its machine alone and became a second stand-in
  machine. `NamesOf` now names nothing when the user is missing, so such a report attaches to the Director's machine
  row.

**The tests, each in one sentence** (in `LicenseRefusedAndSilentMachineTests`):

- `TwoDirectorsOfOneMachine_RefusedTheSameWay_AreOneRefusal_AndOneMachine`: the first and then the second key holder
  of one machine are refused the same way, and the log has one refusal row counting 2 reports under the second
  Director, while the page shows one machine.
- `ARefusalSentByOneDirector_IsEndedByTheNextDirectorsAcceptedReport_AndNothingIsLeftOpen` (the review's step 2): the
  first Director is refused and stops; the next one's accepted report ends that refusal, writes one acceptance row,
  and leaves nothing open.
- `AMachineWhoseNextDirectorIsRefused_ShowsRefused_IsNotSwitched_AndIsNeverCalledSilent` (the review's step 3): the
  first Director was accepted and the next one is refused. The machine shows "Report refused", is skipped by the
  decision with the refusal's reason, is sent nothing, and gets no silence row twenty minutes after its last
  accepted report.

**Revert check.** With the code committed, `IsRefusalOf` was put back to "same Director" alone and the class was run:
exactly these three tests failed ("Failed: 3, Passed: 29"). The file was restored from the commit. With the fix, all
860 license tests pass.

**What is left, and why it is acceptable.** A Director that sends a report naming no machine (or no user), was never
accepted, and then stops for good leaves its stand-in row open. That needs a Director that never once sent a
well-formed report. The row still says how long ago its latest refused report came, so it reads as old rather than
live.

## Note 1 - "the same refusal" is judged on the sentence, and the sentence carries data: NO CHANGE, agreed

Agreed with the reviewer's reading. Holdings come from one ordered answer of our own tool, active first, so the
duplicated email and the in-use list come out in the same order on every report of the same fault. A row per change
of code or sentence is the brief's rule ("another if the reason changes"). If an order that varies is ever seen,
the fix is to put the emails in each sentence in a fixed order. I have not done that here, because it would hide
nothing that is seen today.

## Note 2 - the ingest now waits for the write gate before refusing: NO CHANGE, agreed

Agreed. The gate is per Director, Hello never waits on a database write, and a containment after a successful
`ApplyReport` costs one report's history entry and a sequence that is not advanced. The next report is taken
normally. That is the same shape as any other store failure inside the gate.
