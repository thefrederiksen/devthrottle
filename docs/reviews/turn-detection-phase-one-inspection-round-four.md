AGREE

## Scope and positive evidence

I inspected HEAD `074337a03` on the inspection worktree, delta `0f3fc22e5..HEAD` - commits
`c3643917e`, `43a11c676`, `b44781cf6` - reading both product files and both test files in full, plus
the Manager's account in `docs/missions/turn-detection-2026-09-15/build-report.md`. I read the shipped
files from `origin/main` at `74da12d36` with `git show` where I needed shipped state; I never opened the
shared checkout `D:\ReposFred\devthrottle`.

Runs I made, all against this worktree, every log redirected to a file and the exit code read from
`dotnet`:

- Focused: `ContentTurnRuleTests` (26 passed) and `TurnDetectionShadowRetentionTests` (19 passed),
  zero failed, zero skipped.
- Revert one - `ShadowRows` put back to `File.ReadAllLines`: `The_row_reader_does_not_shut_the_writer_out`
  red with `System.IO.IOException : The process cannot access the file ... because it is being used by
  another process.` and `A_half_written_row_is_not_counted_as_a_row` red with
  `Assert.Single() Failure: The collection contained 2 items` - both the report's stated symptoms,
  verbatim. Restored with `git checkout --`.
- Revert two - `Append` put back to `File.AppendAllText`:
  `A_reader_holding_the_file_does_not_cost_a_row` red on three consecutive runs, all three with
  `Assert.Equal() Failure: Values differ` - the report's stated symptom and its "red on all three
  runs". Restored, and all four touched files hash-checked against HEAD (`6aadeae5b8`,
  `7991bdf7e7`, `1a5d7e442b`, `1db1a15ab`) before and after.
- Three mechanism probes, each a temporary test file against a throwaway `CC_DIRECTOR_ROOT`, deleted
  afterwards and the tree verified clean. Their results are quoted below where they bear.

## The diagnosis - settled, armed, no fault: verified independently

The Manager's conclusion is the one the brief said to attack first, and it holds. The chain, each link
checked in code rather than taken from the report:

1. `OnContentCheckCore` (`TerminalStateDetector.cs:829-846`) takes the burst off the books BEFORE
   `RunContentCheck` runs - `_checkScheduled` cleared, `_checkPending` cleared, `_pendingBytes`
   zeroed. So once the check has fired, a failed append has no retry: nothing owes the row. That is
   the "gone for good" half of the account, and it is true of the code.
2. `TurnDetectionShadowLog.Append` (`TurnDetectionShadowLog.cs:225-231`) catches its own exceptions
   and swallows them after a `FileLog` line, so a lost append never faults the check. The fault
   counter is incremented only by `RestorePendingCheck`, reached only when the check itself throws -
   so "no content check has faulted" plus "no check is armed" plus "the last burst was SETTLED, rule
   on; check armed" leaves exactly one place the row could have gone: inside `Append`.
3. `Append` can fail to produce a row in two ways only: `Enabled` false, or the write threw. The
   stamp at `TerminalStateDetector.cs:625` is truthful - `ScheduleContentCheck` arms unless the
   process is disposed, and a stamp on the already-scheduled path still describes a check that IS
   armed - so a check genuinely ran to completion.

I then attacked the two alternatives that would produce the same message, because the report does not
mention them:

- **`Enabled` flipped false by a concurrent test.** `ContentTurnRuleTests.cs:70` does set it false, and
  it is a process-wide static. But the whole assembly sets
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  (`src/CcDirector.Core.Tests/TestParallelization.cs:18`), and every class that writes `Enabled` or
  `CC_DIRECTOR_ROOT` sits in the one serialized `CcStorageRoot` collection. No test can run
  concurrently with `ContentTurnRuleTests` at all. The alternative is excluded structurally, not by
  luck.
- **`CC_DIRECTOR_ROOT` moved mid-test** so the append landed in another root. The same serialization
  excludes it, and the failure message listed the session's own file present with its first row, so
  the root did not move between row one and the listing.

With both excluded, the diagnostic run's message proves the row was lost inside `Append`, and the
same run's second failure - the sharing violation out of `File.ReadAllLines` in `ShadowRows` - shows
the handle fight was live in that run. The mechanism explains BOTH original suite failures (both
were the writer losing: no row, no pending check, no fault) and the reader-losing failure observed in
the diagnostic run. My probes reproduced both directions outside the suite, with the exact Windows
message.

What would have to be true for this to be wrong: some way for `Append` to complete silently without a
row, or for a test outside the serialized collection to touch `Enabled` or the root. I found neither,
and the parallelization switch is checked, not assumed.

## The retry, and its bound

- **Reachable, not decoration.** A probe held the file the way an ordinary reader does for 120
  milliseconds and called the real `Append`: the row arrived, and the call took at least 100
  milliseconds - it genuinely waited for the holder rather than landing on a first lucky attempt.
- **The bound holds.** With the budget cut to 60 milliseconds against a holder that never lets go,
  the row was lost and the call returned between 55 and 400 milliseconds - it retried, gave up, and
  did not stall.
- **A stuck handle stalls other sessions' appends for at most the budget plus one pause**, under the
  process-wide `Gate` - the trade the budget comment states (`TurnDetectionShadowLog.cs:119-121`).
  That is bounded and documented.
- **Past the bound the row is lost and logged.** The exception escapes `AppendLine`
  (`TurnDetectionShadowLog.cs:271`) to `Append`'s catch, which writes
  `[TurnDetectionShadowLog] append failed for {sessionId}` (line 231). I found no comment or report
  sentence claiming otherwise; the loss is stated in three places and pinned by
  `An_append_past_the_contention_budget_loses_the_row_and_says_so`.
- **Budget edge cases behave sanely.** A zero budget degrades to the pre-fix single attempt; the
  deadline is taken before the first attempt so the total wait is about the budget; only `IOException`
  is retried, so an access-denied or argument failure fails immediately as before.

## The share-mode claim - the reasoning holds, and the guard's deletion is justified

The claim (`TurnDetectionShadowLog.cs:253-281`, commit `43a11c676`) is that widening what the append
PERMITS buys nothing, because a reader holding `FileShare.Read` denies the write whatever our flags
say. Windows sharing makes this exact: a new open is admitted only if its requested access is
permitted by every existing handle's share mode, and the reader's `FileShare.Read` permits only
reading - no flag on our side reaches into the reader's share mode. My probe checked both legs:

- A reader that permits writers: the old `File.AppendAllText` succeeded, and the widened open
  succeeded. Identical.
- A reader that denies writers: the old append threw, and the widened open threw too. Identical.

So any guard pinning the widened share mode passes before and after - it cannot fail on either side,
which is what the report claims for its deleted guard. The deletion, not the widening, is the right
call, and the reasoning is now measured by me rather than taken from the report.

**Byte-for-byte**: a probe appended the same line to two empty files - one via
`File.AppendAllText(path, line, Encoding.UTF8)`, one via the new open - and the files' bytes were
identical, and identical again after a second append to the now-non-empty files. The byte-order mark
goes on only when the file is empty (`stream.Position == 0`, line 263), which is exactly
`File.AppendAllText`'s behaviour. The file on disk is unchanged in shape.

## The reader-side fix

- **A half-written row cannot be counted.** `ShadowRows` counts only newline-terminated lines
  (`ContentTurnRuleTests.cs:1105-1117`): the walk consumes a line only at its `\n`, so the trailing
  fragment of a torn last line is skipped. The committed test pins it, and the revert made the
  same test count 2 items - the fragment - so the pin is real.
- **A row cannot be counted twice.** One pass over the text with a running `start` index; each
  newline terminates exactly one row and advances the index. There is no path that re-reads a span.
- **The newline rule is true of the writer's output.** Every append's payload is
  `JsonSerializer.Serialize(record) + "\n"`, and the serializer escapes a newline inside a string
  value, so a complete row never contains a raw newline and always ends with one. Half of a row is
  therefore the only way a trailing fragment arises, and it is the one case the reader excludes.

## Does the fix close the failure or hide it

Two green full runs narrow it; what settles it is the mechanism and the reverts, and I verified both
independently rather than taking the report's word. The revert of the test's reader reproduced the
sharing violation verbatim; the revert of the product's writer reproduced the row loss on three
consecutive runs; the mechanism reproduces both observed symptoms and excludes every alternative I
could construct (the two above, plus the fault path, which the fault count and the disposition stamp
rule out). The suite failure is closed by mechanism, not by green runs. The green runs are
consistent with it and no longer carrying the weight.

## Round three's residual, and what the delta broke

**"A locked live file still costs the row" - NARROWED, and the narrowing is pinned.** A holder that
releases within the 500-millisecond budget no longer costs the row: the committed test
`A_reader_holding_the_file_does_not_cost_a_row` proves it, and my probe confirms the real `Append`
waits past a 120-millisecond hold and lands the row. A holder that never releases still costs it, and
that residual is now pinned by a committed test rather than implied. This is a real narrowing of the
round-three finding, not a move.

**Nothing round three closed is broken by the delta.** The sweep (`OwnedFileName`,
`SweepAgedFiles`) and the rollover are untouched; the check path's logic - `RestorePendingCheck`, the
`finally` arms, `RunContentCheck`'s order - is untouched, and the only additions are the disposition
stamps, the fault-string write inside an existing catch, and the test-only reads. The 45 focused cases
across both classes are green on this tree. The seven closures stand against the changed code.

## Findings - sentence-level, none reopening the verdict

1. **A false "exactly" in the new reader test.**
   `ContentTurnRuleTests.cs:979-987`: "The holder here opens exactly as the log's append does" - the
   holder opens `FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete` while the
   log's append opens `FileShare.Read` (`TurnDetectionShadowLog.cs:260-261`). Mode and access match;
   the share mode does not. The test still discriminates: I checked that a holder with the append's
   real `FileShare.Read` passes the fixed reader and reddens the reverted one identically, so the
   assertion's power is unaffected - but the sentence is false as written. What would make this
   finding wrong: the holder's share flags actually equalling the append's.

2. **A justification that is false on four branches.**
   `TerminalStateDetector.cs:405-406`: the `_lastByteDisposition` comment says the stamp is
   "written beside work that already arms a timer on the same path, so it is not a cost the
   'a working session pays nothing' claim has to account for". On the suppressed, brand-new,
   already-ACTIVE-with-no-body-change, and settled-nothing-armed branches (lines 530, 543, 566, 594,
   616, 621), the stamp is the only added work and nothing is armed. The cost claim itself survives -
   one volatile reference store is not what that claim is about - but the stated reason does not hold
   on those branches. What would make this finding wrong: every stamped branch arming a timer, which
   the code contradicts.

3. **The retention helper does not follow the discipline its comment points at.**
   `TurnDetectionShadowRetentionTests.cs:349-353`: `ReadRows` says "Read the rows without denying the
   writer - see ContentTurnRuleTests.ShadowRows" but reads with `Split('\n', RemoveEmptyEntries)`,
   which COUNTS a non-newline-terminated last line - exactly the half-written row `ShadowRows`
   refuses to count. No committed test is currently exposed (both uses follow appends that fully
   landed or never landed), but a future poll through this helper against a live writer has the
   hazard the cross-reference claims to inherit. What would make this finding wrong:
   `Split` dropping an unterminated tail, which it does not.

4. **A count slip in the report.**
   `build-report.md:787`: "the other nineteen in `TurnDetectionShadowRetentionTests` stayed green" -
   the class runs 19 cases in total (measured), of which 2 are new, so the others are 17. The
   `ContentTurnRuleTests` count (24 others, 26 total) is right. Arithmetic, not a proof claim.

## Residuals that do not reopen a finding

- **A row lost past the budget leaves evidence only in `FileLog`** - the shadow file itself carries no
  gap marker, so a thinned measurement reads as a measurement that was always that size. This is
  stated in the code and pinned by the bound test; it is the same shape as round three's
  observation-harm residual and is not made worse by this change.
- **A torn-write retry can corrupt the last line.** If `stream.Write` or its flush throws `IOException`
  after some payload bytes have landed (a full disk being the realistic cause), the retry re-writes the
  whole payload, leaving `partial + full + newline` as one unparseable row instead of the clean loss the
  old code produced. Both outcomes lose the row; the new one can also poison the final line. Disk-full
  on a four-megabyte-capped local file is the one path there, and nothing claims it is covered.
- **The 500-millisecond budget is a judgement, not a measurement** - no real foreign reader's hold
  length has been measured. The report says so itself, and the bound's cost is pinned.

## Verification boundary

I ran the two focused classes, the revert probes, and three mechanism probes on this worktree. I did
NOT run the full `CcDirector.Core.Tests` suite end to end, `scripts\test-local.ps1`, either Gateway
suite, live terminal bytes, or the labelled corpus; the two green full-suite runs are the Manager's
own and I read them as self-testimony. What I verified independently is the mechanism and the reverts,
which is what the brief says settles an intermittent failure - and they do. The Gateway suites retain
no verdict on this change, unchanged from the earlier rounds.
