# The full Core suite fails, and it is not a flake

Found by the Architect on the rebased tree, immediately before landing. **Phase one is NOT merged
because of this.**

## What was observed

Two consecutive full runs of `CcDirector.Core.Tests` on the rebased branch, each about nine minutes:

| Run | Result | The test that failed |
|---|---|---|
| 1 | 4,470 passed, 1 failed, 8 skipped | `ContentTurnRuleTests.The_detector_holds_red_when_the_row_rule_it_was_handed_says_nothing_was_gained` |
| 2 | 4,470 passed, 1 failed, 8 skipped | `ContentTurnRuleTests.The_shadow_row_is_written_after_the_state_decision_not_before` |

**Two different tests, same class, two runs in a row.** That is a class-level instability, not one
bad test, and a single isolated re-run passing in 741 milliseconds does not contradict it.

## The diagnostic answers the question it was built for

Run two's message, in full:

> no shadow row was written past row 1 in ...\turn-detection-shadow\1f96c57c...jsonl; the directory
> holds 1f96c57c...jsonl:314b; **NO check is armed for this session, so no row will ever be written
> for this burst**

That positive signal is the fix for round two's finding seven - the one the report first tried to
close by lengthening a wait from five seconds to fifteen, which round three correctly called a
tolerance change rather than a diagnosis. It has now paid for itself on its first real use: it says
**never**, not late. Waiting longer would never have gone green.

## What is NOT yet established - this is a hand-over, not a diagnosis

The cause has not been observed and must not be guessed. One hypothesis worth testing FIRST because
it would make this a test defect rather than a product defect: `OnBytesCore` deliberately does
nothing on an already-active session - no screen read, no check, no row - which is the design, since
the rule must cost a working session nothing. A test that writes a burst before the session has
settled back would therefore correctly produce no row and no armed check, which is exactly what the
message describes.

**That is a hypothesis. It has not been checked.** The alternative - that a burst on a settled
session can fail to arm a check - is the very defect rounds one and two were chasing, and it would
be a product defect of the first order.

Two facts that bear on it and are worth having before diagnosing:

- The same suite passed in full on the SAME work before the rebase (4,471 passed, 0 failed,
  reported by the Manager). It fails after rebasing onto `origin/main` at `74da12d36`. The suite's
  total is unchanged at 4,479, so the rebase added no tests to it.
- Both failures happened only in a FULL-suite run on a busy machine. Every green run behind this
  work - the Manager's focused runs, all three inspections' runs - was a focused subset. The full
  suite under load is a condition none of them exercised.

## Method note, for whoever picks this up

The first run's assertion message was destroyed by piping the run through `tail`, which buffers and
discards. The evidence above exists because the second run was redirected to a file instead. Do not
pipe a long run through `tail` or `head` - it also makes `$?` the exit code of `tail`, so the first
run reported `exited with code 0` while carrying a failure.

## Closed - and the Architect verified it independently

The cause was NOT the product defect this document was written to fear. The run showed the session
SETTLED, a check ARMED, and nothing faulted. The two failures were the two directions of one
collision: the test's reader and the shadow log's writer fighting over a single Windows file handle,
where `File.ReadAllLines` denies writers and `File.AppendAllText` denies readers. The reader losing
threw a sharing violation; the writer losing had its append swallowed and the row lost for good,
which reads exactly like a check that armed nothing.

**One half of that is a product finding, not a test bug.** Reading one of these files used to cost a
row. Anyone running `Get-Content` over a shadow file, a scoring script, or a backup scan would
silently thin the very measurement the file exists to hold - and item five's whole job is scoring
those rows. The append now retries within a bounded budget; past the bound the row is still lost and
still logged.

### Independent verification by the Architect

Three full end-to-end runs of `CcDirector.Core.Tests` now pass, two by the Manager and one by this
seat, against a reproduction that failed twice in a row on the same machine under the same load:

| Run | By | Result |
|---|---|---|
| reproduction | Architect | 4,470 passed, **1 failed** |
| reproduction | Architect | 4,470 passed, **1 failed** (a DIFFERENT test) |
| after the fix | Manager | 4,475 passed, 0 failed, 8m05s |
| after the fix | Manager | 4,475 passed, 0 failed, 8m46s |
| after the fix | **Architect** | **4,475 passed, 0 failed, 8 skipped, 10m54s** |

The Architect's run was redirected to a file rather than piped, so the exit code is `dotnet`'s own
and the log is complete: zero occurrences of `[FAIL]` or `Failed!` in the whole 2,990-byte log.

**What three green runs do and do not establish**, in the Manager's own words and this seat agrees:
they NARROW an intermittent failure, they do not settle it. What settles it is the mechanism and the
reverts that reproduce both observed symptoms. Stated here so a later reader does not take the table
above as more than it is.
