# Review of pull request 4243: the Mail Desk screening hand-off (task F)

Reviewed commit `6a8f26919` on branch `mail-desk-p3-hand-off`, against sections 1, 2, 3, 4, 8 and 10 of
`docs/qa/mail-desk/phase-3/CONTRACT.md` as it stands on `origin/mail-desk-phase-3` (commit `f8b28e1ed`, which adds the
timed-start row; the pull request's branch does not yet hold that one-line commit), and sections 3 and 4 of
`docs/qa/mail-desk/phase-2/CONTRACT.md`. The reviewer read; it built nothing and fixed nothing. The two throwaway tests
mentioned below were written in the review's own worktree, run, and deleted; the tree was checked clean before this file
was written. The seat that built the work decides what happens to each finding.

## Findings

### 1. A paused Mail Desk start still screens mail: it asks the model, spends, and writes decisions

**What breaks.** Pausing a trigger (`POST /triggers/{id}/pause`) is the owner's stop button. A paused trigger is still
claimed and handed out (`TriggerStore.ClaimForDirector` does not look at `Paused`), its check still runs, and on the
ordinary path `TriggerService.Decide` turns any count above zero into a `paused` run that starts nothing. The Mail Desk
path never reaches `Decide`: `ReportCheckAsync` hands every Mail Desk report to `HandToMailDesk`
(`src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs`, about line 426), and `HandToMailDesk` (lines 480 to 517)
looks at the check's failure and at `_screening`, never at `trigger.Paused`. So a paused Mail Desk start screens every
new email as if it were running.

**Proof.** A throwaway test over the pull request's own `MailDeskHandOffRig`: add the Mail Desk trigger and one channel,
pause the trigger with `TriggerStore.SetPaused`, report one email from an unknown sender. The run came back `screened`,
the scripted model's call log was not empty, and one decision row was written. The test passed (that is, the defect is
real); it was deleted afterwards.

**Why it must change.** An owner pauses the Mail Desk start for the reasons one pauses anything: the screening is
deciding badly, or it is spending. Today the pause stops neither. Every call still counts toward the spend limit, and
every decision is recorded with the channel's mode, so a channel in `live` mode will be acted on in phase 4 from
decisions made while the owner believed the start was stopped. Nothing in the contract makes the Mail Desk exempt from a
pause, and phase 1's channel fold already has words for "new mail, but the start is paused", which this path can never
produce. A test that pauses the Mail Desk start and asserts no model call, no decision row and a `paused` run (or
whatever outcome the Tech Lead rules a paused screening records) would have caught it.

### 2. A check that leaves a handed channel out of BOTH its context and its memory is screened, not refused, and raises no problem

**What breaks.** Section 4 of the contract: "A channel handed to this machine and dropped from the report keeps its kept
fields (the check returns every channel it was given, phase 2 3.2; a missing one is a defect, logged and refused)."
`MailDeskService.ScreenAsync` (`src/CcDirector.Gateway/MailDesk/MailDeskService.cs`, lines 108 to 117) takes the set of
channels it treats as handed from the report's own context sections, then checks that the returned memory holds exactly
those. When the expected set (the channels that name this machine now) differs, it only writes a log line (line 111) and
goes on. So a check that drops channel B from both the context and the memory passes both checks: the run is `screened`,
B's kept fields stay as they were (the merge keeps entries it was not handed), no `mail_desk_channel_checks` row is
written for B, and no problem is raised.

**Proof.** A throwaway test over the rig: two channels, a stand-in check that reads mailbox A, then channel B removed
from both `report.State` and the context inside `report.Output`. The run came back `screened` and `OpenProblems()` was
empty. The test passed; it was deleted afterwards. The pull request's own test for this promise,
`Merge_ACheckThatDropsAHandedChannel_IsRefusedAndNothingIsScreenedOrKept` (`MailDeskMemoryTests.cs`, line 250), removes
B from the memory only, so the context and the memory disagree and the refusal fires; it never tries the case where
they agree.

**Why it must change.** A mailbox that silently stops being read is the failure this phase's per-mailbox problems exist
to prevent: the owner sees no problem, the channel row's `signedIn` still says `yes` from the last good check, and mail
in B is never screened. The code cannot tell a dropped channel from one whose machine list changed between the hand-out
and the report, because the Gateway does not record what it handed; that is why it only logs. But the contract's answer
is "refused", and the harm of refusing a run in the rare honest case (one failed run, the next hand-out is correct) is
far smaller than the harm of never noticing a dropped mailbox. Either the contract changes to say what happens, or the
Gateway keeps what it handed (or compares against the channels that named the machine when the assignment was handed)
and refuses the mismatch.

### 3. The Mail Desk channel row says "the seat it started last is still running" for every skipped-running screening

**What breaks.** In this phase a Mail Desk start never starts a seat, so its only `skipped-running` run is the new one:
"the previous screening is still running". `MailDeskChannelFold.OutcomeInWords`
(`src/CcDirector.Gateway/MailDesk/MailDeskChannelFold.cs`, line 165), which words the Mail Desk start's last check on
the channel row, still answers `skipped-running` with "new mail, and the seat it started last is still running". The pull
request gave the activity record the right sentence (`TriggerService.ActivityFor`, which tests
`run.Reason == ScreeningStillRunning`) and added the `screened` words here, but did not change this line.

**Why it must change.** The Gateway owns the words (repository rule 7) and the client shows them verbatim, so the owner
is told a seat is running when none is, on the one screen meant to show what the Mail Desk is doing. With screenings of
up to four minutes and intervals that can be shorter, this label will be common, not rare. The fix is one line in the
fold, reading the trigger's last reason as `ActivityFor` does; a test on the channel row after a skipped-running report
would hold it.

### 4. Any change of the spend limit clears the stop, even one that leaves the limit at or under what was spent

**What breaks.** `MailDeskSettingsStore.Set` (`src/CcDirector.Gateway/MailDesk/MailDeskSettings.cs`, lines 170 to 173)
deletes the `mail_desk_spend_stopped_utc` row on every change of the spend limit, and `MailDeskSpend.Stopped`
(`MailDeskProblems.cs`) is true only while that row exists. So an owner whose Mail Desk stopped at $5.00 with $5.0003
spent, who sets the limit to $5.00 again or lowers it to $4.00, closes the spend problem and makes
`GET /gateway/mail-desk/settings` answer `stopped: false` with the label "$5.0003 spent of the $4.00 spend limit". No
model call is made (the ledger holds the limit on its own), so no money is lost.

**Why it must change.** Section 7 says the spend problem "is open on the `mail-desk` factory until the limit is raised
above what was spent". Here it closes without that, and stays closed until a later screening happens to need a model
question; on a quiet mailbox, or one decided by rules and lists, that can be a long time. Meanwhile every email that
needs the model is decided `failed` with the spend-limit reason and, by the contract, is not retried, while the Open
problems card says nothing. The fix is to clear the stop only when the new limit is above the spend, or to drop the stop
row's role in `Stopped` for any limit at or under the spend. This is the lowest of the four.

## Notes that are not findings

These proved no harm against the contract, or could not be shown to. They are recorded so the Tech Lead sees them once.

- **A Gateway stop, or the four-minute deadline, spends an attempt on every email not yet asked.** The model maps a
  cancelled token to the `cancelled` failure kind (`MailDeskModel.cs`, line 318), so after the deadline, or while the
  Gateway shuts down, each remaining email gets a `failed` row with its attempt count raised, without the model ever
  seeing it. Three such runs keep an email for the owner. The contract says exactly this ("a question not asked by then
  is a failure of that email"), and the email goes to the owner, which is the safe side, so it is not a finding. A deploy
  during a screening does count as one attempt.
- **Two Mail Desk starts in one account.** `MailDeskStart.Is` matches any definition trigger of `mail-desk` whose check is
  `checks/screen.py`, and `_screening` is kept per trigger. An owner who edits the draft to hold two interval starts of
  the screening check gets two kept memories over the same mailboxes, and two screenings that can run at once: both find
  no decision, both ask the model, and the second `Record` is `Kept`, so its call's cost is never counted toward the
  limit. The template has one start and phase 1's channel fold already assumes one, so this needs an owner's edit; noted,
  not a finding.
- **The Mail Desk test reads the trigger found before the lock.** `ReportCheckAsync` decides `mailDesk` from `found`
  (read before the per-trigger lock), not from `trigger` (read again under it). A republish that turns an ordinary
  trigger into the Mail Desk start between the two reads would send that one report down the ordinary path. I could not
  find a way for a republish to change one trigger row's check path in place, so this is noted only.
- **A timed start's run is left waiting on its check.** `DueCheckService.Report` refuses a Mail Desk timed start with
  409 `mail_desk_not_ready` and records nothing, as the contract's new row says. I did not read how the Director treats
  that refusal (whether it reports again every poll), so I cannot say whether the run is ever closed.

## What was checked and found correct

- **The hard order: one session start, and the Mail Desk path never reaches it.** `_startSession` has one caller,
  `FinishStartAsync` (`TriggerService.cs`, line 686), reached only from the ordinary path after `BeginStart`. The Mail
  Desk branch returns from inside the lock before `Decide` and `BeginStart`, for every count, with the switch on; the
  switch off is phase 1's 409 first; no service wired is 409 `mail_desk_not_ready` before the lock and before anything is
  written. `ResolveUnknownStart`, which runs first, only adopts or lapses an earlier lock and starts nothing. The resume
  route releases a lock and starts nothing. The timed path refuses every Mail Desk timed start before the report is read.
  A Gateway restart mid-screening loses the in-memory `_screening` entry and writes no run row; the next report screens
  again and already-decided emails are skipped. A live channel's decisions are recorded with mode `live` and nothing is
  queued (`filingQueue` comes back as the check returned it).
- **The memory.** `MailDeskMemory.Hand` hands only active channels naming the machine, with the Gateway's four fields
  from the row and the check's fields and `filingQueue` copied from the kept entry, nothing else. `Merge` keeps every
  kept entry not handed this run, untouched; takes the check's entry whole for each handed channel; and for a mailbox
  with a model failure replaces exactly `cursor`, `sentCache` and `keys` with the kept values (removing a field the kept
  entry did not have, so a first run stays a first run). A removed channel's entry is dropped 30 days after removal; a
  merged memory over 65,536 bytes is refused with both sizes in the run's reason, and the run is `failed`, so the
  check problem shows it. The claim is renewed on every assignment poll, so a backup cannot take the start mid-screening
  on a lapse, and a second report while a screening runs is `skipped-running` with no memory kept.
- **Nothing double, nothing lost.** `MailDeskDecisionStore.Record` re-reads under its lock and inserts, updates a
  `failed` row in place, or leaves any other row untouched; `ScreenAsync` skips an email holding a decision that is not
  `failed` before any model is built or asked. Cost on an updated row is the sum over attempts, so the spend counts every
  attempt's priced call. Only mailboxes with a model failure below the third attempt are written back; the third failure
  is `keep-for-owner` and lets the mailbox move on; a spend-limit failure does not write the mailbox back, as section 3
  says.
- **The spend limit.** The ledger starts from every decision row's cost and is consulted before each model question; the
  stop is written once, after the loop, and read by the one fold (`MailDeskSpend.Of`) that the settings route, the spend
  problem and the 08:00 email share.
- **No body text.** The preview is passed to the model and appears in no entity, no run reason (the reason is counts
  only) and no log line in the new code; the run row's reason is the summary sentence. The pull request's marker test
  searches the tables and the log sink through a new test-only observer on `FileLog`.
- **Tenant scope.** Every new read and write goes through `CreateContext(tenant)`, whose query filter applies to every
  tenant-scoped entity, and `MarkSpendStopped` writes the tenant on the row it adds. The database script's Postgres test
  for the two new columns exists (`MailDeskDecisionAttemptsPostgresTests.cs`); I did not run it.
- **The migration pair.** Both providers add `AttemptCount` (default 1, so a row screened before this phase counts as one
  attempt) and `RulesVersion` (default 0), and both snapshots carry them.
- **The template offer.** Offered on switch-on only, its failure reported in the answer rather than undoing the switch;
  at Gateway start off the start path, every switched-on account in its own scope, one account's failure not stopping
  the others.

## Scope

**Read:** the whole diff of the pull request against `origin/mail-desk-phase-3` for `TriggerService.cs`,
`DueCheckService.cs`, `TriggerEndpoints.cs`, `MailDeskService.cs`, `MailDeskMemory.cs`, `MailDeskDecisionStore.cs`,
`MailDeskSettings.cs`, `MailDeskProblems.cs`, `MailDeskEndpoints.cs`, `MailDeskChannelFold.cs`, `MailDeskTemplateOffer.cs`,
`AdminMailDeskEndpoint.cs`, `GatewayHost.cs`, the entity, the SQLite migration, `TenantSettingKeys.cs`, `FileLog.cs`,
`TriggerDtos.cs`, `FactoryTriggerSource.cs`, `FactoriesScreenFold.cs` and `FactoryAgentsViewEndpoints.cs`; the unchanged
`TriggerStore.ClaimForDirector`, `RecordCheck` and the claim lapse, `MailDeskStart.cs`, and the cancellation and spend
paths of `MailDeskModel.cs` and `MailDeskScreening.cs`; the names of every new test, and the bodies of the dropped-channel
test and the first hand-off tests. The phase 3 contract in full and phase 2's sections 3 and 4.

**Ran:** `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
"Category=MailDesk|FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~DueCheck"` on Linux:
886 passed, 0 failed, 0 skipped. Two throwaway tests over the pull request's rig, for findings 1 and 2, both of which
passed (proving the defect), then deleted.

**Could not reach:** the database script (the Tech Lead ran it; I did not), the Postgres migration file's body (I read
its snapshot lines in the diff summary only), the Director side of a 409 `mail_desk_not_ready` refusal, the 08:00 email
and the Open problems card with the new spend stop (folds read, not run end to end), the `trigger runs` command and
`factory_ops.py` beyond its outcome list, and the Cockpit and phone, which this pull request does not touch. I did not
read the `.Designer.cs` files.

## Re-review

Re-reviewed commit `f06f48ebe` on `mail-desk-p3-hand-off`, against the four findings above and the answers in
`docs/qa/mail-desk/phase-3/reviews/hand-off-answers.md`. The commits since this review was first pushed (`fbff5d909`) are
the merge of the phase branch, which brings only the contract's timed-start row, and the answer commit. The answer commit
touches only `TriggerService.cs`, `MailDeskService.cs`, `MailDeskChannelFold.cs`, `MailDeskSettings.cs`, three test
files, the answers file and nothing else, so nothing outside the four findings moved.

### The four findings: all four closed

1. **A paused start.** `HandToMailDesk` now records a paused start `paused`, with its count, before the
   screening-running check, and keeps no memory. My finding-1 probe, rerun with the opposite expectations, passed: the run
   is `paused`, the scripted model has no calls, there are no decision rows, no starts and no kept memory. The pull
   request's own test `ReportCheckAsync_APausedMailDeskStart_ScreensNothingAndIsRecordedPaused` holds it.
2. **A dropped channel.** `ScreenAsync` now refuses a report that leaves out any active channel naming the machine,
   before the memory check and before any email is screened. My finding-2 probe (channel B removed from both the context
   and the memory) now gets a `failed` run naming B, with no model call and no decision row.
3. **The channel row.** The fold now says "the previous screening is still running, so this check was not screened"
   when the run's reason is `TriggerService.ScreeningStillRunning`, and the paused label no longer claims there was new
   mail. A test reads the row after a skipped-running screening.
4. **The spend stop.** The stop row is removed only when the new limit, in millionths, is strictly above the sum of every
   decision row's cost, which is the contract's "raised above what was spent". The sum is read in the same tenant-scoped
   context, so it is the account's own spend. Tests cover a limit raised above the spend and limits at or under it.

### The new excuse: "created or changed after the check ran"

A missing channel is excused when its `CreatedUtc` or its `ChangedUtc` is later than the report's `CheckedAtUtc`. I
checked whether this can hide a check that really dropped a channel.

- **Only three writes move `ChangedUtc`:** adding a channel, removing it (a removed channel is never expected anyway),
  and changing its mode (`MailDeskChannelStore`, lines 103, 149 and 179). So on a Director whose clock is right, the
  excuse covers a channel added after the check ran (correct: it could not have been handed) and a channel whose mode
  changed after the check ran although it had been handed. **That second case is excused for one run only.** My third
  probe dropped B from a report, changed B's mode one minute after the check's time (the run was excused and
  `screened`), then dropped B again on the next run: that run was refused, naming B. A really dropped channel therefore
  costs at most one unnoticed run per mode change, and its mail is read on the next good run, because its kept fields
  were never touched.
- **The excuse trusts the Director's clock, and that is a gap, not a finding.** `CheckedAtUtc` is the Director's own
  time, and no Gateway code I found checks it against the Gateway's clock. My fourth probe reported a check stamped two
  days earlier than the Gateway's time: a channel created on the Gateway's day, then dropped from both context and
  memory, was excused and the run was `screened`. While a Director's clock is behind, every channel added or switched in
  mode within that gap is excused on every run, so a mailbox dropped in that window is not noticed until the gap has
  passed. It needs a machine whose clock is wrong by more than one interval, which is a misconfiguration, so I have not
  numbered it. Using the Gateway's own time the assignment was handed out (or the Gateway's receive time) instead of the
  Director's `CheckedAtUtc` would close it. The Tech Lead decides.
- **The excuse has no test of its own.** The pull request's new test covers a dropped channel that is refused; none covers
  the excused case, so a change that widened the excuse (to every change, say) would pass the suite. My third probe is
  the shape such a test could take.

### Scope of the re-review

**Read:** the full diff `fbff5d909..f06f48ebe` of the Gateway code and the test project, the names of the six new tests,
the contract change brought in by the merge, and the three writes of `ChangedUtc` in `MailDeskChannelStore.cs`.
**Ran:** the same filtered unit-test command as before: 892 passed, 0 failed, 0 skipped (886 before, plus the six new
tests). Four throwaway probes over the pull request's rig (findings 1 and 2 rerun, the excuse lasting one run, a clock
behind by two days), all passed; deleted afterwards and the tree checked clean. **Could not reach:** the database script,
which this change does not touch, and how the Director stamps `CheckedAtUtc` in practice.
