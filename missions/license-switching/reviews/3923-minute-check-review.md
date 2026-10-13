# Pull request 3923 review - the Gateway's once-a-minute check

Reviewed at head `f173d4ead`, against its merge base with origin/main `faffafcdc` (origin/main had not moved).
Nothing in the worktree was edited. Every reproduction and every deliberate break below was done outside the
repository.

## Verdict

**Changes needed.** One must fix, five should fix, four notes.

The send path follows contract section 3 on every path that completes normally, the tenant scoping is right, and the
proof's numbers hold. The defects are all on paths that do NOT complete normally: a check that fails part-way, a send
cut off part-way, and a Director whose clock is ahead.

## Findings

### 1. Must fix - a check that fails after planning a send strands that switch for the life of the process

`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:453` sets the in-flight mark when a send is PLANNED
(`PlanResend`). The only place it is cleared is the `finally` of the send itself (`:505`). Anything that throws between
the two leaves the mark set for good: the rest of `PlanTick` (`:337` throws on purpose; `:351`, `:363`, `:384`, `:404`,
`:409`, `:411`, `:414` are database writes that come after the first planned send), and the same in `SwitchNowAsync`
between `:247` and `:257`.

`Classify` (`:310`) then puts that row in "blocked" on every later check and never in "outcome not known", so it is
never sent again and never settled, and the store refuses the machine any new switch because it has an open row. The
machine cannot be switched again until the Gateway restarts. Nothing in the log says so after the one failed check; the
engine just lists the machine as "a switch sent to it has not finished" each minute.

Why it matters: one passing database error during one check turns switching off for that machine, silently, which is
the thing the mission exists to prevent at night. It also makes the class's own promise at `:63` ("an open row is
finished, never abandoned") untrue.

Reproduced. Steps: machine at 97%, the first send times out (row `unknown`); on the next check a second open row that
names a machine that does not exist makes `PlanTick` throw after it has planned the resend of the first; remove the bad
row; five more healthy checks with a fresh report each and a Director that would answer. Required: the first row is
asked about again. Seen: one send in total, the row is still `unknown`. A control with the same steps and no failed
check in the middle passes (two sends, row `switched`).

### 2. Should fix - a resend that is cut off part-way leaves the row saying "never sent", and the machine is then given a second switch identifier

A row in `pending` with `director-offline` is treated as known never to have left the Gateway (`:312`), so it does not
hold its machine back. But when such a row is sent AGAIN (`:381`, `:399`) nothing is written to it before the command
goes out (`:499`); the row changes only when a result comes back (`:509`). If the Gateway stops while that resend is in
flight - or `RecordResult` fails - the command may have reached the Director and the row still says it never left.
After the restart the row is in "never sent": a changed decision, or a new Switch now, ends it as "it was never sent"
and writes a NEW identifier for the same machine while the outcome of the first is not known. That is what contract
section 5.4 forbids, and the log then says a switch never left that may have run.

The Director's own guards (a different identifier is refused while one runs; the expected active account no longer
matches) should stop a double exchange, so the damage is a wrong log row and a wasted refused switch, not two switches.
The first send of a row does not have this hole: a new row is `pending` with no reason, which is read as "outcome not
known".

Reproduced. Steps: Director offline, row is `pending` / `director-offline`; next check the command goes out and the
check is cancelled while it is in flight; a new service over the same store; the best target has changed. Required:
only ever one identifier sent to that machine. Seen: two.

A fix that fits the design: before a resend of such a row, write it back to `pending` with no reason (so a cut-off
send reads as "outcome not known"), and put `director-offline` back when the send returns nothing.

### 3. Should fix - a replayed answer overwrites a newer report when the Director's clock is ahead

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:431-437` compares the answer's finish time, which is the Director's
clock, with the Gateway's receive time of the last report. The only correction is `ClockSkewSeconds`, and that is zero
for any difference under two minutes (`LicenseUsageIngest.ReadAtShift`). So a Director whose clock is ahead by less
than two minutes has every answer judged that much newer than it is. The summary at `:409-411` says a replayed answer
is refused "whenever a report has arrived since"; that is true only when the two clocks agree.

Reproduced at the store. Director's clock 90 seconds ahead; the switch ran 30 seconds before the report the Gateway
holds (which shows the machine on A) and is stamped 60 seconds after it; the answer has `Replayed = true`. Required:
not taken. Seen: taken, the machine and its holdings now say B.

It heals at the machine's next report (two minutes at most), and in the common case the read-back and the report agree,
so this is not a must fix. But it is exactly point 4 of the brief, and the existing tests
(`ApplySwitchReadBack_ReplayedAnswerWhenALaterReportExists_NeverOverwritesIt`,
`Restart_AReplayedAnswer_SettlesTheRow_AndNeverOverwritesTheMachinesLaterReport`) only cover clocks that agree. A rule
that needs no second clock: a replayed answer's read-back is never taken once the machine has reported after the row
was written - both of those times are the Gateway's.

### 4. Should fix - one Director that does not answer holds up every tenant after it, and halves how often everyone is checked

`LicenseSwitchService.cs:137-150` awaits each tenant's whole check, sends and owner email included (`:184`), before the
next tenant starts. The pull request says so itself. Two consequences it does not spell out:

- Any check that takes longer than a minute makes the host skip the next minute (`GatewayHost.cs:6484`), with no log
  line. One hung Director anywhere means every tenant is checked every two minutes, not one. Each further hung
  Director in another tenant adds another minute. The owner email call has the same effect, with a 30-second wait.
- The order is whatever the tenant list gives, so a tenant at 97% can sit behind someone else's sleeping machine.

Reproduced: with the first tenant's Director made to answer only once the second tenant's command arrives, the check
does not finish in 5 seconds; the second tenant's switch is not sent.

The brief asked that a hung Director cannot stall the check for other tenants. As built it can, by up to 60 seconds
each. With one owner in production today this does not bite, so it is for the Tech Lead to rule whether it is fixed
here or recorded as known. Each tenant's check could be started inside its scope and awaited together after the pass;
the per-tenant guard at `:161` already keeps one tenant from overlapping itself.

### 5. Should fix - three behaviours no test holds down

Each line is one change made to a copy of the source outside the repository; all 191 license tests stayed green.

- **Which Director a resend goes to** (`:463`). Changed to the Director the ROW was first written for, not the one
  that sent the machine's last accepted report: 0 red. That is contract sections 2.4 and 6.4, and point 1 of the
  brief. Every test keeps one Director per machine from start to end. Needed: a row written for one Director, the
  machine's next report from another, and the resend seen at the second.
- **The in-flight mark** (`:453`). Never set: 0 red. So nothing shows that Switch now, arriving while the minute
  check's send for the same machine is in flight, leaves that machine alone. (This is also why finding 1 went unseen.)
- **Settling from a report while the machine says that switch is still running** (`:347`). Condition removed: 0 red.

### 6. Should fix - none of the new store statements has run on PostgreSQL, and the hosted Gateway is PostgreSQL

The proof says the database script was not needed because no migration was added. The migration is not the risk; the
statements are. `OpenNoRoom`, `ExtendNoRoom`, `CloseNoRoom`, `RecordNoRoomEmail` (a conditional update with a null
test), `ApplySwitchReadBack` (a transaction around two updates and a time comparison) and `HasMachines` have only ever
run on SQLite. `src/CcDirector.Gateway.DatabaseTests` has no test that calls `LicenseStore` at all; its one mention of
these tables is the list of pinned columns. If one of them fails on PostgreSQL the check fails every minute for every
tenant in production and nothing switches. One database test that walks a switch and a no-room event through the store
would settle it before the phase 5 deploy.

Smaller, same place: `license_switch_log.FromAccountEmail` is now a lookup key (one open event per account) and is not
pinned the way the other account email columns are. Equality is the same under the default ordering, so this is
consistency only.

### 7. Note - a row can be settled `failed` from a report that was written before the switch ran

`:340` takes "the Gateway received a report after the last send" as "the report shows what the send did". A report the
Director built just before the command reached it, received just after the send, passes that test. If the tunnel then
drops and the machine stays away past `NotAfterUtc`, the row is settled from that older report: `failed`, although the
switch ran. The machine's next report corrects the active account; the log row stays wrong. Narrow, and the contract's
"settled by its next report" does not say more. Comparing against the report's own `SentAtUtc` would narrow it further.

### 8. Note - the owner email

Walked, and it holds: a restart between "event opened" and "email sent" sends exactly one email after the restart and
none on later checks (ran it: one call in four checks, the row ends `owner-emailed`). A failed send is not marked sent.
Nothing is sent on a self-hosted Gateway. The check cannot overlap itself, so two checks cannot both send.

Three things to know:

- A send the mail service keeps refusing, with no wait asked for, is tried on every check for as long as the event
  lasts, and a weekly limit can hold an event open for days (ran it: 30 calls in 30 checks). `:427` honours a wait
  only when the service names one. A growing wait of the Gateway's own would cap it.
- A send that TIMES OUT is counted as failed (`LicenseOwnerMailer.cs:100`) and tried again, though the service may
  have delivered it. That is a second way to two emails, beside the stop-between-confirm-and-mark one the pull request
  names. An identifier for the event sent with the request would close both, if the account service can honour one.
- The retry, like the whole check, runs only for a tenant with a Director connected. If every machine of the tenant is
  off, an unsent email waits until one comes back.

### 9. Note - a tenant is inert only until its first report, then writes about 14 log lines a minute for good

`HasMachines` is quiet and a tenant with no machines leaves no line (the test asserts no clock read, no send, no row;
it does not and cannot easily assert "no log line", so that part rests on reading `:159` and `LicenseStore.cs:596`).
But machines are never removed, so one report ever makes the tenant permanent, and each check then writes two lines
each from `TickAsync`, `ListOpenNoRoom`, `ReadPicture`, the read model, `ListOpenSwitches`, `ListActiveBars` and
`Decide` - about 20,000 lines a day per tenant when nothing is happening.

### 10. Note - rules

- `ReadAnswer` (`:552-560`) has a try-catch in a helper. It is the edge where a Director's text comes in and it turns
  into a logged `director-error`, so I would leave it, but it is outside the letter of rule 4.
- The store's write methods (`RecordSend`, `TryMark`, `TrySettle`, `Bar`, the four no-room methods,
  `ApplySwitchReadBack`) log their result and no failure line of their own; the failure is logged once, by `TickAsync`.
- `SwitchNowAsync(tenant, ...)` takes the tenant from its caller and the send takes it from the scope. The two are
  never compared. A route that passed one tenant inside another's scope would read one tenant's rows and look the
  Director up in the other's, find nothing, and leave rows `pending`. No cross-tenant send, but worth one assertion in
  the route when the Licenses API pull request wires it.

## What I checked and found sound

1. **The send path.** The row is written before the send (asserted by the fake Director reading the row as the
   command arrives). All four rows of the table in 3.5 are as written, including "already active" as a success and
   an answer for another identifier refused. Offline: same identifier while the decision stands, ended at
   `NotAfterUtc`, a new identifier after. Restart: resent only once the machine has reported, once more after
   `NotAfterUtc`, then settled from the report with a message that says so. The store refuses a second open row for a
   machine. The command is addressed through `SendCommandAsync`, which looks the Director up inside the tenant in scope.
2. **Concurrency.** Sends inside one tenant start together. No database context lives across a send: the store opens
   and closes one per call and the gate is released before any send. Overlap is prevented twice (host and per
   tenant). A check longer than a minute skips the next one; see finding 4 for the cost.
3. **The owner email.** Finding 8.
4. **The read-back.** Sound when the clocks agree, and when the skew is over two minutes and corrected. Finding 3 for
   the band between.
5. **Tenants.** Every store call opens its context for the tenant passed in; the in-memory marks are keyed by tenant;
   the host runs the check inside `ForEachTenantAsync` and the hosted test fails when it does not.
6. **The database.** `dotnet ef migrations has-pending-model-changes`: no changes for SQLite, and none with
   `CC_GATEWAY_EF_PROVIDER=postgres` against the PostgreSQL migrations project. Kind `no-room` fits: kind 7 of 16
   characters, states `open` and `ended` within 16, the longest new reason code (`owner-email-failed`) 18 of 32,
   messages capped at 500, the empty machine identifier breaks nothing because the table has no foreign key, and a
   null switch identifier is already what the spare rows carry under the unique index.
7. **Inert.** Finding 9.
8. **Plain English, no fallbacks.** Nothing to raise beyond finding 10.

## The proof's claims

| Claim | What I found |
|---|---|
| License unit tests: 191 passed | 191 passed, 0 failed, 0 skipped |
| Hosted tests: 2 passed | 2 passed (run with the brief's wider filter, which matches the same 2) |
| "A row with no known outcome is sent again without waiting for a report": 4 red | 5 red now; the fifth is the test the proof says was added afterwards |
| "An event already emailed is emailed again": 4 red | 4 red, the same four |
| "The store no longer refuses a second open switch": 1 red | 1 red (my edit also hit the no-room guard, which turned its own one test red, matching that line of the table) |
| `CronRunResultTests.cs` is the file on origin/main | True: no difference from origin/main |
| The gate: 22,796 of 22,800 | Not run by me. A gate run belongs to one seat, and the brief did not ask for it |
| The three mission route tests are flaky | Not checked |

## How the reproductions were run

A scratch test project outside the repository, linked to this worktree's Gateway and to its own test helpers, with two
control tests on the pull request's happy path that pass. Four findings fail there (1, 2, 3, 4); two walks pass (the
restart between event and email; the every-check retry count). The deliberate breaks were made in a copy of the
committed source, also outside the repository, where the untouched copy first gave the same 191. The scratch folder is
this session's and will not last; the steps above are enough to write each test again.
