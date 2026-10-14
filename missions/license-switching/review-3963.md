# License Switching - review of pull request 3963 (the whole-phase fixes)

**Verdict: no blocking finding.** The three findings of `review-phases-2-and-4-whole.md` are built as the review
proposed: a switch that keeps failing the same way holds its machine out of the automatic decision for a growing
wait that is written on the failed row; every renewal warning due in one check goes out in one email; and the two
comments name the client-result dependency the sign-in relay stands on. Five findings follow, none blocking. Two are
about words in the documents, one is about the page, and two are limits of what this change covers that the next
phase should know.

## Scope

Read on the branch `license-switching/whole-review-fixes` at `6796130fa`, four commits over `origin/main` at
`4f844472a`:

- `MISSION.md` section 5, the three findings of `review-phases-2-and-4-whole.md`, and the changes to `contract.md`
  (new section 5.8, the constants table, section 8.1) and `cockpit-api.md` (sections 2, 3 and 4).
- The product code: `LicenseDecisionEngine.cs`, `LicenseDecisionModel.cs`, `LicenseSwitchService.cs`,
  `LicenseStore.cs`, `LicenseSnapshotSource.cs`, `LicenseDisplayFold.cs`, `LicenseRenewalEmailService.cs`,
  `LicenseEntities.cs`, the two migrations and both model snapshots, `LicenseCockpitDtos.cs`, the comment in
  `GatewayStreamClient.cs` and the comment in `GatewayHost.cs`.
- The tests: `LicenseSwitchHoldTests`, `LicenseSwitchHoldServiceTests`, the new part of
  `LicenseRenewalEmailServiceTests`, the new hosted test in `LicenseSwitchCheckHostedTests`,
  `AddLicenseSwitchHoldPostgresTests`, and the small edits to `LicenseDisplayFoldTests` and `LicenseEndpointsTests`.
- The Licenses page as it stands on this tree: `apps/cockpit/src/licenses/LicensesView.tsx`,
  `LicenseAccountCard.tsx`, `licenseParts.tsx` and `packages/client-core/src/licenses/licensesClient.ts`.
- The Director's side of a failed answer, to see whether a failed switch can come back with no reason word:
  `src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs`.

## What was run

Everything below ran in a scratch copy out of this tree, a detached worktree at a short path under the user's
temporary folder, into which `origin/main` was merged (a clean merge, no conflict, 241 files from main). No tracked
file in this tree was changed; this file is the only addition. The scratch worktree was removed afterwards.

- **The Gateway unit tests on the merged result**: 11,665 tests, 11,664 passed on the first run. The one failure,
  `SessionReadExecutorTests.DispatchAsync_GithubUrls_RepoWithoutGithubOrigin_ReturnsConflict`, was caused by my
  setup, not the code: that test uses the temporary folder as a repository path, and I had pointed the temporary
  folder inside the scratch worktree, whose origin is GitHub. Rerun with the temporary folder outside the tree it
  passed. (A first attempt with the scratch copy under the session's scratchpad folder failed 4,970 tests at once:
  the path was too long for the native SQLite library to load. That was the environment, not the change, and it
  is why the copy was re-cut at a short path.)
- **The hosted license tests on the merged result**: `CcDirector.Gateway.Tests`, filtered to the `Licenses`
  namespace, 23 tests, all passed, including the new
  `RunLicenseSwitchCheckAsync_ADirectorThatRefusesTheSameSwitchTwiceInARow_IsNotSentAThird_AndTheHostsOwnPictureSaysTheMachineIsHeld`.
- **Seven wrong changes, one at a time**, each applied to the scratch copy, built, run against the hold and renewal
  email test classes (102 tests), and reverted before the next. Every one was caught:

  | Wrong change | Tests that failed |
  |---|---|
  | Held machines are switched automatically (the hold filter removed from the engine) | 12 |
  | The hold starts after the third failure, not the second | 8 |
  | The hold ignores which account the machine is now on | 1 (`Decide_AHoldForASwitchAwayFromAnAccountTheMachineIsNoLongerOn_HoldsNothing`) |
  | A later switch row of the machine does not end the hold | 4 |
  | Only the first row of a grouped renewal email is marked | 2 |
  | One renewal email per sign-in instead of one per check | 7 |
  | Every machine held is not treated as the no-room case | 4 |

- **Not run**: `AddLicenseSwitchHoldPostgresTests`, which runs only under `scripts\test-database.ps1` against a
  throwaway PostgreSQL in Docker. It was read, not executed. The SQLite half of the migration ran under every store
  test above. The page was not opened in a browser; what it draws was read from its source.

## The three findings, as checked

### One: the hold

Checked against the brief, item by item, in the code and in the tests:

- **After two failed answers in a row with the same reason word for the same source and target.**
  `LicenseStore.HoldAfterRepeatedFailure` counts the machine's switch rows newest first and stops at the first row
  that is not the same failure (state `failed`, same reason word, same from, same to, and a reason word other than
  `director-offline`). The first failure of a run holds nothing; the second holds one minute, then two, four, eight,
  sixteen, thirty-two, then one hour from the eighth on (`FailureHold`). Rows a Switch now wrote count in the run, as
  section 5.8 says. The Director always sends a reason word with a failed answer (`LicenseSwitchCoordinator.Failed`
  takes one on every path), so a failed answer with no word, which would never be held, cannot come from this build.
- **Recorded on the failed row and survives a restart.** `HeldUntilUtc` is written on the row that earned it, from
  the row's own `FinishedAtUtc`; `ListHolds` reads it back and `LicenseSnapshotSource.Build` puts it on the machine.
  `TickAsync_AGatewayThatRestartsWhileAMachineIsHeld_KeepsTheHold` makes a new service over the same store and the
  next send waits for the stored hold.
- **A different reason, source or target, or a success, resets it.** Any such row is the machine's latest row and
  carries no hold, so `ListHolds` lists nothing for the machine, and the next failure is the first of a new run.
  Tested in the store (`AFailureThatIsNotTheSameAsTheOneBeforeIt`, `ASwitchThatWorkedInBetween_EndsTheRun`) and
  through the service (`AFailureWithAnotherReason_StartsTheCountAgain`).
- **Every eligible machine held is the no-room case, and the email goes once.** `JudgeTired` splits the eligible
  machines into free and held; when free is empty and held is not, and a target has room for the held ones,
  `NothingSwitchedEmail` returns the "keeps failing" email and `Decide` opens the exhaustion event with it, exactly
  as it opens the no-room event. While the event is open, `stillOpen` stops a second email. The service test runs
  70 checks with one machine refusing every switch: one email, one `no-room` row, `SendCount` 1. When the held
  machines have no target either, it is the plain no-room email, which is what is true.
- **Switch now ignores the hold.** `PreviewManual` and `DecideManual` use `SplitMachines`, which does not look at
  the hold; `SwitchableMachines`, which the page's "can switch now" answer uses, is the same split. Tested pure
  (`DecideManual_AHeldMachine_IsSwitchedAllTheSame`) and through the service, where the Switch now's own failure is
  one more of the run and grows the hold.
- **No path switches a held machine automatically.** The only automatic decision is `Decide`, and it builds its
  switches from the free list. A held machine has no open row (its latest row is final), so the minute check's
  resend of open rows cannot reach it either.
- **No hold lasts for ever.** A hold is a time at most one hour ahead of the row's end; `IsHeld` and `ListHolds`
  both compare it with now. A machine moved to another account by hand is not held at all (`IsHeld` requires the
  machine to still be on the account the failing switch was from), so a hold cannot outlive its own case.

### Two: the renewal email

- One check for one tenant builds `inThisEmail` from every warning due in that check - new rows and rows whose
  earlier send failed and whose wait has passed - sorted by machine label and account so the order does not depend
  on the read, and sends ONE email (`BuildEmailOfSeveral` when there is more than one). Every row of it is
  remembered as confirmed before the first row is marked, and all are marked with the same sent time.
- A holding due on a later check gets its own email, and the rows already marked are not sent again
  (`ASignInThatBecomesDueOnALaterCheck_GetsItsOwnEmail`).
- A failed send sets the wait on every row before any row is written, and the retry is one email about all of them
  (`OneEmailOfSeveralThatFails_IsTriedAgainAsOneOnItsWait_AndGoesOutOnce`). A database that refuses the marks costs
  no second email, and every row is marked when it takes writes again. A restarted Gateway sends nothing again once
  the rows are marked. The one exception the contract already names - a restart between the mail service's
  confirmation and the mark - is unchanged by this pull request.
- Tenant scoping: `TickAsync` reads one tenant's machines and rows, and the grouped email is built from that list
  alone; the existing "one account never hears of another's" tests still run and pass.

### Three: the comments

Both comments say the same true thing: the SignalR client runs client-result invocations concurrently and
fire-and-forget ones one after another; `Command` must stay a client result (`On<DirectorCommand,
DirectorCommandResult>` on the Director, `InvokeAsync` on the Gateway) or the approval would wait behind the sign-in
it answers; no fire-and-forget handler on that connection may wait. Both say it was measured out of tree and not by
a test, and point at the whole-phase review. That is the dependency the review named.

## The Licenses page: is a held machine visible?

The Developer is right that the page code was not changed, and right that the read model serves `heldUntilUtc` and
`heldText` on the machine. **Those two fields are served and never drawn**: no file under `apps/cockpit/src` or
`packages/client-core/src` names either, and the TypeScript `LicenseMachine` type does not carry them.

A held machine is nonetheless visible on the page as it stands, through three sentences the page already draws
and this change rewrites:

- The schedule table draws each machine's forecast `text` (`LicensesView.tsx` line 405), and for a held machine
  `MachineForecast` makes that text the held sentence: "Held out of the automatic switch for 1 minute, until
  2026-10-11 03:16 UTC: its switch from ... has failed more than once in a row (...). Switch now still sends it at
  once."
- The page-level next-switch sentence (`LicensesView.tsx` line 247) ends with "The automatic switch keeps failing
  and is held: it is tried again in ..." when every machine that could be switched is held.
- A failed switch row that held its machine says so at the end of its `summary`, which the log draws
  (`LicensesView.tsx` line 445), and the `no-room` row's summary says the switch keeps failing.

What is NOT visible: the machine's own `heldText` where the machine is listed, and the account card's appended
sentence, because the card draws `expectedText`, not the account's `nextSwitchText` (`LicenseAccountCard.tsx`
line 150). See finding 3 below.

## Findings

### Not blocking - the page document promises a sentence on the machine that the page does not draw

**Where.** `cockpit-api.md` section 2, the new `heldUntilUtc`, `heldText` row: "Show `heldText` on the machine as
given." `apps/cockpit/src/licenses/LicensesView.tsx`; `packages/client-core/src/licenses/licensesClient.ts` line
138.

**What.** The field is served and typed on the Gateway, but the client type does not carry it and no view draws it.
The schedule table draws the same sentence as the machine's forecast text, so the owner does see it, but on a
different part of the page from where the document says, and the account card's appended sentence is not drawn at
all.

**Why it matters.** The document is what the next page change is built from. A reader of section 2 will believe
the machine shows its hold where the machine is listed, and will not look for it in the schedule table.

**Proposed.** Either add `heldText` to the client type and draw it on the machine's row in the overview (a one-line
change under the machine's status), or change the sentence in section 2 to say where the hold is shown today: the
schedule table's machine text, the page-level next-switch sentence and the log. The first is the better one and
belongs to the phase that touches the page.

### Not blocking - contract 5.8 says the event ends "when a machine of the account is switched", the code ends it when no machine is left on the account

**Where.** `contract.md` section 5.8, "The event ends as the no-room event ends: when a machine of the account is
switched, or the account is no longer tired." `LicenseDecisionModel.cs` (`LicenseExhaustion` summary) and
`LicenseDecisionEngine.JudgeExhaustion`.

**What.** The exhaustion event ends when NO machine is on the account any more, when a fresh reading shows no tired
window, or when its end time has passed. With two machines on a tired account, one switched and one still held on
it, the event stays open - which is right, the outage continues for the held one, and no second email goes. The
contract sentence reads as if one switch ends it.

**Proposed.** One word: "when no machine is left on the account". The code and the model's summary already say so.

### Not blocking - a switch that times out every time is not held, and the documents should say so

**Where.** `LicenseSwitchService.cs`, the open-row pass that settles a row from the machine's own report after its
`NotAfterUtc` and one more send (`TryMark` with no reason word); `LicenseStore.IsAnsweredFailure`.

**What.** The hold counts only rows that ended `failed` on an answer of the Director. A Director that takes the
command and never answers makes rows that end `unknown`, then are settled `failed` from the machine's report with
NO reason word; such a row is not held and ends any run before it. That machine is still decided afresh after each
expiry - far less often than every minute (once per `SwitchNotAfter` plus one resend), so it is not the defect the
whole-phase review described, and section 5.8 is accurate in saying "an ANSWER of the machine's Director". But
nothing says it in so many words, and the owner is not emailed about it, because a target exists.

**Proposed.** One sentence in 5.8: a switch that is sent and never answered is not a failure of the run and is not
held; it is bounded by its own expiry instead. If a Director that never answers turns out to be common, that is a
new finding for phase 5, not a change here.

### Not blocking - the grouped email's `AccountEmail` is a comma-joined list

**Where.** `LicenseRenewalEmailService.BuildEmailOfSeveral`: `new LicenseOwnerEmail(string.Join(", ", accounts),
...)`.

**What.** `LicenseOwnerEmail.AccountEmail` is documented as "the Claude account the email is about"; for the email
of several it is "a@..., b@...". `LicenseOwnerMailer` never reads the field, so nothing breaks today, and the fake
mail services in the tests do not look at it either.

**Why it matters.** The first reader of the field that treats it as one address - a log line keyed on it, a per-
account send limit - will mis-parse the grouped email without any test saying so.

**Proposed.** Either a sentence on the record saying the field may name several accounts, comma-separated, for the
email of several sign-ins, or an empty string there with the accounts in the body only. Not worth a column; worth a
sentence.

### Not blocking - the PostgreSQL half of the migration is proven by a test this review did not run

**Where.** `src/CcDirector.Gateway.DatabaseTests/Data/AddLicenseSwitchHoldPostgresTests.cs`.

**What.** The test is well built: it migrates to the step before, inserts two failed rows, applies the migration,
drives the real store through `GatewayDatabase` on PostgreSQL for the write and both reads, and reverses the
migration. It runs only under `scripts\test-database.ps1` with Docker, which this review did not start. Whoever
runs the next release gate's database script sees it; until then the PostgreSQL column is proven by reading.

## The other things the brief asked for, as checked

- **No sleeps and no real clocks in the tests.** The added test lines contain no `Task.Delay`, `Thread.Sleep`,
  `DateTime.UtcNow`, `DateTime.Now` or `Stopwatch`; every time is a value and "a minute later" is an assignment.
- **No key, no real person's email address, no machine or account name in the diff.** The only addresses in the
  added lines are `account-a@example.com`, `account-b@example.com` and `account-c@example.com`; the machine names are
  `the-windows-machine`, `the-mac` and `the-old-machine`; no token-shaped string, no real host name.
- **The contract's constants table** names the two new rows as the Gateway's alone and says the Director knows
  nothing of a hold, which is true: nothing crosses the wire for it.
- **The `no-room` row's new label** "Nothing was switched" is true of both reasons the row now records, and the
  two tests that named the old label were updated, not deleted.
- **Enterprise logging.** `HoldAfterRepeatedFailure` logs entry, outcome and failure; `ListHolds` is deliberately
  quiet, like the other reads the minute check makes every minute, and says so.
- **Not reviewed.** The two migration designer files beyond their column and the model snapshots beyond their
  three lines; the fold's wording beyond the sentences the tests pin; anything under phase 3's page work.
