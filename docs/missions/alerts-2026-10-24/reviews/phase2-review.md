# Alerts mission - Phase 2 review (the Gateway)

Reviewed: pull request 4472, branch `alerts/phase2-gateway`, two commits on top of origin/main
(`97f51425a`, `8485516ed`), read as `git diff origin/main...HEAD` in the worktree
`D:\ReposFred\devthrottle-alerts-p2` on 25 October 2026. A separate review session; the author did not
review its own work.

Verdict: **one finding, with proven harm, fix before merge.** Everything else the mission asks of Phase 2
is in place and holds under its tests.

## Scope

What I read:

- The mission document, sections 3, 5.1, 5.2, 5.6 and Phase 2 of section 6, and Observation 1 of the Phase 3
  review (what the already-merged client expects of this Gateway).
- Every file in the diff: `AlertDtos.cs`; `AlertEndpoints.cs`, `AlertsService.cs`, `AlertMarkStore.cs`,
  `IAlertSource.cs`; the three sources (`RestartRequestAlertSource`, `SecretTransferAlertSource`,
  `MessageRequestAlertSource`); `AlertMarkEntity` and the `GatewayDbContext` changes; both migrations
  (SQLite `20261025095046_AddAlertMarks`, PostgreSQL `20261025095255_AddAlertMarks`) and both model
  snapshots; the two push notifier changes; the `GatewayHost` wiring; and all nine test files.
- On origin/main, the code the branch leans on: `SessionKeyGuard` (the whole allow list and `Check`),
  `GatewayEndpoints.ResolveReadTenant`, `HostedTenantBoundary` use, `FleetManagerOwnerDevice.Require`,
  `FleetMessageLinkRequestEndpoints` (`List`, `EndIfASessionEnded`, `Labelled`, `Caller`, the answer route),
  `FleetMessageLinkRequestStore.List` and `TryAnswer`, `SecretTransferStore.List`, `ExpireOverdue`,
  `TryAnswer`, `TryFinish`, `SecretTransferEndpoints.ToDto` and `Classify`, `DirectorRestartRequestStore`
  (`List`, `Stamp`, `Clock`, `Retention`), `GatewayDatabase.CreateContext(TenantId)`, `ApplyTenantScope`
  and the UTC value converters, the `[JsonConverter(typeof(JsonStringEnumConverter))]` on
  `DirectorRestartRequestState`, and the merged client's `AlertsTray.tsx` seen-marking.

What I ran:

- `dotnet build` of `CcDirector.Gateway.UnitTests` and `CcDirector.Gateway.Tests` (Debug), then the branch's
  own tests: the `UnitTests.Alerts` namespace plus the two push notifier test classes (93 passed, 0 failed)
  and `AlertRoutesTenantIsolationTests` over a real hosted-mode `GatewayHost` (12 passed, 0 failed). Builds
  wrote only to `bin/` and `obj/`; `git status` is clean afterwards.
- An out-of-tree proof for finding 1: a scratch console program in my scratchpad, referencing Npgsql 9.0.2
  (the version the Gateway pins), against a throwaway `postgres:16` container started and stopped for the
  run. It wrote one `timestamp with time zone` and read it back. Output:

      UtcNow samples with sub-microsecond ticks: 18/20
      written  UpdatedAtUtc ticks = 639285588001234567  (2026-10-25T21:00:00.1234567Z)
      read back SeenVersion ticks = 639285588001234560  (2026-10-25T21:00:00.1234560Z)
      seen >= updated : False

What I could not reach:

- `scripts\test-database.ps1` (the PostgreSQL migration test) was not run by me; I read
  `AddAlertMarksPostgresTests` and judged it by reading. Its assertions use whole-second timestamps, so it
  would not have shown finding 1 even when green.
- No browser, no phone: the client was read, not rendered. Phase 4 owns the screenshots.

## Findings

### 1. On the hosted Gateway, a restart-request alert can never read as seen (proven)

**Where.** `AlertsService.List` decides `Seen` as

    mark.SeenVersionUtc >= alert.UpdatedAtUtc

and `MarkSeen` stores `SeenVersionUtc = alert.UpdatedAtUtc` in `alert_marks`.

**Mechanism.** For a restart request, `UpdatedAtUtc` comes from `DirectorRestartRequestStore`, which is an
in-memory dictionary stamped from `DateTime.UtcNow` at full 100-nanosecond tick precision (the clock is
`() => DateTime.UtcNow`; nothing rounds it). The hosted Gateway stores the mark in PostgreSQL
(`NoSqliteOnHostedArchitectureTests` guarantees it has no SQLite), whose `timestamp with time zone` holds
microseconds. Npgsql drops the sub-microsecond ticks on the way in - the proof above shows `...4567`
written and `...4560` read back. So the stored version is up to 900 nanoseconds EARLIER than the alert's
`UpdatedAtUtc`, and `seen >= updated` is false. On this machine 18 of 20 `DateTime.UtcNow` samples had
sub-microsecond ticks, so roughly nine restart alerts in ten are affected; the hosted Gateway runs in a Linux
container, whose clock has nanosecond resolution and the same property.

The secret transfer and message request sources are NOT affected: their timestamps are read from PostgreSQL
rows, already truncated, so the stored version equals the alert's `UpdatedAtUtc` exactly.

**Concrete harm.** On the hosted Gateway every restart alert keeps its unseen dot and its "New:" text for
ever: the owner opens the tray, the client posts `seen`, the Gateway stores the mark, and the next read still
answers `seen: false`. Goal 2 of the mission (the bell and tray tell the owner what is new) is wrong for the
one alert kind the mission was started for. The merged client (`AlertsTray.tsx` line 44) also re-posts
`seen` for that alert on every mount, because the Gateway never confirms it - a write per tray open per
restart alert, for nothing.

**Why the tests did not see it.** `AlertRoutesTenantIsolationTests` runs the host on SQLite, where EF Core
stores a `DateTime` as text with all seven fractional digits, so the round trip is exact there. The
PostgreSQL test marks seen with `now.AddMinutes(-3)` on a whole-second `now`, which has no sub-microsecond
ticks to lose.

**What would fix it (for the Developer; not applied by me).** Compare at the precision the store keeps, or
store what the store can hold: truncate the version to whole microseconds (or milliseconds) in both
`MarkSeen` and the `Seen` comparison, or compare `seen >= updated.AddTicks(-9)`, or keep the version as a
`long` of ticks. Then add a test whose `UpdatedAtUtc` carries sub-microsecond ticks, run through the real
PostgreSQL in `AddAlertMarksPostgresTests`.

## Checks against the mission that hold

- **5.1, no second table of alerts.** The only new state is `alert_marks` (`tenant_id`, `alert_id`,
  `seen_version`, `cleared_at`, plus `touched_at` for the sweep). Every alert is folded at read time from the
  feature's own store; no source invents a query or writes a record of its own.
- **5.2, the contract.** `AlertDto` carries every field the table lists; `kind` is the closed set
  `restart-request` / `secret-transfer` / `message-request`; `Detail` is the feature's own DTO. The enum on the
  restart record serialises as a string on both routes because the converter sits on the type, and
  `List_TheDetailIsTheRestartRecord_InTheSameJsonTheRestartRouteServes` proves the two bodies byte-equal.
  Every field name the Phase 3 review's Observation 1 lists is present (`canAccept`, `machine`, `id`;
  `transferId`, `canAnswer`, `askedBySessionId`; `requestId`, `status`, `requesterSessionId`), each pinned by a
  source test.
- **5.2, the endpoints.** The four routes exist with the mission's shapes; the list body always carries
  `alerts` and `needsAnswerCount`; clear of a needs-answer alert is `409` with the mission's exact sentence; a
  refusal is a non-2xx with the sentence in `error`; the id arrives URL-encoded and clears; clear-all returns a
  count; a malformed id is `400` naming the shape. `/gateway/alerts/clear` and `/gateway/alerts/{id}/clear`
  cannot collide (three segments against four).
- **Route guarding.** `SessionKeyGuard` is an allow list and nothing in it matches `gateway/alerts` at any
  verb, including a raised key (`AlertRoutesSessionKeyGuardTests`, with a positive control on the comparable
  allowed read). The tenant is resolved by `ResolveReadTenant` through the hosted boundary, which denies when
  no account is bound (`EveryRoute_WithNoAccount_Is403`). This is the same depth of guard the restart
  accept and decline routes have today; see observation 1 for the one way it differs from two other owner
  routes.
- **Tenant isolation.** `AlertMarkStore` opens every context with `CreateContext(tenant)` and stamps
  `TenantId = ctx.ActiveTenant` on insert, so the global filter scopes reads and writes; the restart store is
  keyed per tenant; the two database stores open tenant contexts. Another account's id answers `404` on
  clear and is skipped on seen, proven over HTTP with two bound device keys, and marks on the SAME alert id in
  two accounts are separate rows on the composite key (service test and the PostgreSQL test).
- **Which records are alerts.** Needs-answer always; closed for 24 hours after the last change
  (`AlertWindow.Keeps`); an accepted restart is Earlier at once with the progress line as its summary and
  updates in place. Every state of every record has words, and an unknown state throws rather than render a
  sentence nobody wrote. Every finished secret transfer state writes an `Outcome` (`TryAnswer`,
  `TryFinish`, `ExpireOverdue` for both expiry and orphaning), so `Finished(dto)` cannot throw on real data;
  an allowed message request always carries an amount from the answer route, so `AmountWords` cannot throw on
  real data.
- **The migration.** Both providers gain the table with the composite primary key `(tenant_id, AlertId)`,
  the byte collation on PostgreSQL, timestamps as `timestamp with time zone`, both snapshots updated, and each
  migration follows `AddScheduleFailureNotices`, the newest on origin/main. The branch is based on the
  current tip, so the snapshot is built on the current model. `Down` drops only the new table.
- **5.6 and goal 8, nothing existing changed behaviour.** The diff touches no restart, secret or message
  request route, store or state machine. The two push notifiers gain one field, `alertId`, and their existing
  assertions on `kind`, `url`, `tag`, `title` and `body` stand unchanged. `MessageRequestAlertSource.List`
  ends a waiting request whose session has gone through the same `EndIfASessionEnded` the owner's list
  already runs on every poll, so the record and the notice to the asker are what they were.
- **Section 6, Phase 2's test list.** Unit tests per source for every state; endpoint tests for tenant
  isolation, clear refused while needs-answer, clear-all count, a malformed id; a database test for the
  migration. All present, and the ones I could run pass.

## Observations (no proven harm; for the seats named)

1. **For the Delivery Lead - who else can mark and clear.** The alert routes admit any non-session credential
   that resolves a tenant. `FleetManagerOwnerDevice.Require` (used by hand-over) and
   `FleetMessageLinkRequestEndpoints.Caller` (used by the owner's list of message requests) go one step
   further and refuse a Director's own credential and the shared machine token. The restart accept route
   does not, so the branch is consistent with the feature it folds; but a Director's credential can today
   call `POST /gateway/alerts/clear` and empty the owner's Earlier list. No code path does, so no harm is
   shown; the mission could state which rule alerts follow.
2. **For the Delivery Lead - a cleared accepted restart hides its ending.** A cleared alert is not listed
   again (5.2), and the clear is keyed on the id, not the version. If the owner clears "Restarting the
   Director on mac-mini" while it is running, the later "Restarted" or "The Director stopped without
   restarting" never reaches the tray. That is a literal reading of the document; GitHub's inbox would
   resurface it on new activity. The document, not the code, decides.
3. **For the Delivery Lead - the id form.** The mission's example is `restart:3f9c...`; the branch, the
   push payloads and the Phase 3 fixtures all use `restart-request:...`. The code is consistent with itself
   and the client never parses the id; the document should say the same.
4. **For the Developer - the read path per poll.** Each `GET /gateway/alerts` (every 5 seconds per open
   client) runs three source reads, one `All` query, one `Sweep` query, and for the message source a second
   list plus a session lookup per waiting request. Fine at today's volumes; noted so nobody is surprised by
   the query count in the logs.

## Answers (the Developer, 25 October 2026)

- **Finding 1 - accepted, fixed.** `AlertMarkStore.ToStoredPrecision` cuts a version to whole microseconds,
  the precision `timestamp with time zone` keeps. `MarkSeen` stores the version cut that way, and
  `AlertsService.List` compares both sides at that precision, so SQLite and PostgreSQL agree. Tests:
  `AlertsServiceTests.MarkSeen_AVersionWithSubMicrosecondTicks_ReadsSeen_AndAChangeOfAMicrosecondReadsUnseen`
  stores `...1234567` and reads `...1234560`, then the alert reads seen, and a change of one microsecond reads
  unseen. `AddAlertMarksPostgresTests` runs the same round trip through the real `AlertsService` on the real
  PostgreSQL: the column holds `21:00:00.123456`, the alert reads seen, and it reads unseen after a
  one-microsecond change.
- **Observation 1 - accepted (the Delivery Lead's decision), changed.** `seen`, clear one and clear all now
  take `FleetManagerOwnerDevice.Require`, the rule hand-over uses. A Director's own credential and a
  credential with no device row (the shared machine token) are refused with 403 `owner_only`, and only the
  owner's own phone or browser may write. The GET is unchanged. Tests: for each of the three routes, one in
  `AlertEndpointsTests` with a workstation device and with no device. Over a real hosted Gateway,
  `AlertRoutesTenantIsolationTests.ADirectorsOwnKey_ReadsTheList_ButIsRefusedEveryWrite` shows a Director's
  key bound to the account reads the list and is refused all three writes, and nothing gets marked or
  cleared.
- **Observation 2 - declined, per the Delivery Lead.** A cleared alert stays cleared, as section 5.2 says.
  Bringing it back on new activity is for the mission document to decide.
- **Observation 3 - declined, per the Delivery Lead.** No code change. The code, the push payloads and the
  client all use `restart-request:...`, and the Delivery Lead agreed that form.
- **Observation 4 - declined, per the Delivery Lead.** No change. The query count per poll is fine at
  today's volumes, as the reviewer says.
- **The collation proof (the Delivery Lead's earlier finding) - accepted, fixed in `7937b7a48`.**
  `("alert_marks", "AlertId")` is in the expected list of
  `Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`. The proof now differs only where main
  already does: `cron_failure_notices` is not listed. The position moves from 7 to 8 because of the one
  inserted line.
