# Review: the stuck-inbox alert costs one statement per poll (pull request 4607, head d592e6dbe)

Reviewed 6 November 2026 by a separate review session. Read only: nothing in the worktree was edited, built or run.

## Verdict

No finding. Every alert case the old code raised is still raised with the same cause, count, time and sentence, and
nothing is raised that was silent before. The one read is a correct UNION ALL on both SQLite and PostgreSQL, filtered to
the account on every branch. The statement count test counts the commands the real store sends and fails on the old
code. The batched liveness call is scoped to the account on both of its paths. The new project-instructions rule is
true against the provisioner on origin/main.

## Scope

What I read (from origin/main and the pull request head, never the working tree):

- The diff of origin/main...d592e6dbe: five files, 411 lines added, 39 removed.
- The whole of src/CcDirector.Gateway/Factory/Inbox/FactoryInboxStuckAlertSource.cs at the head, and the old version
  at origin/main, side by side.
- GatewayHost.cs: DevReportSessionLiveness (the old per-session rule) and the new EndedSessionsAmong, and the alert
  source wiring at line 6327.
- History/SessionHistoryStore.cs: both EndingsOf overloads, EndingsIn and Get; the session_history key in
  Data/GatewayDbContext.cs (composite primary key on tenant and session id).
- Data/GatewayDbContext.cs: the collation "C" on the Factory, State and ClaimSessionId columns of the inbox tables.
- The two new tests, FactoryInboxStuckAlertSourceCostTests and FactoryInboxStuckAlertPostgresTests, and the thirteen
  existing tests in FactoryInboxStuckAlertSourceTests.
- src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs at origin/main (constructor, Start, Reconcile, the two
  timers, ServeOn) and the hosted guard around Start and Reconcile in GatewayHost.cs.
- MISSION.md sections 5 ("Nobody picks it up") and 7 (the check).

What I ran, all out of tree on a git archive of d592e6dbe in my scratchpad:

- The sixteen stuck-alert unit tests on SQLite: 16 passed.
- A scratch test that records the text of every command the source sends on SQLite: one statement, a UNION ALL of
  three grouped reads (the text is quoted below).
- The same sixteen tests with the OLD source file put back (origin/main's file, given an adapter so the new tests
  compile): the three cost tests failed ("an idle account cost 3 statements"; the with-work counts differ), the thirteen
  behaviour tests passed. So the behaviour tests agree on old and new code, and the cost tests tell them apart.
- scripts/test-database.ps1 with Docker (engine 29.6.1 on this machine), filtered to the stuck-alert tests: the pull
  request's PostgreSQL test and a scratch test of mine, 2 passed, 0 skipped. My scratch test adds what the pull
  request's does not cover on PostgreSQL: the "Boss read it" sentence (a bundle opened by a Boss), the statement count
  (1 idle, 1 with work) and the SQL text.

What I could not reach:

- EndedSessionsAmong in GatewayHost has no test of its own in this change, and it is private to the host. Its
  equivalence with DevReportSessionLiveness is established by reading (below), not by a test.
- The full local gate and the parked run (mission section 7). I ran the tests the change reaches, not the gate.
- The hosted Gateway itself. The statement counts above are from a throwaway PostgreSQL, not the deployed service.

## The alert cases, old against new

The old code made three reads and three loops; the new code makes one read of three kinds of row and the same three
loops over the rows. For each case:

- Not received in 15 minutes (no-director or no-secretary). Old: group Sent messages with SentAtUtc on or before
  now minus 15 minutes per factory in the database, count and oldest; per factory, no-director when the registry names
  no Boss computer or no Director runs on it, else no-secretary; since = oldest + 15 minutes. New: the same grouped
  read is the first branch of the UNION ALL (Kind 0) and the loop is unchanged. Same cause, count, since, sentence.
- Secretary ended with the message unfiled (secretary-ended). Old: read every Received, unbundled, claimed message
  on or before the cut, ask the liveness per distinct claimant, keep the ended ones, group per factory in memory with
  an ordinal comparer, count the rows, since = oldest ReceivedAtUtc + 15 minutes. New: the second branch groups per
  factory AND claimant in the database (count and oldest per group), asks the liveness once for all distinct claimants,
  keeps the groups whose claimant ended, groups those per factory in memory with the same ordinal comparer, count = sum
  of the group counts, since = min of the group oldests + 15 minutes. The sum of per-claimant counts over ended
  claimants equals the old row count, and the min of per-claimant minimums equals the old minimum. The database grouping
  is byte-wise on both stores (collation "C" on PostgreSQL, the SQLite default), the same as the ordinal comparer.
- Boss did not decide in 2 hours (boss-did-not-decide). Old: read open bundles handed on or before the cut, group per
  factory in memory, count, neverRead = all rows have no ReadBySessionId, since = oldest HandedAtUtc + 2 hours. New: the
  third branch groups per factory in the database with count, oldest and Unread = count of rows with no
  ReadBySessionId; neverRead = Unread == Count. Equal to "all unread". Same sentence on both branches of neverRead,
  shown on SQLite by the existing two tests and on PostgreSQL by my scratch test.
- No Director. Unchanged: the registry and directorRunsOn are consulted exactly as before, only later and only when a
  row came back.

Nothing new is raised: every row of the one read comes from the same three predicates the old code used, with the same
cuts (receiveBy and decideBy are computed as before). The fold into one alert per factory (Fold) is untouched.

One deliberate difference that changes no alert: when the read returns no rows, the registry is no longer asked. The
old code asked it on every poll and then used nothing from it.

## The one read

The SQL the source sends on SQLite (captured from Entity Framework's own command event):

    SELECT 0 AS "Kind", "f"."Factory", '' AS "Claimant", COUNT(*) AS "Count", MIN("f"."SentAtUtc") AS "Oldest", 0 AS "Unread"
    FROM "factory_inbox_messages" AS "f"
    WHERE "f"."tenant_id" = @__ef_filter__ActiveTenant_0 AND "f"."State" = 'sent' AND "f"."SentAtUtc" <= @__receiveBy_0
    GROUP BY "f"."Factory"
    UNION ALL
    SELECT 1 AS "Kind", "f0"."Factory", "f0"."ClaimSessionId" AS "Claimant", COUNT(*) AS "Count", MIN("f0"."ReceivedAtUtc") AS "Oldest", 0 AS "Unread"
    FROM "factory_inbox_messages" AS "f0"
    WHERE "f0"."tenant_id" = @__ef_filter__ActiveTenant_0 AND "f0"."State" = 'received' AND "f0"."BundleId" IS NULL
      AND "f0"."ClaimSessionId" IS NOT NULL AND "f0"."ReceivedAtUtc" IS NOT NULL AND "f0"."ReceivedAtUtc" <= @__receiveBy_1
    GROUP BY "f0"."Factory", "f0"."ClaimSessionId"
    UNION ALL
    SELECT 2 AS "Kind", "f1"."Factory", '' AS "Claimant", COUNT(*) AS "Count", MIN("f1"."HandedAtUtc") AS "Oldest",
      COUNT(CASE WHEN "f1"."ReadBySessionId" IS NULL THEN 1 END) AS "Unread"
    FROM "factory_inbox_bundles" AS "f1"
    WHERE "f1"."tenant_id" = @__ef_filter__ActiveTenant_0 AND "f1"."ClosedAtUtc" IS NULL AND "f1"."HandedAtUtc" <= @__decideBy_2
    GROUP BY "f1"."Factory"

On PostgreSQL the same shape, with count(*)::int and count(*) FILTER (WHERE "ReadBySessionId" IS NULL)::int for the
unread count, against gateway.factory_inbox_messages and gateway.factory_inbox_bundles. One statement on both stores,
idle and with work; the account filter is on every branch; the three branches project the same six columns in the same
order and types. The anonymous composite group key on the second branch translates cleanly (GROUP BY Factory,
ClaimSessionId). MIN over a nullable ReceivedAtUtc is safe because the branch excludes null values and a group is never
empty.

## The statement count test

Real. It subscribes to Entity Framework's diagnostic listener for the CommandExecuting event and counts commands whose
connection data source is this test's own database file, so a test running beside it cannot add to the count. Nothing
is hand-built: the rows are written through FactoryInboxStore (SendAsync, Receive, HandBundle) and read through the
real source over the real SQLite file. It fails on the old code (run above): idle costs 3 statements, with work the
count differs, and the liveness is asked per Secretary rather than once.

One remark, not a finding: the idle test asserts "at most one" statement, which on its own would pass with a counter
that counts nothing. The sibling test asserts exactly one with work, so the counter is proven by the pair.

A second remark, not a finding: the PostgreSQL test is named "IsReadInOneStatement_OnPostgres" but asserts the
alerts' words and counts, not the statement count. My scratch run confirmed the count is one on PostgreSQL, so the name
is true, but the test does not itself prove it.

## The batched liveness call

EndedSessionsAmong answers "ended" by the same two rules as DevReportSessionLiveness: on the fresh roster
(PushedSessions.TryLocate with the tenant and the stale window) an ActivityState of Exited or Failed; absent from the
roster, a non-empty EndingKind on the history row. The old path read the history row with Get inside
_tenantBoundary.EnterScope(tenant), which makes CreateContext() resolve that tenant; the new path calls
EndingsOf(tenant, ids), which calls CreateContext(tenant) directly. Both reach the same tenant-scoped context, whose
global query filter restricts session_history to that tenant_id, and the table's primary key is (tenant_id, SessionId),
so the ToDictionary in EndingsIn cannot collide. The remaining liveness answers (Idle, Busy, absent with no ending) all
map to "not ended" in both the old per-session rule and the new set, which is all the alert source ever asked.

## The project-instructions rule (rule 0c)

Checked against src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs at origin/main:

- The switch is read once, in the constructor, into _enabled (lines 95 to 98). True.
- When set, Start returns before mapping anything or creating a timer (line 112), and Reconcile returns at once
  (line 149). "Maps nothing, starts no timer and never reconciles" is true.
- The log line is "[TailscaleServeProvisioner] CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve auto-provisioning disabled
  (local test mode)." The rule quotes the part before the parenthesis. True.
- Without the switch, Start maps https 443 to the Gateway's port with "tailscale serve --bg --https=443
  http://localhost:<port>", logging only the new mapping, never the previous backend. "The old value is not logged" is
  true for start-up. (The watch and reconcile ticks do log an observed wrong backend later, but by then it is the
  test port they observe.)
- FrontDoorWatchInterval is 60 seconds (line 71), and the reconcile timer is 5 minutes besides. "Re-asserts every 60
  seconds" is true.
- A hosted Gateway skips both Start and Reconcile behind "if (!Hosted)" in GatewayHost.cs. True.
- CC_DIRECTOR_ROOT is the root override read by InstanceContext in CcDirector.Core. True.

## Against the mission rules

Section 5, "Nobody picks it up": not Received within 15 minutes, or a bundle not decided within 2 hours, raises "the
Factory inbox of X is stuck" with the reason. Both thresholds (ReceiveWithin, DecideWithin), the title and the reasons
are unchanged by this pull request. Section 7 asks for the local gate and the parked run on every Gateway task; those
are the author's to run and are outside what I ran.

## Answers

- Remark 1 (the idle test asserts "at most one", which a counter that counts nothing would pass): accepted, test only.
  The idle test now asserts exactly one statement - the one read always runs - and is renamed
  `List_AccountWithNoInboxWork_CostsExactlyOneStatement_AndAsksNeitherTheRegistryNorTheLiveness`, so it proves the
  counter on its own, not only through its sibling.
- Remark 2 (the PostgreSQL test's name claims one statement but asserts only the alerts): accepted, test only, by
  making the name true rather than adding a second counter. It is renamed
  `List_EveryReasonInTwoFactories_RaisesTheSameAlerts_OnPostgres` and its summary says it proves the translation and the
  alerts; the statement count stays pinned by `FactoryInboxStuckAlertSourceCostTests`. The read is the same single
  statement on both stores, as the review's own PostgreSQL run showed.
