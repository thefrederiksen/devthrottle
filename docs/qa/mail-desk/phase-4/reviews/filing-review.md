# Mail Desk phase 4, task A (filing in live mode) - review

Reviewer of pull request 4274 (branch `md4-filing` into `mail-desk-phase-4`), opened by the phase 4 Tech Lead.
Reviewed on 19 October 2026, at commit `69b41720f`.

## Scope

**Read:** the whole diff `git diff origin/mail-desk-phase-4...HEAD` (12 files: `MailDeskService.cs`,
`MailDeskMemory.cs`, `MailDeskDecisionStore.cs`, `MailDeskEmailSection.cs`, `MailDeskWords.cs`,
`MailDeskEntities.cs`, `FactoryEmailComposer.cs`, and the tests `MailDeskFilingTests.cs`,
`MailDeskDecisionStoreTests.cs`, `MailDeskEmailSectionTests.cs`, `MailDeskHandOffRig.cs`, `MailDeskHandOffTests.cs`),
plus the unchanged code it leans on: `MailDeskMemory.Merge` as a whole, `MailDeskDecisionStore.Record` and
`DecidedBetween`, and the key pruning and queue handling in `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`.
Checked against `docs/qa/mail-desk/phase-4/CONTRACT.md` sections 1, 2 and 7, phase 2 sections 3.3, 3.4 and 4.1, and
phase 3 sections 1 and 4.

**Ran:** `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
"FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~DueCheck|FullyQualifiedName~FactoryEmail"`
on devlinux: **967 passed, 0 failed, 0 skipped** (1 minute 41 seconds). The new filing tests are inside that filter.

**Could not reach:** the real check against a real or fake Graph (the stand-in check in `MailDeskHandOffRig.cs` was
read instead, and it matches phase 2 section 3.4 for what these tests use); the rig of contract section 1; the parked
suites outside the filter. I wrote no code and changed nothing but this file.

## Verdict

**Mergeable, with two findings for the Developer to decide on.** Neither moves an email wrongly or twice, acts on an
observe decision, or writes `already-gone` as `filed`. What I specifically looked for and did not find:

- An email moved twice: a retried result for a settled decision is ignored by `RecordFiling` (`MailDeskDecisionStore.cs:161`),
  and the check removes every attempted item from the queue; a written-back mailbox keeps the queue the check
  returned and reverts only `cursor`, `sentCache` and `keys`.
- An observe decision acted on: `AwaitingFiling` (`MailDeskDecisionStore.cs:180`) selects only `Mode == live`, and the service
  queues only for a channel whose current mode is live.
- `already-gone` written as `filed`: `FilingOutcome` maps each result word to its own outcome; the 08:00 fold counts
  and marks only `filed`.
- A queue past the memory cap: the queue is appended before the size check in `Merge`, so an oversized queue refuses
  the whole memory (proved by `ScreenAsync_QueueThatPushesTheMemoryOverItsCap_FailsTheRunAndKeepsNoMemory` and its
  under-the-cap twin, which isolates the queue as the cause).
- Body text, a sender or a subject in a log line: the new log lines carry ids, keys (hashes), channel ids, outcome
  words and counts only.
- Example data (section 7): every address in the new tests is `example.com`, `example.org`, `example.net` or a
  `*.example` domain.

## Findings

### 1. A live decision whose key never comes back to the key table stays unfiled forever, with no outcome and nothing saying so

**Where:** `src/CcDirector.Gateway/MailDesk/MailDeskMemory.cs:166` (the accepted refinement: skip an item whose key is
not in the returned key table), with `src/CcDirector.Gateway/MailDesk/MailDeskService.cs:261` and `:273`.

**The harm.** The refinement assumes a decision missing from the key table is only missing *for now* and will be
reread. That holds only while the email is still in the Inbox and the reread comes. When the key never returns, the
decision is skipped on every run for good: its outcome stays null for ever, `AwaitingFiling` hands it back on every
run of that channel for ever, and the run summary's "N waiting to be filed" counts it for ever, so that number stops
meaning anything. Before the refinement, the same item would have been queued, refused by the check with "no such key
in this channel's key table", and written `failed` with that sentence - a visible end. The refinement replaced a
visible failure with a silent, permanent "pending".

**Failure scenarios (both reachable without a broken check):**

1. A live mailbox decides an archive for email A and the model fails on email B in the same run. The mailbox is
   written back, so A is not queued (contract 2.1, correct). Before the next run the owner deletes A, or files it by
   hand. The reread starts from the reverted cursor, A is no longer in the Inbox, so the check never reports it and
   its key never re-enters the table. A's decision row says `archive`, `live`, outcome null - for ever.
2. The same written-back run, then the owner switches the mailbox to observe (contract 1: it stops queuing; the test
   `ScreenAsync_ChannelSwitchedBackToObserve_StopsQueuingItsLiveDecisions` proves this much). More than 7 days or 40
   reported messages later the owner switches it back to live. A's key has been pruned (phase 2 section 3.3), so A is
   skipped on every run from then on, again with no outcome.

**What would prove a fix:** a test in `MailDeskFilingTests` where a written-back email is absent from the reread, and
the assertion that its row ends with an outcome (for example `already-gone` or `failed` with a sentence) rather than
null; and the "waiting to be filed" count excluding what can never be queued. How to end it is the Developer's
decision.

### 2. The 08:00 section misses every email filed after the window it was decided in

**Where:** `src/CcDirector.Gateway/MailDesk/MailDeskEmailSection.cs:161` (`FiledLine`) and `:124` (the "moved" word),
fed by `MailDeskDecisionStore.DecidedBetween`, which selects by `DecidedUtc`.

**The harm.** Filing always lands one run after the decision: the decision is queued at report time, and the check
moves it on its next run. The fold counts "moved" only among decisions *decided* in the window, and the row has no
time of filing, so an email decided in the last interval before the 08:00 email is composed is not yet `filed` when
that email is built, and is outside the next day's window when it is. It is never counted as moved in any 08:00 email,
and its junk line never gets "moved" beside it. With the check running every few minutes this is the last interval of
every day, every day: the owner reads "Moved to Archive 3" when 4 were moved, which is the one sentence of this section
meant to say what the Mail Desk did to their mailbox. A channel failure or a cap refusal that delays filing by several
runs widens the gap.

**Why the test does not catch it:** `Section_LiveMailbox_CountsWhatWasMovedAndSaysMovedBesideAFiledJunk`
(`MailDeskEmailSectionTests.cs:406`) puts every decision and its filing inside the same window, so it never meets an
email decided in one window and filed in the next.

**What would prove a fix:** a test with an archive decided just before the window ends and filed just after, asserting
it is counted in exactly one 08:00 email. The contract's "the count of emails moved ... in its window" reads as moved
in the window; whether that needs a time of filing on the row is the Developer's and the Tech Lead's decision.

## Observation, not a finding

A report with one filing result for an unknown key fails the whole run before `RecordFiling` runs, as contract 2.2
requires, so the *valid* `moved` results in the same report are discarded too. The test
`ScreenAsync_ResultForAnUnknownKey_FailsTheRunAndKeepsNoMemory` shows it: `k-1` was moved by the stand-in check, yet its
outcome is asserted null. Next run the old queue is retried, Graph has given the moved email a new identifier, and
`k-1` is written `already-gone`, not `filed`. Nothing is moved twice and the row is not false, so I do not count it as
a harm; but the comment at `MailDeskService.cs:149` ("They are written whether or not the memory is kept, because the
moves were made") is not true on this path. It needs a broken check to happen.
