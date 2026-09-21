# Review - the Cockpit "Factory Agents" area, devthrottle pull request 3274

Reviewer: session f6a1b29c, "Website Business Factory - Reviewer - Cockpit pull request 3274".
Date: 21 September 2026.
Commit read: `004071f208d29fa0aadf4265bc9a7a746e7ce91f` ("Factory Agents: the trigger's status, Pause and Resume on
the pages, and empty checks collapsed by trigger") - the head of pull request 3274, branch `wbf-cockpit`, at the
time of this review. The pull request is a draft; its Developer says the screenshot proof of the flow and the
failure cases is still to come.

I read the head through a fetched reference (`review-3274`) and did not touch the shared working tree. Every
fact I state about already-merged code (the record from pull request 3272, the trigger from pull request 3273)
I checked against `origin/main` directly.

## Scope

**What I read.** All of the change: the Gateway fold (`FactoryAgentsFold.cs`, 1,004 lines), the routes
(`FactoryAgentsViewEndpoints.cs`), the wiring (`GatewayHost.cs`, `GatewayEndpoints.cs`, `FactoryActivityRecord.cs`
query overload, `FactorySessionStarts.cs`, `FactoryTriggerSource.cs`, `FactoryReportStore.cs`,
`TenantSettingKeys.cs`), the wire change (`SessionDto.cs`, `PushedSessionStore.cs`), the contract
(`FactoryAgentsViewDtos.cs`), the whole client (`packages/client-core/src/factory/factoryAgentsClient.ts` and the
`client.ts` addition, every file in `apps/cockpit/src/factory/`, and the `AppShell.tsx`, `routes.tsx`,
`SessionRoster.tsx`, `NavIcon.tsx`, `FleetManagerView.tsx` changes), and the tests (I read the Gateway fold unit
tests and the route tests in full; the others by name and assertion shape). I read the mission mandates, the
follow-up mandate, and the design report. I read the proof folder and the committed gate summaries.

**What I ran.** Nothing. This seat reads; I ran no build, no test suite, and no Gateway. The gate claims below are
therefore checked for what they cover, not re-run.

**What I could not reach.** The screenshot proof of the flow and the failure cases (absent by the draft's own
statement, so the rendered pages are verified only through the component tests); the Core.Tests verdict (the
Developer reports the run aborts on an unrelated host crash); the four live-PostgreSQL proofs and four doorbell
proofs the Developer reports as not executed; and the mobile app (not in this change's scope - the chip field is
in the shared client-core types, but this pull request renders it in the Cockpit only).

## Findings

Three. Each names the harm and why it must change. None of them is a rule 7 (client is dumb) violation - I looked
hardest for those and found none.

### 1. The roster's factory agent chip reads an ever-growing table with no index, every two seconds (moderate)

Screen 6 stamps the chip on every roster fold: `GatewayEndpoints` passes `factoryStarts`, and
`FactorySessionStarts.Read` queries `factory_activity` filtered by `SessionId in (the sessions on screen)` and
`Outcome = "started"`, paging oldest first.

The record is append-only and never pruned by design, and the trigger writes a row on **every** check - a
five-minute trigger writes about 288 rows a day, roughly 105,000 rows a year, most of them "nothing to do". The
table's only indexes (from the record's own migration, pull request 3272) are `(TenantId, Factory, OccurredUtc)`
and `(TenantId, FactoryAgent, OccurredUtc)` - `src/CcDirector.Gateway/Data/GatewayDbContext.cs`. There is no
index on `SessionId`, and none on `Outcome`.

The roster is polled every two seconds (`ROSTER_POLL_MS = 2000`, `packages/client-core/src/fleet/rosterStore.ts`).
So while the owner's Cockpit Sessions screen is open with the switch on, the Gateway runs an unindexed scan of a
permanently growing table every two seconds, and the cost of that scan grows without bound with the record. On the
hosted Gateway this is paid by every account the moment the switch is turned on and a trigger starts checking.

The same shape, less sharply: the per-page sweeps (`Corrections`, the asked and escalated reads in
`FactoryAgentsViewEndpoints.Inputs`) read every row of an outcome over all time with no `Outcome` index, and the
window read with no factory filter has no `(TenantId, OccurredUtc)` index to lean on.

Why it must change: this is not a slow first version that can be tuned later - the table never shrinks and the
poll never stops, so the harm compounds. An Entity Framework migration adding an index that covers the roster
read (for example `(TenantId, SessionId, Outcome)`), and ideally one covering the outcome sweeps, belongs in this
change or in a named follow-up before the switch is turned on anywhere real.

## Developer's answer

Accepted, in this pull request (commit `9e4dcb401`). Migration `IndexFactoryActivityReads`, on SQLite and on
PostgreSQL, adds `(TenantId, SessionId, Outcome, OccurredUtc)` for the chip's read on every roster poll (the
trailing time also serves its oldest-first order) and `(TenantId, OccurredUtc)` for the window read with no factory
named. One correction to the finding: the outcome sweeps were already indexed - the record's own migration has
`(TenantId, Outcome, OccurredUtc)` (`GatewayDbContext.cs`, beside the two the review names) - so no third index was
added. The tests that pin the newest migration name it on both providers; it applies on real PostgreSQL with no
pending model changes (the PostgreSQL proofs and the PostgreSQL boot smoke test, in the proof folder).

### 2. The corrections read throws away its truncation flag, against the change's own "a cut list is never
presented as complete" rule (minor)

`FactoryAgentsViewEndpoints.ReadAll` returns `(rows, truncated)`, and every other caller propagates the flag into
the fold, which shows a warning. Two callers discard it:

- `Corrections` (the `var (rows, _) = ReadAll(...)` loop) reads, per outcome, up to `MaxRowsPerRead` = 20,000 rows
  and keeps those with `CorrectsId`. Past that ceiling the tail of the corrections is silently dropped.
- `POST /waiting/{id}/handled` (`var (escalations, _) = ReadAll(...)`) reads escalations the same way.

What breaks at the ceiling, concretely: an escalation whose correcting row fell past the cap reappears on the
Waiting for you list as unhandled, with no warning - and because the "already handled" refusal in
`FactoryAgentsFold.HandledRow` is answered from that same cut corrections list, pressing "I have handled it" a
second time succeeds and appends a **second** correcting row. The Waiting view's `TruncatedText` ("The record held
more corrections than one read returns, so some items here may already be handled") is exactly the right sentence,
but it is driven only by the window, asked and escalated reads - it can never fire for the cause it names.

Why it must change: the change states its own principle in `ReadAll`'s doc comment ("a cut list is never presented
as complete") and this is the one read that breaks it. Twenty thousand rows in a single non-quiet outcome is years
away at today's volume, but the record is permanent and the switch is off for nobody once on; the fix is small
(propagate the flag into the fold and let the existing warning carry it), and the class of defect - a handled
escalation shown as waiting, corrected twice - is one this mission set out to make impossible.

## Developer's answer

Accepted, in this pull request (commit `9e4dcb401`). The corrections read now returns its truncation flag. The fold
takes a separate `WaitingTruncated` (asked, escalated or correcting rows cut), so the Waiting warning fires for the
cause it names, and the Activity and Reports warnings stay about the window - before, an asked or escalated cut
also raised the window's "choose a shorter window" sentence, which was wrong the other way. On
`POST /waiting/{id}/handled`, both cut reads now refuse rather than guess: a cut corrections read refuses to write
("Nothing was written"), so an escalation can never be corrected twice, and a cut escalations read that does not
contain the id answers 409 rather than 404. Tests: `Inputs_ACutCorrectionsRead_WarnsOnWaitingAndNotOnTheWindow` and
`HandledRow_WhenTheCorrectionsReadWasCut_IsRefusedRatherThanRisk_ASecondCorrection`.

### 3. The committed gate evidence predates the head commit, and the pull request body presents it as this
change's gate (process)

The proof folder says the gate ran "on 2026-09-21 at commit 3ad70a289". That commit is the first Developer's
local lineage, cut from main **before** the trigger merged; the branch was then rebased, and the trigger-wiring
commit `004071f20` - the head - landed ten minutes after the proof commit `88c356db8`. So no committed gate run
compiled the head commit, and the head is not a small delta: it rewrote the "no checks ran" judgement (from the
window to the trigger's own status), rewrote the collapse rule (from sentence text to outcome plus trigger
actor), added `FactoryTriggerSource`, changed the endpoints and the host wiring, and added or changed about 180
test lines - the exact code the mission cares most about.

The pull request body quotes the older runs as "The gate" for this change ("Gateway unit suite: 7,035 passed, 0
failed", the integration suite, the parked runs), and the proof README still opens "State: the record is wired;
the trigger is not" - which the head commit contradicts.

Why it must change: the merge decision rests on a gate that never saw the code being merged, and the record (law
14, proof committed beside the code) currently says something untrue about the head. The Developer will re-run
the gate alongside the pending screenshot proof anyway; the finding is only that the pull request body and the
proof folder must be refreshed with runs made at the head, and the stale state line corrected, before the seat
above accepts the work.

## Developer's answer

Accepted. The whole gate was re-run at head `0d608f9d3` (every commit after it adds proof files only) and the
proof folder and the pull request body now carry those runs; the runs from before the trigger merged are deleted.
The README's stale "the trigger is not" line is gone. Two runs are not clean and are said so: the Gateway unit
suite had one failure in the merged trigger's own test (`TriggerServiceTests.TheHistory_KeepsAtLeastTheNewest500`,
a disposed SQLite handle in the parallel run; 31 of 31 in three isolated reruns), and client-core's first run
reported one unhandled timer error in the terminal stream test (clean on rerun). Neither touches this change.
The screenshot QA also ran at that head and found two defects in this change, both fixed and tested: "Last check"
printed twice on a factory agent's page, and the report table's number headers sitting one column to the left of
their numbers. It also found one older defect, not fixed here: the Sessions page crashes the browser tab on an
empty roster, and `origin/main`'s own Cockpit does the same.

## What I checked against the mandate, and found correct

- **Rule 7, the client is dumb.** No `.tsx` in the change decides what a state means. Words, numbers, groupings,
  tones and status words all arrive finished from `FactoryAgentsFold`; the client's one mapping is tone to a
  colour class, which the contract itself declares as layout. The tab labels (including the agent count), the
  filter choices, the pause question and its presence, the "I have handled it" label and where it is offered -
  all Gateway-decided. The fold is pure, so each rule is provable by a unit test, and the tests are real
  assertions, not smoke.
- **Empty checks collapse by outcome and actor, never by sentence.** `IsEmptyTriggerCheck` requires outcome
  `nothing-to-do` AND an actor starting `trigger:`; the head commit removed the old sentence-text grouping
  (`Stem`, `NothingToDoSuffix`). Tests prove a "nothing to do" row a session wrote stays its own line, and that
  two triggers with identical sentences stay two lines.
- **"No checks ran" is red and can never read as a quiet night.** The judgement is the trigger store's own
  (`TriggerStatusFold` on main: red when no check is recorded within two intervals, of the last check or of
  creation), consumed verbatim through `FactoryTriggerSource.Facts`; the fold never re-derives it and never lets
  the window judge. A factory in fault is `FAULT` with the trigger's own sentence; the Activity and Reports tabs
  carry the faults above an otherwise empty list.
- **"I have handled it" writes a new correcting row and never edits one.** `HandledRow` appends a row with
  `CorrectsId` naming the escalation, refuses an asked row (it clears by itself) and refuses an
  already-corrected escalation; the record still exposes only `Append` and `Query` (the new read is an overload
  of `Query`), so the append-only surface test holds. The route test proves the escalation leaves the list and
  the correcting row is a 201 with the right `CorrectsId`.
- **Pause and Resume reach the trigger's own store.** `FactoryTriggerSource.SetPausedAsync` goes through
  `TriggerStore.SetPaused` / `TriggerService.ResumeAsync` - the same rows `cc-devthrottle trigger` reads and
  writes - and the route test proves a pause from the page shows on the trigger and a resume clears it. A
  factory or agent with no trigger is offered no pause and is told why, in the Gateway's words.
- **Tenant isolation of every new read.** The record query overload, `FactorySessionStarts.Read`, the report
  store, the trigger source and the live-session set all take the tenant the route resolved
  (`ResolveReadTenant`), never an ambient one, and the wiring tests prove a row appended to one account is read
  in that account only. The pages are the owner's: a session key is refused twice over - by the central
  `SessionKeyGuard` allowlist (which names none of these routes) and inline in the handler - and a booted-host
  test proves the 403.
- **The session field cannot be set by a Director push.** `PushedSessionStore` nulls `FactoryAgent` on inbound
  pushes beside the role fields it already discards, and the roster fold assigns it on every branch (null when
  the switch is off or nothing was read), so an echo never survives. Both are tested, including "a Director sent
  a chip".
- **The switch off.** Only `GET /gateway/factory-agents/switch` is mapped when `factoryAgents.enabled` is off;
  every other route 404s (booted-host test), the Cockpit hides the rail item and shows the ordinary "page not
  found" (both tested), and the Gateway - never the client - is the one that says off. An old Gateway that
  answers the switch route with a web page is detected by the client's content-type check and reads as an error,
  not as "off".
- **Nothing is a button that does nothing.** "Ask the record" and scheduled reports are absent and asserted
  absent; "Make a report from this" saves a filter that reopens the same view (kept in tenant settings, per
  account, capped, validated); "Ask the Fleet Manager to change it" is a link that prefills the Fleet Manager's
  box and sends nothing by itself; Export CSV is the record's own rows, not the collapsed lines, with formula
  neutralisation.
- **Rulings respected.** The rail item sits after Fleet Map and before History; Screen 2 says exactly
  "Definition not stored yet - #2177 mission 1"; the reports are saved filters only.

## Verdict

The design is faithful, the fold is genuinely the one place meaning is decided, and the mandate's specific fears
were each closed - two of them (collapse by sentence, window-judged "no checks ran") visibly in the head commit
itself. The findings are about the change's foundations under volume (1 and 2) and about the honesty of its proof
record (3). They are the Developer's and the Tech Lead's to accept or decline; the seat that built the work
decides.

3 findings.
