# Review - pull request 3925, the Gateway routes the Licenses page reads

Reviewed at `871fd3d39`, against its merge base with `origin/main`, `faffafcdc`. 17 files, 4,367 lines added.
Reviewed by a separate review session that wrote none of it. Nothing in the worktree was edited except this file.

## Verdict: changes needed

One must fix (finding 1), eight should fix, nine notes. The routes themselves are sound: the account comes from
the credential and nowhere else, one account cannot read another's, the guard knows the three reads, the shapes
document matches the code name for name, and every test the proof names passes here. What needs changing is the
memory bound of the usage history, and a handful of places where the page would still have to judge something
or would be told something untrue.

## What I ran and read

- Read in full: `LicenseEndpoints.cs`, `LicenseDisplayFold.cs`, `LicenseSnapshotSource.cs`, `LicenseUsageHistory.cs`,
  `LicenseCockpitDtos.cs`, the changes to `GatewayHost.cs`, `LicenseStore.cs`, `LicenseUsageIngest.cs` and
  `SessionKeyGuard.cs`, and the code they stand on (`LicenseDecisionEngine.cs`, `LicenseForecaster.cs`,
  `LicenseReadModel.cs`, `GatewayEndpoints.ResolveReadTenant`). Read `cockpit-api.md`, the proof file, MISSION.md
  sections 3, 5 and 7, and contract sections 2.2, 5.2 and 5.7.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~License"`: passed 252, failed 0.
- `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"`: passed 8, failed 0.
- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~LicenseMessagesCarryNoKey"`: passed 12.
- A script compared every key of the five JSON examples in `cockpit-api.md` with the properties of the response
  types, in order: all 40 objects match.
- An out-of-tree console project (in the session's scratch folder, referencing the Gateway project) reproduced
  findings 1, 2, 5, 6, 7 and 8. Its output is quoted under each.
- NOT done: I did not re-run the eighteen mutations of the proof. I checked by reading that the named test would
  catch each one, and that is all. I did not run the whole gate.

## What holds (the six things the brief asked to check hardest)

1. **Isolation and authorisation.** All three handlers take the account from
   `GatewayEndpoints.ResolveReadTenant(ctx, _tenantBoundary)` (`GatewayHost.cs`, the `LicenseEndpoints.Map` call),
   the same resolver the other Cockpit reads use; no route reads an account from the path, the query or a body, and
   no account bound is a 403. The store opens a context for that account only, and the history is keyed by account.
   `SessionKeyGuard.cs:370-372` lists the three reads under GET; `LicenseGuardTests` proves each by verb, and
   `LicenseHostTests` proves it over real HTTP through the real middleware, with a second account that sees nothing
   of the first.
2. **The fold is in one place.** Every label, tone and sentence is in `LicenseDisplayFold`, and what is at its
   limit, what has room and what Switch now accepts are the engine's own answers. The gaps are findings 2, 3, 4, 7.
3. **The shapes** cover goals 2 and 3 and most of goal 1; the gap is finding 4. `cockpit-api.md` matches the code.
4. **No sign-in key** is in any response type; an empty account answers 200 with a sentence on all three routes; a
   log row of an unknown kind is listed as "Note" with its message; a page is 1 to 200 rows.
5. **The history** is safe under concurrent ingest and read by reading: every access is inside one lock and `Read`
   hands out copies of immutable points. It never mixes accounts of two DevThrottle accounts. It is NOT bounded
   (finding 1), and no test exercises it from two threads (finding 10).
6. **Logging, no fallbacks, try-catch.** The handlers log entry, exit and failure and rethrow; the only try-catch
   blocks are at the route handlers and the store read. Findings 11 and 12 are the small exceptions.

## Findings

### 1. The usage history is not bounded per DevThrottle account - must fix

`src/CcDirector.Gateway/Licenses/LicenseUsageHistory.cs:50-51, 57, 110-120`.

Two holes, and together they make the growth permanent:

- **No ceiling on how many accounts one DevThrottle account holds.** `MaxPointsPerWindow` (line 28) caps one
  window of one account. Every new email in a report creates a new series (lines 59-60), and a report may carry 64
  holdings (`LicenseUsageIngest.MaxHoldings`).
- **A point stamped in the future is never dropped.** A point's time is the Director's own `ReadAtUtc` plus the
  shift (line 57). `Trim` drops only points older than two hours before now (line 112). Nothing checks that
  `ReadAtUtc` is not ahead of the Gateway's clock - `LicenseUsageIngest.Malformed` does not look at it, and the
  skew correction is taken from `SentAtUtc`, not `ReadAtUtc`.

Reproduced out of tree: 50 reports, each naming 64 new accounts with readings stamped ten years ahead, then a read
three days later: `accounts held after 50 reports, read three days later: 3200`. Nothing was dropped.

Why it matters: the class comment says the cap is "a ceiling on what a misbehaving Director can make the Gateway
hold", and the brief asked for exactly this bound. The hosted Gateway is one process for every account, on a plan
chosen for its memory. One Director with a bug or a bad clock - no malice needed for the future stamp - grows
memory that only a restart gives back.

What would close it: cap the number of account series per DevThrottle account (the oldest goes), and never keep a
point later than now (clamp it or drop it). Add a test for each.

Related and smaller: an account that stops reporting is only trimmed when it reports or its forecast is read
(`Trim` runs for the one account being written or read), so its last two hours stay until the Gateway restarts.
Bounded and small; a note.

### 2. A stale account's window still says "at-limit" - should fix

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:205-228` against `:124-130`.

The fold's own header says of a stale account "neither 'at limit' nor 'has room' may be said". `Window()` does not
know the account is stale, so it says it anyway. Out of tree, an active reading of 99% that is ten minutes old:
`status=stale isStale=True fiveHour.level=at-limit`.

Why it matters: the page gets `status: stale` and `fiveHour.level: at-limit` in the same account and has to decide
which wins - that is the page deciding what a state means. A page that colours by `level` shows a red bar on
numbers the Gateway itself says cannot be trusted. Give a stale account's windows their own level (or pass the
staleness into `Window`), so the level alone is enough.

### 3. The forecast's windows carry numbers and times with no words - should fix

`src/CcDirector.Gateway.Contracts/LicenseCockpitDtos.cs:410-426`, `LicenseDisplayFold.cs:409-436`.

`LicenseWindowForecastDto` has `utilization`, `resetsAtUtc` and `percentPerHour` but no `utilizationText`, no
`level` and no `resetsText`. Rule 4 of `cockpit-api.md` (lines 23-25) says that beside every time that matters
there is a sentence saying how far away it is; this one has none. A schedule that shows when a window resets, or
colours a window that is at its limit, has to compare `resetsAtUtc` with now or `utilization` with 97, or join each
forecast row to the overview by email. Either carry the overview's `LicenseWindowDto` words on the forecast window,
or say in the document that the page takes them from the overview and must never work them out.

### 4. Goal 1's "renewal date" is not on the account - should fix

`LicenseCockpitDtos.cs:126-194` (`LicenseAccountDto`); MISSION.md lines 30-31.

Goal 1 lists, per account: plan, both windows, when each resets, **renewal date**, machines. The account carries
no date of that kind, and `cockpit-api.md` never uses the word. The nearest fact is the sign-in expiry, which is on
each machine's holding (`machines[].active`, `machines[].spares[]`). To show one per account the page must collect
that account's holdings across machines and pick the soonest - a comparison of times, on the page. An account's
sign-in expiry only reaches the account row when it is already the status (`sign-in-needed`,
`sign-in-expires-soon`).

Either put it on the account (the soonest expiry across its holdings, the machine it is on, the verdict and the
sentence, folded like the rest), or have the Tech Lead rule that "renewal date" means the per-machine expiry shown
in the machines table, and write that in `cockpit-api.md` so phase 3 does not invent it.

### 5. The forecast route answers 500 when a window is rising very slowly - should fix

`src/CcDirector.Gateway/Licenses/LicenseForecaster.cs:212`, reached by `LicenseEndpoints.cs:84`.

`readTime.AddHours((97 - utilization) / perHour)` throws when the answer is past the end of the calendar. Out of
tree, one account at 10% whose two readings 1.9 hours apart differ by 0.000001:
`THROWS ArgumentOutOfRangeException: Value to add was out of range.` The forecaster was on main before this pull
request, but this pull request is what puts it behind a route, and one such window fails the whole forecast for
that DevThrottle account until the two hours of readings age out. It needs a rate below roughly a millionth of a
percent an hour, so it depends on how finely the provider reports use; a Director with a defect can produce it
at will. A window that would take longer than it has until its reset is simply "not expected to reach 97%" - the
line should say that instead of computing a date.

### 6. A machine with two operating system users is named twice - should fix

`LicenseDisplayFold.cs:96-97, 144, 197, 343`; the names come from `LicenseReadModel.cs:173-174`.

A machine is a machine name AND an operating system user everywhere else in this design. `activeOnMachines`,
`spareOnMachines`, the status sentence, the Switch now sentence and `nextSwitch.machines` carry the name alone. Out
of tree, two users on one machine: `activeOnMachines=[m1, m1]`, `"In use on m1 and m1."`,
`"Moves m1 and m1 to account-b@example.com."` The page cannot tell the two apart or link either to its row in
`machines`. `LicenseMachineForecastDto` has the same weakness from the other side: it has no `machineId`, so the
schedule's rows can only be joined to the overview's by name and user. Name a machine the same way in every list
(the user in the words when a name repeats, and `machineId` beside the name).

### 7. A switch row in a state this build does not know says "is switching" - should fix

`LicenseDisplayFold.cs:531-538`.

The fold is careful with an unknown KIND. For a `switch` row in an unknown STATE the summary falls to the default
arm. Out of tree, state `cancelled`: `stateText=cancelled isOpen=False summary="m1 is switching from
account-a@example.com to account-b@example.com."` The row is closed and the sentence says it is under way. Keep
"is switching" for `pending` and `unknown` only, and give any other state its message, as an unknown kind gets.

### 8. The same reading sent again IS added again when the machine's clock is skewed - should fix

`LicenseUsageHistory.cs:57, 102`.

A repeat is recognised by an identical time, and the time is `ReadAtUtc` plus the shift. For a machine whose clock
is more than two minutes off, the shift is receive time minus sent time, which differs by milliseconds from one
report to the next. So a holding whose read failed (old numbers, old read time) is added afresh by every report.
Out of tree, one reading sent five times with a jittering shift: `points kept for ONE reading sent five times: 5;
rate=0`. The forecast then says "Not rising" where the truth is "No rate yet", and the class comment (lines 11-13)
and the proof's mutation 13 claim a guarantee that only holds at a shift of zero. Recognise the repeat by the
machine's own `ReadAtUtc`, before the shift.

### 9. One page poll writes dozens of log lines - should fix

`LicenseDisplayFold.cs:172, 183, 323`; the lines are `LicenseDecisionEngine.cs:136, 178, 448`.

The overview asks `DecideManual` once per account and once more per other account; the forecast asks again per
account in use. Each call logs two lines. Five accounts is about fifty lines per overview read, and the document
tells the page to read every 30 to 60 seconds, per open Cockpit and phone. The engine's header already explains why
its small predicates do not log ("a line per call would bury the decisions in the log"); the fold now does that to
the Switch now lines the minute check and a real Switch now write. The fold needs a way to ask without logging.

### 10. Nothing tests the history from two threads - note

`LicenseUsageHistoryTests.cs`. The brief asked that the history be safe under concurrent ingest and read. By
reading it is: one lock, copies out. No test would fail if the lock were removed from `Read`. One test with a few
writers and a reader would hold it down.

### 11. Two sentences are written for cases the code says cannot happen - note

`LicenseDisplayFold.cs:365` (`move?.RefusalMessage ?? "No other account has room ..."`) and `:461`
(`move.RefusalMessage ?? "No next account is known."`). Each is a fallback that states a cause the code did not
establish. If the case is impossible it should throw (CLAUDE.md rule 3); if it is possible - finding 6's repeated
names could reach line 461 - the sentence is a guess shown to the owner as fact.

### 12. The fold's small public methods do not log, and do not say why - note

`Account`, `Window`, `Machine`, `Holding`, `LogEntry`, `ReasonText`, `ReadErrorText` and `Span` are public and log
nothing. That is the right call for per-row helpers, and the engine's header states the same exemption for its own;
the fold's header should state it too, or the methods should not be public.

### 13. A walk through the log can, rarely, show one row twice - note

`LicenseEndpoints.cs:117, 129`, `LicenseStore.cs` `ListLogPage`, `cockpit-api.md:498-499`.

The walk is held to rows created at or before the moment it began, and a row's time is the Gateway's clock when
the report was RECEIVED, not when the row was saved. A report received just before the first page is read and saved
just after puts a row inside the walk's range after page one was taken; every later row moves down one, and page
two repeats the last row of page one. Nothing is lost and nothing leaks. The document's "never appears in it and
never moves a row from one page to another" is a little more than the code gives. The cursor is also not checked
against now, so a cursor with a later time simply reads later rows; harmless.

### 14. `TheGuard_KnowsEveryRouteTheGatewayMaps` cannot fail the way its comment says - note

`LicenseGuardTests.cs:36-42`. It checks the three constants it lists by hand. A fourth route mapped in
`LicenseEndpoints.Map` under a new constant fails nothing here. The host test does catch a mapped route the guard
forgets, but only for the routes in its own hand-written list as well. Fine as it is; the comment overstates it.

### 15. `verifiedByUsageRead` has no words - note

`LicenseCockpitDtos.cs:539-540`. It is `true`, `false` or `null` with nothing beside it, and in the document's own
example a switch that was refused before it began carries `false`. A page that shows "not verified" on that row is
deciding what the value means. A sentence beside it (or folding it into `timingText`) would keep the page dumb.

### 16. The rows of one switch share nothing but their time - note

Goal 3 asks for "which machines" per switch. One switch of an account is one row per machine, and the rows carry no
common identifier, so a page that wants to show them as one event groups by time, from and to - its own rule. If
phase 3 lists one row per machine, nothing is needed; if it groups, the Gateway should say which rows belong
together.

### 17. Any session key can read every account email, machine and user - note, for the Tech Lead to confirm

`SessionKeyGuard.cs:365-372`. The comment gives the reason (an agent can say which account a machine is on). It is
a decision made in this pull request rather than in MISSION.md, so I name it: every agent session of the account
can now read all of its Claude account emails, machine names, operating system users and the switch log.

### 18. What "never a sign-in key" is and is not proven by - note

`LicenseMessagesCarryNoKeyTests` checks the NAMES of the members of the response types. Two free-text values pass
through from the Director unchanged: the read error detail for an error word this build does not know
(`LicenseDisplayFold.cs:593`) and a log row's `message`. The contract obliges the Director never to put anything
from a sign-in in them, and the Gateway caps their length; no test on the Gateway side can show their content is
clean. The proof's wording ("holds every member name of these types to that") is accurate; this is only so nobody
reads it as more.

## The proof file's claims

| Claim | Checked | Result |
|---|---|---|
| 252 license unit tests pass | Ran | True at `871fd3d39` |
| 8 host tests pass | Ran | True |
| The no-key scan includes the new types and passes | Ran; read how it picks types (every `License*` type in the contracts namespace) | True |
| Test counts per file (86, 25, 18, 9, 8) | Counted facts and theory rows | 9 and 8 exact; the others consistent with the 252 total |
| Every example in `cockpit-api.md` is a real answer | Compared all keys with the types, in order | Names and order match; I did not regenerate the values |
| Eighteen mutations, all caught | Read only, not re-run | Each named test does assert the mutated behaviour; mutation 13 holds only at a shift of zero (finding 8) |
| The gate: red only on `GateTestsDoNotDependOnTheMachineTests`, red on main | Not re-run; known and outside this change per the brief | Not checked |
| "The history ... two hours of readings per account in memory" | Out of tree | Not true as a bound (finding 1) |
