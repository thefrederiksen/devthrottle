# Website Black - phase 1 - Review of the Gateway trial grant (branch `wb/p1-trial-grant`, commit 80a17411d)

Reviewer session, 6 November 2026. Reported to the Tech Lead session 219d4aea. Read only; nothing in any
worktree was changed, nothing was deployed, nothing touched Azure or a real database.

## Scope

**What I read.** The whole diff `origin/main...80a17411d` (merge base 1948c4a00; nine files, 588 insertions). The
Developer's `proof.md`. This repository's `CLAUDE.md`. The mission document (decisions D3, D4, D19, D20, section 5.3,
section 9) and the research document, sections 1 to 4. Around the diff, on the branch: all of `TrialRegistry.cs`,
`HostedEnrollmentEndpoint.cs` (Enroll, PersonalAccountGate, EnrollIntoTeam, LeaveOtherPlaces, the move),
`EntitlementRegistry.Evaluate`, `EntitlementScopes`, `RemoteAccessRule.Decide` and `PreLaunchGrace`,
`TenantRegistry.MintOrLookupBySubject` and `LookupBySubject`, `MobileEnrollmentEndpoint.CompleteHostedEnroll`, the
hosted enrolment wiring in `GatewayHost.cs`, `GatewayHostOptions.FromEnvironment`, `GatewayDbTestHarness`,
`HostedProTrialTests.cs`, and the deploy workflow's application-setting handling.

**What I ran.** The two new test classes in this worktree, in the foreground:

| Suite | Filter | Result |
|---|---|---|
| CcDirector.Gateway.Tests | FirstDirectorTrialTests | 11 passed, 0 failed |
| CcDirector.Gateway.UnitTests | FirstDirectorTrialSettingTests | 11 passed, 0 failed |

Both match the proof. I did not re-run the full gate; the Tech Lead ran the default and `-Gateway` runs green at
this commit and I have no reason to doubt that.

One reproduction out of tree (a scratch console project in my session's scratchpad referencing
`CcDirector.Gateway.csproj`; the worktree was not edited): a migrated SQLite database with a `BEFORE INSERT` trigger
on `account_trials` that aborts every insert, then `GrantIfFirstArrival` on an account with no row. Finding 1 rests
on it.

**What I could not reach.** The hosted Gateway, its application settings, and the website repository's
`GET /api/trial` (read only through the research document). Whether Teams is released on the hosted Gateway (an
application setting I may not read); finding 3 is conditional on it.

## Verdict in one line

Safe to deploy with the setting off: with it off the Gateway behaves exactly as today. With it on, the rule is
right in every case I could construct except one, where the comment, the proof and the code disagree about what a
failed ledger write does (finding 1). Five findings; the first two need a change before the flip, not before this
release.

## What I checked and found correct

- **Setting off is today's Gateway.** With `FirstDirectorTrial.Off` (or null) `grantFirstDirectorTrialFrom`
  stays null (`HostedEnrollmentEndpoint.cs:209-214`), the grant block at `:381-404` never runs, and the three
  decisions below it read the same `outcome` and `tier` the old code read from `decision`. The only code that
  executes differently with the setting off is one null-pattern check per Director-door call and, at start-up on a
  hosted Gateway, one read of the environment variable with one log line. No new write, no new answer, no new
  failure on enrolment. The start-up read throws only on a value that is neither 1 nor 0; the production image sets
  none, and the deploy workflow writes only `CC_GATEWAY_TEAMS` and `CC_GATEWAY_TEAM_MENTOR` (see finding 4 for the
  one caveat).
- **The setting is read in one place** (`GatewayHostOptions.FromEnvironment` -> `FirstDirectorTrial.FromEnvironment`;
  no other reader of the variable exists in the repository), only on a hosted Gateway, on for exactly `1`, and any
  other value stops the Gateway (law 1; the same shape as `PreLaunchGrace.FromEnvironment`).
- **The rule with the setting on**, traced through the code:
  - Granted only when the door is the Director door AND the setting is on (`:209-214`), after the paid read and
    before the mint, to NotEntitled or Entitled-on-free, and only when the account holds no tenant (`:383-388`).
  - A paying account: `Evaluate` answers Entitled on a paid tier, `IsFree` is false, nothing is asked. A paying
    account on a plan without hosted capacity (`pro_selfhost`) gets no trial and is then refused by the plan gate as
    before.
  - An account with a running 14-day trial: `Evaluate` answers Pro, not free, nothing is asked.
  - An account whose trial has ended: `GrantIfFirstArrival` re-reads the row and answers None; it enrols on free.
    One trial per account ever holds because the ledger row is never removed (no code in the Gateway deletes an
    `account_trials` row) and the grant checks the ledger before anything else.
  - A second Director: the row exists, Active comes back with the first end instant, nothing is re-stamped.
  - Two Directors enrolling at once in one process: the ledger's write lock serialises them and the second reads
    the winner's row; across processes the primary key and the `DbUpdateException` re-read do the same. The tenant
    mint is idempotent by subject under its own lock.
  - The Cockpit and phone door passes no setting (`MobileEnrollmentEndpoint.cs:142-144`), a move passes no ledger
    (`:815-817`), a team enrolment never reaches the gate. The negative control in the proof (door condition
    removed, one test red) is the right one.
  - A failed PAID read (Unknown) is not asked for a trial and stays a 503, as before.
  - The 7-day boundary: strictly-less-than in `ReadStatus`, pinned by `Evaluate_ProUntilTheSevenDaysEnd_FreeFromTheEndInstant`.
- **Security.** No request field reaches the grant: the subject comes from the validated token, the instant from the
  server clock, the length is a constant, the door is fixed by the route. Nothing extends a trial but the
  administrator's audited `ExtendIfLater`, which is unchanged. A caller cannot restart a trial by re-enrolling, by
  moving, or by deleting a Director. A fresh account is a fresh subject and would get its own trial; that is the
  website's sign-up to police and is inherent in "one trial per account".
- **Test standard.** Both classes use a fixed clock, the SQLite file harness (a fake, built once per process), no
  port, no sleep. Temporary files follow the harness pattern the gate already redirects into the run folder.

## Findings

### 1. A failed ledger WRITE during an enrolment is an unhandled exception, not the retry the code and the proof claim - and no test reaches it

**Where.** `src/CcDirector.Gateway/Api/HostedEnrollmentEndpoint.cs:397-402` (the `TrialOutcome.Unknown` branch and
its comment "The ledger could not be read or written ... Retry, never refuse, never grant");
`src/CcDirector.Gateway/Tenancy/TrialRegistry.cs:365-386` (the two catch clauses);
`src/CcDirector.Gateway.Tests/FirstDirectorTrialTests.cs:255-265` (`Enroll_TrialLedgerUnreadable_SettingOn_IsARetryThatMintsNothing`);
`proof.md`, the row "a ledger failure is a 503 and no tenant".

**What the code does.** Entity Framework wraps any provider exception raised while the INSERT executes in a
`DbUpdateException`. `GrantIfFirstArrival` catches that type on the assumption it is a lost grant race, re-reads the
ledger, finds no row, and re-throws (`TrialRegistry.cs:365-379`). The second clause, the one that answers Unknown,
is not reached from inside the first. The exception leaves `PersonalAccountGate` and `Enroll` unhandled, so the
Director door answers an HTTP 500 with no `Retry-After`, not the 503 retry every other temporary failure on this
route produces. Nothing is minted (the throw is before the mint), so a retry is a first arrival again; no trial is
lost and none is granted wrongly.

**Reproduced out of tree** (scratch project, migrated SQLite database, `BEFORE INSERT` trigger that aborts the
insert, account with no row):

```
read before: None
THREW Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes. See the inner exception for details.
inner: Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'simulated write failure'.
```

**Why the test does not see it.** The test drops the whole table, so `EntitlementRegistry.Evaluate` already answers
Unknown through the trial READ before the grant block runs; `notPaying` is false for Unknown, the block at `:381` is
skipped, and the 503 comes from the pre-existing Unknown path at `:406`. The grant's own Unknown branch at `:397` is
executed by no test in the repository. The branch IS reachable (a failure opening the connection or in the
`Any` query inside the lock is not wrapped and does land there), so it is not dead, only unproven.

**Harm.** With the setting on, the realistic transient failure - the database drops the INSERT - fails the
enrolment with an unexplained 500 instead of the retry sentence the Director and the proof expect, and the comment
above the branch says the opposite of what happens. The registry's own header states "This type deliberately does
not throw"; here it does. This is the ledger's behaviour since July, but this change is its first live caller since
11 October, so it becomes reachable in production at the flip.

**What must change (before the flip, not before this release, since it needs the setting on).** Either the ledger
answers Unknown for a `DbUpdateException` that is not a race (the re-read found no row), so the gate's 503 path
handles it as the comment says, or the gate catches it and answers the same 503. Then a test in
`FirstDirectorTrialTests` that makes the WRITE fail while the read succeeds - the trigger above works in the SQLite
harness - and asserts 503, no tenant, no row. The existing drop-the-table test stays; it proves the read failure.

### 2. No test proves the route is wired: every test calls `Enroll` directly

**Where.** `src/CcDirector.Gateway/GatewayHost.cs:5590-5592` (the pass of `trials: TrialRegistry,
firstDirectorTrial: Options.FirstDirectorTrial`); `HostedEnrollmentEndpoint.cs:130-132` (`Map`).

**Harm.** If that one line were missing or regressed, `Enroll` would throw the wiring-defect exception
(`:212-214`) on every Director enrolment the moment the setting is turned on, and all twenty-two tests would stay
green: none of them builds a `GatewayHost` or posts to `/devices/enroll-hosted`. The first time the wiring is
exercised with the setting on would be production, at the flip, on the one path every new customer takes.
`HostedTeamDirectorKeyTests` shows a real-host test over this route already exists in the suite.

**What must change (before the flip).** One route-level test: a `GatewayHost` with hosted options and
`FirstDirectorTrial` on, a Director's POST to `/devices/enroll-hosted` with a valid account token, then one row in
the ledger and Pro on the next entitlement read. It also proves `GatewayHostOptions` carries the setting to the
route, which nothing proves today.

### 3. The 7 days can start on an enrolment that is then refused (observation, decision for the Tech Lead)

**Where.** The grant runs at `HostedEnrollmentEndpoint.cs:388`; the tenant is minted at `:261`; the team-leave
refusal at `:292-297` and the device registration follow. The research (section 3, item 1) placed the grant "once
the workstation key is issued".

**What happens.** Where Teams is released, a Director that was set up for a team and still has sessions registered
there, set up again for the person's own account, is refused with 409 at `:294-296` - after the trial row is written
and the tenant minted. The person's 7 days are running while they close those sessions; the retry gets the same row
and the earlier start. The same holds for a database failure at the registration step. D4 says the days start when
the Director first connects.

**Why I demand no change.** The population is narrow (a team member's Director with live team sessions, on the
first personal enrolment), the loss is the time until the retry, and the Developer's placement buys the stronger
property: a trial written after the key could leave a minted tenant with no row, and that account would never be
granted one (it would read as already known). Moving the grant after the key would need the already-known answer
read before the mint and carried forward, and a loud log when a late grant fails. Worth one line in the proof so
the trade-off is on record; the Tech Lead may rule it acceptable as it stands.

### 4. One sentence in the setting's documentation is false, and it is the sentence a future operator would rely on

**Where.** `src/CcDirector.Gateway/Tenancy/FirstDirectorTrial.cs:25-26`: "The deploy writes no application setting
of its own, so the image is the one place a hosted setting is stated."

**What is true.** `.github/workflows/deploy-hosted-gateway.yml` writes two application settings when asked
(`CC_GATEWAY_TEAMS`, `CC_GATEWAY_TEAM_MENTOR`, lines 29-30, 581-582), and an Azure application setting overrides the
image's `ENV`. So `CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR` set as an application setting would turn the grant on - or,
with a mistyped value, stop the Gateway - with no Dockerfile change and no pull request.

**Harm.** None today: no such setting exists, and the proof's post-deploy check (the start-up log line reading
`=off`) is exactly the check that catches it. The sentence should say the opposite: the image is where the flip is
made, an application setting would override it, and the log line is the proof of what the process read. Keep that
log-line check as a standing step of every hosted deploy while this setting exists.

### 5. A stale "how this fails on purpose" sentence

**Where.** `src/CcDirector.Gateway.Tests/HostedProTrialTests.cs:133-134`: "put the GrantIfFirstArrival call back
into the enrolment gate and the row count and the tier assertions both go red."

**What is true.** The call is back, behind the setting; the test now pins "off by default", and putting the call
back no longer makes it red. Harm is only to the next reader, who would try the negative control and conclude the
test is broken. Reword to name the setting.

## Notes that are not findings

- `tenants.LookupBySubject` at `:387` has no catch, so with the setting on a failed tenant-mapping read there is a
  500. `MintOrLookupBySubject` at `:261` has behaved that way on every enrolment since the mint existed, so this adds
  one more read of the same class, not a new kind of failure. Mentioned so the Tech Lead knows it was looked at.
- The website shows a running trial with a 14-day wording today (research section 1). With the setting off there is
  no row to show. The flip's website work already plans the 7-day wording; the two must land together.
- The proof's claim that the move path grants nothing is true by construction (`:815-817`) but has no test; the
  route test in finding 2 could add a move case cheaply.
