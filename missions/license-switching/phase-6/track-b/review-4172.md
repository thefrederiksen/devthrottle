# Review of pull request 4172 - the license alarms for a refused report and a silent machine (Track B)

Reviewed at head `f0603e75b`, base `9a5032dc2` (origin/main when the branch was cut; main was at `f08847c53`, one
unrelated commit further on, when I reviewed). Reviewer: a separate review session, reading code it did not write.

## Verdict

**Not ready to merge. One finding must be fixed; one is small and should be fixed with it; one is a decision for the
Tech Lead, not a defect of this change.**

The design is right: it is the one alarm mechanism and nothing else, the rows are the state, every open row of a kind
is passed on every raise, and nothing is held in memory that a restart would lose. The defect is in what counts as
"true now" for the silent kind, in one order of events the tests never run.

## What I read and what I ran

- Read in full: the diff of pull request 4172 (eight files), `LicenseAlarmService`, `LicenseApproverAlarm`,
  `LicenseStore` (the refusal and silence code, `ListOpenMachineHealth`, `IsRefusalOf`, `NamesOf`),
  `LicenseSwitchService.WatchSilence`, `LicenseUsageIngest.Ingest` and `Malformed`, `LicenseDisplayFold` (the
  stand-in row), `LicenseSignInFold.Label`, `LicenseReadModel.Read`, contract section 9 (all of it), the 4113 proof,
  the pull request body and the Developer's proof file.
- Ran, out of tree: a source export of the head in my scratchpad, made into a throwaway git repository (the register
  test enumerates files through git). The worktree was not built into, edited or committed to.
  - The Developer's seven tests (`LicenseMachineHealthAlarmTests`): 7 passed.
  - Two tests of my own (described below): both fail against the change, and each failure is a finding.
  - `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne`:
    red, with exactly the message the proof quotes ("COUNT DIFFERS: tools/cc-director-setup-engine/LauncherSelfUpdate.cs
    has 3 site(s) in code, the register says 2"). `LauncherSelfUpdate.cs`, `docs/BackgroundWork.md` and the Core unit
    test project are byte-identical between this head and origin/main (the diff is empty), and the last commit to
    touch them is #4162. So the proof's claim holds: that test is red on main from the launcher work, not from this
    change.
- Not reached: the live Gateway, a real Director, the Cockpit banner. No deploy is part of this change.

## Finding 1 - MUST FIX. A machine that went silent and whose Director comes back with a refused report is alarmed twice, and the silent alarm's words are false

**What happens.** `WatchSilence` closes an open `machine-silent` row only when the machine's `LastReportAtUtc` is
later than the row, that is, on an ACCEPTED report. A refused report never reaches `ApplyReport`, so it never moves
`LastReportAtUtc`. The 4113 rule "a refused machine is never also called silent" is applied only when a silence row
is about to be OPENED (the `picture.RefusalOf(machine) is not null` skip). It is never applied to a silence row that
is already open. So:

1. The Director stops. After the dead limit the minute check writes the silence row; this change raises
   `machine-silent` - one push, one email. Correct so far.
2. The Director comes back, but its report is refused (it now holds one account twice - the 16 October case - or a
   new build sends a shape the Gateway refuses). The ingest opens a `report-refused` row. The silence row stays
   open, because no report was accepted.
3. Next minute this change raises BOTH: `report-refused` opens (a second push, a second email) and `machine-silent`
   stays open. The banner says "2 license problems need you." and carries, for the same machine, "machine-a has
   sent no report since ..., so it is not switched: its Director may have stopped. Start the Director on machine-a,
   or check that machine-a is on and connected." next to "machine-a is sending reports the Gateway refuses: ...".
   The first sentence is false - the Director is sending every two minutes - and its one thing to do is wrong.
   The Licenses page itself says "Report refused" for that machine (the fold puts the refusal first), so the alarm
   contradicts the page.
4. Both stay until a report is accepted. The owner is told to start a Director that is running.

**Why it must change.** Section 9.3: what is wrong is one true sentence, and the one thing to do is one action the
owner can take. Here one of the two alarms is untrue and its action does nothing. The brief's "a refused machine is
never also alarmed as silent" holds only when the refusal comes first; the other order is exactly how a dead Director
restarted after an upgrade would present.

**Reproduced** out of tree, through the real ingest, store, minute check and alarm service, on the Developer's own
test harness:

    --- silent past the dead limit (now=2026-10-11 03:23) ---
    OPEN ALARM kind=machine-silent machine=machine-a problem="machine-a has sent no report since 2026-10-11 03:12 UTC, so it is not switched: its Director may have stopped." ...
    pushes=1 emails=1
    --- refused after the silence (now=2026-10-11 03:25) ---
    OPEN ALARM kind=machine-silent machine=machine-a problem="machine-a has sent no report since 2026-10-11 03:12 UTC, so it is not switched: its Director may have stopped." ...
    OPEN ALARM kind=report-refused machine=machine-a problem="machine-a is sending reports the Gateway refuses: it holds account-a@example.com twice (once in use and once as a spare)." ...
    OPEN ROW kind=machine-silent ...
    OPEN ROW kind=report-refused ...
    pushes=2 emails=2

The test: push a good report; move the clock one minute past `SilentAfter`; tick the minute check, then raise; push
a held-twice report twice, a minute apart, with a tick and a raise after each; assert there is no open alarm of kind
`machine-silent`. It fails on that assertion.

**What to do.** Either of these keeps "the rows are the state". The second is the cleaner one because it also puts
the Licenses log right, and it is the one I would take:

- In `LicenseMachineHealthAlarm.Conditions` (or `ConditionsNow`), leave out a `machine-silent` row whose machine has
  an open `report-refused` row in the same health list. One line; both lists are already in hand. The log row stays
  open and wrong, but the alarm is right.
- In `WatchSilence`, when a silence row is open and `picture.RefusalOf(machine)` is not null, END the silence row -
  an ended state without a `reporting-again` row, since it is not reporting again, which needs a small store method
  beside `CloseSilence`. Then the row, the page and the alarm agree, and this change needs no special case.

Whichever is chosen: add this order of events as a test (silent first, then refused), beside the 4113 test that
covers the other order.

## Finding 2 - SHOULD FIX WITH IT. The silent sentences have no length guard, and a raise that throws for one kind costs the other kind too

**What happens.** `RefusedCondition` guards its problem sentence against the 400-character limit (the fallback "the
Licenses page says why"). `SilentCondition` guards nothing, and its action names the label TWICE. The label is
`LicenseSignInFold.Label`, which is "name (user)" when two machines share a name; the ingest accepts a machine name
and a user of up to 128 characters each, so the label may be 259 characters and the action 577. The alarm service
then refuses it with `ArgumentException`; `LicenseMachineHealthAlarm.RaiseAsync` logs and rethrows,
`RaiseOneTenantAsync` logs, and for as long as that row is open no `machine-silent` alarm of that tenant opens or
closes, every minute. Because the two kinds are raised in ONE try, the same failure in the refused raise (which runs
first) would also skip the silent raise. The approver alarm leaves one over-long condition out and logs it, so that it
"costs that one alarm, never every approver alarm of the tenant"; this class does that for one kind and one sentence
only.

**How likely.** Names of this length do not come from a real operating system, so this is a bound of the contract
rather than a daily event. It is still inside what the ingest accepts, and the Developer already judged the same
bound worth guarding for the refused kind ("an account email can be 320 characters").

**Reproduced** out of tree: two machines named with 128 letters, with users of 128 letters each, one silent row;
`Conditions` for the silent kind gives `problem length=360, action length=577, limit=400`, and the real
`LicenseAlarmService.RaiseAsync` throws "An alarm's action is one plain-English sentence on one line, ending in a
full stop, at most 400 characters. (Parameter 'action')".

**What to do.** Guard both silent sentences the way the refused problem is guarded (a shorter action when the label
is long: "Start the Director on {label}." is one action and names it once), and raise the two kinds each in its own
try inside `RaiseAsync`, so a failure in one is logged and the other still runs. The approver alarm's per-condition
guard is the pattern.

## Finding 3 - A DECISION, NOT A DEFECT OF THIS CHANGE. There is no way to remove a machine, so a renamed or decommissioned machine's silent alarm never closes

The brief asks that an alarm close by itself "when the machine is removed or renamed". There is no removal in the
product: no route in `LicenseEndpoints`, no store method deletes a machine row, nothing retires one by age. A machine
that is renamed, whose operating system user changes, or that is simply taken out of service keeps its old row
forever; `WatchSilence` writes its silence row once, and with this change that row is a permanent "Machine stopped
reporting" alarm, with a red banner on every page and an action the owner cannot take. Before this change the same
row was a quiet "Not reporting since ..." line on the Licenses page. The code of this pull request is correct here -
it does exactly what section 9.7 says - but section 9.7 was written without this case, and the one thing that would
close such an alarm (a "forget this machine" operation that ends its health rows, or an age after which a silent
machine leaves the alarm) does not exist. The Tech Lead should decide which, and whether it is part of this piece or
the next; it should not be left to be discovered by the owner on a banner that cannot be dismissed.

## What I checked and found right

- **One mechanism.** The only raise is `LicenseAlarmService.RaiseAsync`; no second table, no new timer, no route. It
  is wired into the existing minute pass after the switch check (which writes the silence rows), the settling and the
  readiness check, in `RunLicenseSwitchPassAsync`.
- **Every true alarm of a kind, every run.** `ConditionsNow` reads every open row of both kinds, and each kind gets
  its own `RaiseAsync` with its own full list; an empty health list sends two empty lists, which closes everything.
  The service filters by kind, so one kind's raise never touches another kind's rows, and one tenant's raise is
  inside that tenant's context (`CreateContext(tenant)` in the store and the read model). The "nothing open leaves
  another kind alone" test and the two-account test cover this.
- **Opens once, closes by itself.** Identity is kind + machine + account; a refusal whose reason changes keeps the
  alarm and the words are brought up to date (`Reconcile` updates the two sentences on a still-open row). An
  accepted report ends the refusal row and the next minute closes the alarm; the same for a report after a silence.
  Nothing lives in memory but the per-tenant semaphore, so a Gateway restart changes nothing: the health rows and
  the alarm rows are both in the database, and the first minute after a restart finds the alarm still open and
  sends nothing.
- **Keying.** The refused alarm's machine is the machine's row, or the stand-in identifier the page lists it under,
  computed by the same `RefusedMachineId` over the same `IsRefusalOf` rule the page uses; the test compares it
  against the real fold. The silent alarm's machine is the row's machine identifier, which `OpenSilence` writes
  from the machine view. The newer of two rows about one machine speaks, so the service's duplicate check cannot
  fire.
- **Refused first, then silent.** `WatchSilence` skips a machine with an open refusal, so a refused machine never
  gets a silence row (4113's test covers it). Only the other order is broken (finding 1).
- **Wording.** Both sentences of both kinds name the machine; the one thing to do is one action; plain English; full
  stops; the refused problem falls back when the reason is too long, or when the row's message was cut at 500
  characters (`RefusalReason` returns null then). The titles have no full stop and no address. No account is on
  these alarms, as 9.7 says. The example names in the contract and the proof are `machine-a` and
  `alice@example.com`; the diff carries no real machine, person, address or account name (checked by search).
- **Tenant isolation.** Covered above; the two-account test pushes a refusal in one account and a good report in the
  other, and finds one alarm in the first and none in the second.
- **The gate claim.** Seven tests green out of tree. The one red Core test is red on files identical to main, with
  the quoted message, from #4162. The proof's statement about it is accurate.
- **Contract and the API document.** Section 9.7 matches the code as written. The API document needs no change: the
  alarm route has no list of kinds to extend, and the log kinds it lists are 4113's.

## Small notes, no change required

- `Conditions` takes the kind as a string; a caller passing another kind gets an empty list rather than an error.
  It is used by this class and its test only.
- `LicenseMachineHealthAlarm` logs through `FileLog` directly where `LicenseApproverAlarm` takes a log action; the
  tests here do not watch the log, so it costs nothing today.

## Files of my reproduction

Scratch export and the two tests, outside every worktree:
the review scratchpad, `pr4172/src/CcDirector.Gateway.UnitTests/Licenses/Review4172ReproTests.cs`
and `Review4172LengthTests.cs` in the same folder. Built with the Node typecheck switched off because the export has
no Node workspace; nothing else differs from the head.
