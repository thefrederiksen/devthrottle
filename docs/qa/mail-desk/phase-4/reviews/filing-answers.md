# Mail Desk, phase 4, task A (filing in live mode) - the Developer's answers to the review

Answers to `docs/qa/mail-desk/phase-4/reviews/filing-review.md` (review of pull request 4274 at `69b41720f`), by the
Developer of task A, 20 October 2026. Every finding is accepted and fixed with a test; the observation is answered with
a test and a corrected comment. The refinement the Tech Lead accepted before the review is recorded first.

## The refinement: queue only a key the check still knows (accepted by the Tech Lead, 20 October 2026)

**What the contract says (2.1):** every live `archive` or `junk` decision with outcome null whose key is not already in
the channel's `filingQueue` is appended.

**What is built:** the same, but a decision is appended only when its key is in the channel's key table as the check
returned it (`MailDeskMemory.Merge`, `Queue`).

**Why:** after a written-back run, the Gateway would otherwise queue an email the check has not reread yet (one past
the 10-message batch, or still behind the cursor). The check would report it `refused` ("no such key in this
channel's key table") and its row would end `failed` for good, though nothing was wrong with it. With the filter, the
email is queued on the run that rereads it, which is what 2.1 says should happen ("it is reread and queued now"). A
key cannot be pruned before it is queued: the check prunes only in a run whose memory is kept, and in that same run
the key is queued, and a queued key is never pruned (phase 2, section 3.3). Finding 1 below is the one case the
filter left without an end, and it now has one.

## Finding 1 - a live decision whose key never comes back stays pending for ever: ACCEPTED, FIXED

**The fix.** Each run, for each live channel handed whose check fields move on (no failure, not written back), every
live `archive` or `junk` decision with no outcome yet falls into exactly one of three cases
(`MailDeskService.ScreenAsync`, with the new `MailDeskMemory.Seen`):

1. **Its key is in the returned key table:** it is queued (as before).
2. **Its key is not there, and the check's returned cursor has read past its receive time** (`cursor.newest` later than
   the email's `receivedUtc`): the check read that stretch of the Inbox and did not report it again, so it will never
   come back. It ends with outcome `failed` and the detail "the email was not seen again to file it: the screening
   check read past it and no longer knows it (moved or deleted before it was read again, or read too long ago)"
   (`MailDeskService.NotSeenAgain`), written through `RecordFiling`. This covers both of the review's scenarios: the
   email deleted or filed by hand before the reread, and a key pruned while the channel was in observe mode (when it
   is switched back to live, the cursor is long past it).
3. **Otherwise** (the cursor has not reached it yet): it waits for the reread, and the run says how many are waiting.

The run summary no longer says "N waiting to be filed" for everything. It says
`<n> in the queue, <n> waiting to be read again, <n> not seen again`, so each number means one thing.

**Why the cursor, not the Tech Lead's suggestion of 7 days.** "Older than the check's 7-day key window" would end an
email that is still about to be reread, for example after a Director has been off for a week, when the reread begins
from the reverted cursor and reaches older mail in batches of 10. Measured by receive time, it would also wait up to 7
days to end an email that was plainly gone the next run. The cursor rule ends a decision at the moment it is certain,
and never before: the check reads the Inbox in receive-time order from its cursor (phase 2, section 3.1), so once the
cursor is past an email's receive time and its key is not in the table, the check has looked and not found it. An
email at exactly the cursor's time is left to wait, because the check may still report it. The ending is `failed`,
not `already-gone`, because in the pruned-key case the email may still be in the Inbox: the Mail Desk did not file
it, and says why.

**Tests (`MailDeskFilingTests`):**
- `ScreenAsync_WrittenBackEmailGoneBeforeTheReread_EndsFailedSayingItWasNotSeenAgain`: the review's scenario 1. The
  row ends `failed` with the sentence, is never queued, and is gone from `AwaitingFiling`.
- `ScreenAsync_WrittenBackEmailNotReachedByTheRereadYet_WaitsThenIsQueuedWhenRead`: the other side of the rule. A
  reread whose batch stops before the email leaves it waiting (not queued, not ended), and the next batch queues it.

## Finding 2 - the 08:00 section misses an email filed after the window it was decided in: ACCEPTED, FIXED

**The fix.** The decision row gains `OutcomeUtc`, when its outcome was written: when the check's report of the move
reached the Gateway (or, for a not-seen-again ending, the run that ended it). It comes with one migration pair,
`AddMailDeskDecisionOutcomeTime`, for SQLite and PostgreSQL, after the newest migration in this phase branch. A row
written before this migration keeps `OutcomeUtc` null, and phase 3 wrote no outcome, so nothing is lost. The 08:00 count
"Moved to Archive n - moved to Junk Email n" is now made from `MailDeskDecisionStore.FiledBetween`, which selects
outcome `filed` by `OutcomeUtc` in the window, whenever the email was decided. So an email decided just before one
08:00 email and moved just after is counted once, in the next. The "moved" word beside a junked email is unchanged:
the line belongs to the window the email was decided in, and says "moved" if it has been moved by the time the email
is composed.

**Tests:**
- `MailDeskEmailSectionTests.Section_ArchiveDecidedJustBeforeTheWindowClosesAndFiledJustAfter_IsCountedOnceInTheNextEmail`:
  the review's case, counted 0 in its own window and 1 in the next.
- `MailDeskDecisionStoreTests.FiledBetween_ByTheTimeOfFiling_NotTheTimeOfTheDecision`: by filing time, and an
  `already-gone` is not counted.
- `MailDeskDecisionStoreTests.RecordFiling_NoOutcomeYet_WritesIt_ThenASecondResultIsIgnored` now also checks the time
  written, and that the ignored second result does not change it.
- `AddMailDeskDecisionOutcomeTimeMigrationTests` (SQLite): applies right after `AddMailDeskDecisionAttempts`, keeps an
  earlier row with no outcome time, the model matches the migrations, and its Down removes the column.
- `MailDeskDecisionOutcomeTimePostgresTests` (PostgreSQL, run by `scripts/test-database.ps1`): the column, the queue's
  read, a move written with its time, a second result ignored, and the count by filing time. It passed. The one
  failure in that run, `AddLicenseAlarmsPostgresTests`, fails alone and fails the same way on `origin/mail-desk-phase-4`
  without this change, so it was already there and is reported to the Tech Lead.

## The observation - an unknown key discards the valid results in the same report: ANSWERED

As the review says, contract 2.2 makes a report with a result for an unknown key a broken contract, so none of its
results is written. The memory is not kept, so the kept queue still holds the items the check had moved. The next run
retries them, Graph has given the moved emails new identifiers, and they come back `already-gone`. They are written
`already-gone`, which is true ("not in the Inbox any more"), never `filed` and never moved twice. Writing the valid
results of a report we have just ruled untrustworthy would be the wrong trade, so the behaviour stays.

**Proved by** `MailDeskFilingTests.ScreenAsync_AfterAnUnknownKeyFailedTheRun_TheMovedEmailComesBackAlreadyGoneAndTheRowsEndRight`:
the failed run leaves both items in the kept queue; the next run screens; both rows end `already-gone` with the 2.3
detail; the queue is empty.

**The comment was wrong** on that path and is corrected (`MailDeskService.ScreenAsync`, above `FilingRefusal`). It now
says that a report that breaks its contract has none of its results written, and that a move it did make comes back
`already-gone` next run.
