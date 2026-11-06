# Review: five-minute resource summaries reach the Gateway (phase 3 of the Resource Monitor mission)

Reviewer: a session of a different agent family from the author, on 6 November 2026.
Change reviewed: branch `origin/resource-monitor-gateway-summary`, commits `3c7fb1263` and `d5cc35f8c`, read as
`git diff origin/main...origin/resource-monitor-gateway-summary` in a fresh worktree cut from that branch; then the
third commit `425e68845`, pushed while this review was being written, read as `git diff d5cc35f8c..425e68845` and
covered in the addendum at the end. Line numbers in findings 1 to 12 are those of the first two commits; the third
commit shifts a few lines in `ResourceSummaryStore.cs` and `ResourceCapacityFold.cs` but changes none of the code
those findings point at.
Contract: section 6 of `missions/resource-monitor.html` on `origin/main`, plus the defect class in
`missions/resource-monitor/qa-plan.md` ("a plausible number that passes every range check").

## Verdict in one paragraph

**Nothing blocks the merge.** The contract is met end to end: windows are built purely, buffered for 24 hours,
sent on the existing tunnel behind a Hello capability, stored once per (tenant, Director, window start), rolled
up hourly with roll-before-delete, kept 90 days and two years, and folded on the Gateway with dashes for an offline
machine and "cannot read" for an unreadable figure. I found no tenant leak and no path that drops the tunnel. I
found two correctness defects that lose data in narrow conditions and should be fixed before the owner's
quality-assurance run leans on this (findings 1 and 2), several validation holes of exactly the kind the
quality-assurance plan warns about (finding 3), and one test that would stay green while the catch-up stored
the wrong content (finding 4). The third commit, reviewed in the addendum, fixes the dropped-window design I had
noted but refuses at the granularity of the whole window, which can leave spare capacity over-advertised
(finding 13). The rest are notes.

I built the worktree and ran every new test here: 14 in `CcDirector.Core.UnitTests` (builder, recorder,
outbox), 35 in `CcDirector.Gateway.UnitTests` under `Category=Resources` (store, roll-up, fold and the four
tunnel tests over a real MessagePack SignalR connection), and 88 existing statistics-database tests (hand-rolled
opener, version stamp chain, adoption, baseline equivalence, migrations). All green. The PostgreSQL test needs
`scripts/test-database.ps1` and Docker and was not run here.

The branch is one commit behind `origin/main` (Factory Inbox phase 4). A trial merge shows no conflict; the one
shared file is `GatewayHost.cs`. No statistics migration landed on main in between, so the model snapshot is not
in conflict.

---

## Findings

### 1. Should fix (high): a Director whose clock is more than ten minutes fast loses every window, permanently and silently to the user

- **File and line:** `src/CcDirector.Gateway/Stats/ResourceSummaryStore.cs:179-181` (the refusal), and
  `src/CcDirector.ControlApi/GatewayStreamClient.cs:1283-1287` (the Director drops what was refused).
- **Defect:** `RefusalFor` returns "it ends in the future by the Gateway's clock" when the window's end is more
  than `FutureTolerance` (10 minutes) ahead of the Gateway's clock, and that refusal goes into
  `RejectedWindowStartsUtc`, which the contract defines as "refused for good: sending them again cannot succeed".
  But a window from the future is the one refusal that **does** succeed on a later send: wait ten minutes and the
  same window is inside tolerance. The Director takes the ack at its word and drops the window from the outbox.
- **Failure scenario:** a machine whose clock runs 12 minutes fast (a virtual machine resumed from a snapshot,
  a laptop with time synchronisation off). Every window it closes ends 12 minutes ahead of the Gateway's clock
  and is pushed within ten seconds of closing. The Gateway refuses each one as from the future; the Director
  logs "refused for good" and drops it. Result: that machine has **no rows at all** in `resource_window`, for
  as long as the skew lasts, while its toolbar meter and banners work perfectly. The Cockpit shows it as "no
  resource summary in the last 15 minutes ... its connection to the Gateway may be down", which is not the
  cause. Both sides log a line per window, but nothing the owner looks at says why. This is the "lost windows"
  class the brief asked me to look hardest for, and it fails the mission's goal line "every machine's
  five-minute summaries appear in the Gateway's database" for that machine.
- **Fix:** do not put a from-the-future window in `RejectedWindowStartsUtc`. Leave it out of both lists (and log
  it); the Director's existing `removed == 0 -> break` already stops a batch that settles nothing, and the window
  goes again on the next reseed, succeeding once the Gateway's clock catches up. Windows older than 90 days are
  correctly "for good". Add a store test: a window ending 11 minutes ahead is neither held nor rejected; the same
  window stored again with the clock 2 minutes later is held.

### 2. Should fix: a window committed while the roll-up is reading can be left out of its hour for ever

- **File and line:** `src/CcDirector.Gateway/Stats/ResourceSummaryRollup.cs:61-74` (run start and the due
  query), `src/CcDirector.Gateway/Stats/ResourceSummaryStore.cs:72` and `:116` (ReceivedUtc stamped, then
  committed later).
- **Defect:** the "already rolled" test is `h.RolledAtUtc > w.ReceivedUtc`. `ReceivedUtc` is read from the
  clock at the top of `Store`, before the duplicate query and before `SaveChanges`. `RolledAtUtc` is read at the
  top of `RunOnce`, before the hour's windows are read. So the following order is possible: Store reads
  `now = 10:59:59.950`; the roll-up reads `runStarted = 11:00:00.000` and reads the 10:00 hour's windows; Store
  commits its window with `ReceivedUtc = 10:59:59.950`; the roll-up writes the hour with
  `RolledAtUtc = 11:00:00.000`. The window is now "received before the hour was rolled" by the stamp, but it was
  not in the rows the roll-up read. Nothing ever makes the hour due again, because only a NEW window for that
  hour would. The five-minute row survives 90 days; the two-year record of that hour is short one window, and
  if that window carried the red crossing, the hour's crossings lose it.
- **Failure scenario:** the hosted Gateway with four Directors pushing every five minutes; each `Store` is
  roughly 10 to 50 milliseconds of PostgreSQL round trips between the clock read and the commit. The race needs
  the roll-up to start inside that window. Rare per run, but it runs every hour for two years, and when it hits
  it is silent and unrecoverable.
- **Fix:** stamp the hour with `RolledAtUtc = runStarted - grace` (one minute is plenty) or make the due test
  `h.RolledAtUtc > w.ReceivedUtc + grace`. A window received in the minute before the run then rolls its hour
  again next time, which is harmless (the roll-up replaces the hour from all its windows). Alternatively read the
  clock in `Store` immediately before `SaveChanges`. A unit test can pin this with the fake clock: store with the
  clock at T, run the roll-up with the clock at T + 1 millisecond, and assert the hour is still due.

### 3. Should fix: validation holes in the very defect class the quality-assurance plan names

All in `src/CcDirector.Gateway/Stats/ResourceSummaryStore.cs`, `FiguresProblem` and `RefusalFor`
(lines 170-236). Each is a plausible value that passes every check today.

- **(a) A crossing's start is not checked to lie inside its window** (`:224-234`). A crossing with
  `StartedUtc` a day earlier or later is stored and charted at that time by `MachineResourceHistoryFold.Mark`.
  Input: the ninety-second red spike window with `Crossings[0].StartedUtc = W0.AddDays(-1)`; it is held and the
  chart marks a crossing a day before the window. Check `start <= c.StartedUtc < start + 300 s`, and that
  `StartedUtc + Seconds` does not pass the window's end.
- **(b) The sample counts, which are the roll-up's weights, are unbounded** (`:208`, `:217`, and `SampleCount`
  is never checked at all). `ProcessorSamples` is only required to be above zero. A Director reporting
  `ProcessorSamples = 1_000_000` for one quiet window would make that window the whole hour's average in
  `ResourceSummaryRollup.Combine` (`:150-152`), a believable number computed from an impossible weight. Input:
  `Steady(W0, 20, ...)` with `Machine.ProcessorSamples = 1_000_000` beside a red-spike window; the hour averages
  to 20.00. Check `0 < ProcessorSamples <= SampleCount`, the same for memory, and
  `0 < SampleCount <= WindowSeconds / 5 + slack` (a sampler cannot take more than about 60 samples in 300
  seconds; allow a small margin for timer jitter, never more than, say, 120). `SampleCount < 0` is also
  accepted today.
- **(c) A session's memory peak is not checked against the machine's total** (`:220` only runs when the row
  carries a total, and session rows never do - `:223` forbids it). A session at 400 GB on a 16 GB machine is
  stored and labelled "at least 400 / 400 GB" in the factory cost. Check each session's memory peak against
  `window.Machine.MemoryTotalBytes` when the machine read it.
- **(d) Nothing checks that a session row's samples do not exceed the window's** (`ResourceSessionSummary.SampleCount`
  unchecked).

None of these can come from the Director's builder as written; the point of the Gateway-side check, in the
plan's words, is that "every check gets an input it must reject". Each of the inputs above is accepted today.

### 4. Should fix (test): the ten-minute disconnect test would stay green if the catch-up stored the wrong content

- **File and line:** `src/CcDirector.Gateway.UnitTests/Resources/ResourceSummaryTunnelTests.cs:125-172`.
- **What it proves:** six contiguous window starts, 60 samples each, two of them held during the outage and
  absent from the store until reconnect, outbox empty at the end. That is real evidence and it does trace the
  real recorder, outbox, stream client, hub and store over MessagePack.
- **What it cannot see:** every sample is identical (`cpu: 30, 8 GB`, no sessions). So:
  - **Swapped or stale content.** If the outbox or the store filed the 10:15 window's figures under 10:10, or
    the catch-up sent the same figures for both held windows, the test passes. Input that exposes it: give each
    window a distinct figure (for example `cpu = 10 + minute / 5`) and assert each stored row's
    `ProcessorAveragePercent` matches its own window. Also assert the two windows held during the outage are
    exactly `10:10` and `10:15` (today only their count, 2, is asserted).
  - **Session rows in the catch-up.** No sample carries a session, so a catch-up that stored machine rows and
    silently dropped the session rows would pass. Input: one `ResourceSummarySessionSample` per sample; assert
    six session rows.
  - **A lost acknowledgement.** The test never has the Gateway store a batch and then lose the answer. The code
    handles it (the catch in `PushResourceSummariesAsync` keeps the windows; the store's duplicate query makes
    the resend idempotent) but nothing traces it. Input: a hub whose store commits and then throws once; assert
    the window is stored exactly once and the outbox is empty after the next reseed.
  - **The window closing on the very tick the tunnel drops.** The test stops the connection *after* the
    minute-10 push, so the 10:05 window was already up. Input: stop the connection *before* the push at
    second 600, so the window that closed at that tick is the third one held; assert three held and three
    caught up.
  - **The reseed cadence.** The test reseeds every 60 seconds; production re-pushes every 10. Not a defect, but
    the comment in `GatewayStreamClient.cs:55-56` ("within one re-push interval") is not what the test times.

### 5. Note: on a clock that moves back, the Gateway keeps the fragment and discards the full window

- **File and line:** `src/CcDirector.Core/Machine/ResourceSummaries/ResourceSummaryRecorder.cs:122-125` and
  `src/CcDirector.Gateway/Stats/ResourceSummaryStore.cs:86-107` ("the first copy wins").
- **Scenario:** at 10:07 the clock steps back to 10:03. The recorder closes the open 10:05 window with two
  minutes of samples; it is pushed within ten seconds and acknowledged. At the second 10:05 the recorder opens
  10:05 again and at 10:10 closes it with a full five minutes. The Gateway already holds 10:05 and keeps the
  24-sample fragment; the 60-sample window is acknowledged "held" and discarded. The documented rule is applied
  consistently, but it keeps the worse copy. Preferring the copy with the larger `SampleCount` costs one compare
  and loses nothing in the lost-acknowledgement case, where both copies are identical.

### 6. Note: a window arriving for an hour that has already lost rows to retention would shrink the hour

- **File and line:** `ResourceSummaryRollup.cs:90-116` (the hour is replaced from whatever windows remain),
  `ResourceSummaryStore.cs:182-183` (the 90-day refusal is by window start).
- **Scenario:** at run time `H + 90 days + 30 minutes` the windows 10:00 to 10:25 of hour H are deleted and
  10:30 to 10:55 remain. A window starting 10:35 of that hour is still *accepted* by the store (its start is
  not older than 90 days), marks the hour due, and the next run rebuilds the hour from six windows instead of
  twelve: the crossings of the first half-hour leave the two-year record. It needs a window about 90 days late,
  which only a Director with a clock 90 days wrong can send (the outbox holds 24 hours), so it is a note. The
  clean fix is to refuse by hour: `HourOf(start) < nowUtc - WindowRetention`. Then no window can arrive for an
  hour that has had any deletion.

### 7. Note, ADDRESSED by the third commit: refusing a whole window for one impossible figure dropped it entirely

- **File and line:** `ResourceSummaryStore.cs:184-191` as of the second commit.
- As first written, the unit of refusal was the window and a refused window was dropped for good, so a Director
  whose processor probe occasionally reported 100.3 per cent would have lost exactly its busiest windows. Commit
  `425e68845` now stores such a window with every figure null, the reason in `refused_reason`, and no session
  rows, and the folds show "Figures refused" rather than a gap. See the addendum for what remains of this.

### 8. Note: "no summary in the last 15 minutes" blames the connection when the Director's clock is slow

- **File and line:** `src/CcDirector.Gateway/Api/ResourceCapacityFold.cs:66-68`.
- A Director 20 minutes *behind* the Gateway sends every window on time, each stamped by its own clock; the fold
  judges silence by window end against Gateway now, so the machine shows dashes with "its connection to the
  Gateway may be down". `ReceivedUtc` is on the row and could distinguish the two: received a minute ago but
  its clock says 20 minutes ago is a clock problem, not a connection problem. Not a wrong number; a wrong reason.

### 9. Note: gigabytes are decimal (10^9) where the operating system's monitors are binary

- **File and line:** `ResourceCapacityFold.cs:153-159`.
- A machine everyone calls "16 GB" reports 17,179,869,184 bytes and will appear as "17 GB" in the Cockpit;
  "7.1 GB" spare in the mock-up would read "7.6 GB". The mission's headline agreement goal compares the meter
  with Task Manager, `free` and Activity Monitor, all of which use binary units. Not a correctness defect; a
  presentation choice worth settling once before the Cockpit phase renders it.

### 10. Note: the pieces phase 2 must wire, so the catch-up actually works in a running Director

- `ResourceSummaryRecorder.CloseIfDue` (`ResourceSummaryRecorder.cs:167`) has no production caller in this
  change. Until the sampler calls it on its tick, a window closes only when the next window's first sample
  arrives, and the last window before a Director shutdown is lost (documented as accepted).
- The recorder's `machineName` must be the same string the Hello sends (`Environment.MachineName`). The hub
  overrides any other spelling (`DirectorHub.cs:420-430`) and logs one line per window per push doing so; a
  display name passed here would be a permanent log flood, though no data would be misfiled.
- `ResourceSummaryOutbox` is in memory (`ResourceSummaryOutbox.cs:16-17`); a Director restart during an outage
  loses the buffered windows. Documented and accepted for version 1; worth one line in the quality-assurance
  report so the owner is not surprised.

### 11. Note: first roll-up only one hour after Gateway start

- **File and line:** `ResourceSummaryRollup.cs:208` (`CreateTimer(..., Period, Period)`).
- A Gateway restarted more often than hourly would never roll up or delete. Hosted deploys are far rarer than
  that, so it is a note; an initial run a minute after start would remove the dependency entirely.

### 12. Note: coding-standard points

- `ResourceSummaryStore.MachineWindows`, `SessionWindows` and `MachineHours` are public and log neither entry
  nor exit (CLAUDE.md rule 2). `Store` and `RunOnce` do.
- `recent.Max(w => w.MemoryPeakUsedBytes) ?? usedNow` (`ResourceCapacityFold.cs:95`) is a fallback that cannot
  fire (the store writes the average and the peak together or neither). Harmless, but rule 3 prefers no
  fallback; dropping the `?? usedNow` and treating a missing peak as "cannot read" is more honest.
- Log lines say "ctor" (`ResourceSummaryStore.cs:49`, `ResourceSummaryRecorder.cs:144`), an abbreviation the
  plain-English rule forbids; the codebase does this widely, so it is a nudge, not a finding.
- Try/catch sits only at boundaries (hub method, tunnel leg, timer tick, dispose). Correct.
- Everything else I looked for is in order: logging on the write paths; no clamp anywhere in the Director's
  builder or the Gateway's fold (the one `lastEnds = windowEnd` in `ResourceSummaryBuilder.cs:251` bounds a
  crossing's duration to the window it is reported in, which is the definition, not a correction); every unknown
  is a null with a reason, never a zero, and `MachineResourceHistoryFold.Sums` marks a left-out figure "at
  least" rather than adding it as zero.

---

## What I checked and found sound

- **Tenant leakage.** Every read filters `Tenant == tenant.Value` first (`ResourceSummaryStore.cs:96, 131,
  146, 164`); the tenant comes from the connection binding (`DirectorHub.cs:414`), never the payload; the
  roll-up groups by (tenant, Director, hour) and retention deletes by age only; the Postgres migration carries
  the same `^[a-z0-9-]+$` tenant check as every other `gateway_stats` table, under the computed constraint name,
  because `ResourceSummaryStatsModel.Configure` runs before the tenant guard (`GatewayStatsDbContext.cs:427-429`).
  The SQLite migration carries no constraint, consistent with the "SQLite is untouched" rule there.
- **Acknowledged but not stored.** `HeldWindowStartsUtc` is only ever filled inside the same `Store` call that
  calls `SaveChanges`; a throw anywhere before that leaves the hub's catch to answer `NotStoredReason`, and the
  Director keeps everything (`GatewayStreamClient.cs:1289-1293`). Two concurrent reseeds sending the same batch
  resolve as one insert and one "held" (or a unique-key failure answered as "not stored", which the Director
  treats correctly).
- **Duplicates on resend.** Primary key (tenant, Director, start) plus the `already` query; the same window
  twice in one batch is collapsed first (`:87-90`).
- **Roll-up arithmetic.** Which hours roll: any (tenant, Director, hour) with a window not yet covered by a
  newer hourly stamp, so the current hour re-rolls as it fills and a catch-up re-rolls its hours. Roll before
  delete: the due loop runs before the four `ExecuteDelete`s in the same `RunOnce`, and the test
  `Rollup_AnHourNeverRolledBeforeItsWindowsExpire` pins it. The 90-day boundary keeps a row exactly 90 days old
  and deletes at 90 days plus one hour; two years the same; both pinned by scheduled runs on the fake clock.
  Weights are per-window sample counts, peaks are maxima, crossings concatenate in time order.
- **SQLite versus PostgreSQL.** DateTime columns are `TEXT` in the invariant "yyyy-MM-dd HH:mm:ss.FFFFFFF" form
  on SQLite, so `<`, `>`, `==` compare correctly as text; `timestamp with time zone` on PostgreSQL, where Npgsql
  insists on `Kind == Utc`, which `ResourceSummaryStore.Utc` and the model's `UtcConverter` guarantee for every
  parameter and every stored value (`runStarted` is `.UtcDateTime`; cutoffs derive from it). `ExecuteDelete`
  is translated on both providers; the hour transaction wraps two deletes and the inserts. The `Contains` over a
  `List<DateTime>` is the form that works on .NET 10 (an array form does not).
- **The version 8 stamp with no tables.** Safe. The hand-rolled opener reads only its own tables and refuses a
  file stamped above its version; the chain stamps 8 in the migration's `Up()` and 7 in `Down()`, which
  `GatewayStatsSqliteVersionStampTests` and `TheChain_StampsTheSameSchemaVersionAsTheShippedHandRolledCode`
  check, and both passed here. An older build opening a file the new chain stamped 8 refuses it as "newer", the
  same downgrade behaviour version 7 already had. The adoption guard judges by the applied-migration list, not
  by `user_version`, so a chain file stamped 8 by the hand-rolled opener first is still adopted.
- **Hub capability advertising.** `PushResourceSummaries` is public, so the reflection list in
  `DirectorHub.cs:1015` includes it; it is removed when the hub has no store (`:735-736`); the Director sends
  only when the Hello listed it (`GatewayStreamClient.cs:1269`), and the test
  `AGatewayWithNoResourceStore_DoesNotOfferTheMethod_AndTheDirectorKeepsItsWindows` proves both sides.
- **Nothing drops the tunnel.** The hub method catches and answers every store failure; a 24-window batch with
  many sessions is far below the 32 MB `MaximumReceiveMessageSize` the hub already sets; the Director's leg is in
  its own try/catch after the hosted-desktop leg, so it cannot fail the session reseed.
- **The 24-hour buffer edge.** Dropped when the window's *end* is more than 24 hours old, or when the 289th
  window arrives, both logged and counted (`ResourceSummaryOutbox.cs:86-110`), both pinned by tests.
- **Rule 7, the dumb client.** Every string and tone the Cockpit will show is on `FleetMachineCapacityDto`,
  `ResourceCrossingMarkDto` and `FactoryResourceCostDto`; the raw percentages on `MachineResourceWindowDto` are
  for drawing, not deciding.

---

## Addendum: the third commit, `425e68845` (a window with impossible figures is stored marked refused)

Read as `git diff d5cc35f8c..425e68845`. I rebuilt and re-ran the Gateway resource tests and the statistics
version, baseline and migration tests on this head: 97 passed, none failed.

**What it does, and it does it correctly.** `RefusalFor` is split into `StructuralRefusalFor` (no five-minute
boundary, no machine name, from the future, older than 90 days: still answered as rejected) and
`FiguresRefusalFor` (an impossible figure anywhere in the window). A window that fails only the second is now
stored as a `resource_window` row with every figure null, both unreadable-reason columns and the new
`refused_reason` column carrying the reason, `crossings_json = "[]"`, and no session rows; it is acknowledged as
held, so the Director stops resending it. The hourly roll-up counts it in the new `refused_windows` column and
weights it as nothing (its sample counts are zero and its figures null, so `Combine` skips it). The capacity fold
answers a refused newest window with "cannot read" on all three labels, status "Figures refused" in the bad tone,
and the reason in `Why`; the history fold stamps `RefusedReason` on the window's row. Tests pin each of these and
`Store_ARefusedWindow_CanBeToldApartFromAWindowNeverSent` pins the point of the change. Both migrations were
regenerated with the new columns; the SQLite one still stamps `user_version` 8 and 7. This resolves note 7 and is
the better design.

**Findings 1 to 4 stand unchanged.** The from-the-future refusal is still in `StructuralRefusalFor` and still
answered "for good" (finding 1); the roll-up stamp race is untouched (finding 2); none of the validation holes in
finding 3 are closed, and they matter slightly more now, because an unchecked value is stored as a figure while a
checked one is stored as refused; the tunnel test is unchanged (finding 4).

### 13. Should fix: one impossible figure now nulls the window's GOOD figures too, and that can over-advertise spare capacity

- **File and line:** `ResourceSummaryStore.cs`, the `FiguresRefusalFor` branch in `Store` and `RefusedRow`
  (the third commit's hunks at the old lines 103-117 and 255-296).
- **Defect:** refusal is still per window. A window whose processor peak is 400 but whose memory figures are
  sound has its memory nulled as well; a window where one session's figure is impossible has the machine's
  processor and memory nulled and every other session's row discarded. Each discarded figure was a correct
  reading. The one that bites is the memory peak: `ResourceCapacityFold` computes Spare as total minus the
  highest memory peak over the last hour's windows, and a refused window contributes no peak.
- **Failure scenario:** at 10:20 the machine's memory peaks at 15 GB of 16 while the processor probe overshoots
  to 100.3 per cent for one sample. The 10:20 window is stored refused with memory null. At 10:32 the newest
  window (10:25) reads normally at 8 GB. Spare is computed from the hour's visible peaks, now 8 GB, and the
  Cockpit advertises "8.0 GB" spare for a machine that was at 1 GB free twelve minutes earlier. That is the
  plan's defect exactly: a believable number, wrong because an unknown was left out of a maximum, and the
  "Figures refused" status has already gone back to normal because only the newest window is judged.
- **Fix:** refuse at the granularity the check runs at. A machine processor problem nulls the machine processor
  figures (with the reason in `processor_unreadable_reason`) and keeps memory; a memory problem the reverse; a
  session problem drops that session's row and keeps the rest. Record the reason in `refused_reason` whenever any
  part was refused. And in the capacity fold, when any window in the last hour has `refused_reason` set and its
  memory is null, Spare must be "cannot read" with that reason, because the hour's peak is not known. Add the
  scenario above as a fold test; today `Fold_NewestWindowRefused...` only covers a refused *newest* window.

### 14. Note: the factory cost silently omits refused windows

- **File and line:** `ResourceCapacityFold.cs`, `MachineResourceHistoryFold.FactoryCost`.
- Session rows are not kept for a refused window, so a factory's 24-hour average and peak are over fewer windows
  than it ran in, with no "at least" and nothing in the DTO saying a window is missing. The machine chart shows the
  refused window with its reason; the factory table below it does not. One line - `Windows refused: n` on
  `MachineResourceHistoryDto`, or an "at least" on the factory labels when the span holds a refused window - would
  keep the two in step.

### 15. Note: the migration identifiers changed; safe only because the old ones never reached a real store

- **Files:** `20261106155906_ResourceSummaryTables` became `20261106160918_...` (SQLite) and
  `20261106155949_...` became `20261106161050_...` (PostgreSQL).
- The folder's README forbids renaming a migration because the id is persisted in `__EFMigrationsHistory` and a
  store carrying an id the chain no longer contains is refused as "not one of ours". Here the old ids existed only
  on an unmerged branch, so the rule is not broken for users. It IS broken for any `gateway-stats.db` a Gateway
  built from the first two commits touched - for example a self-host Gateway the Developer ran to look at the
  Cockpit. Before the owner's quality-assurance run, check every statistics file the Developer's test Gateways
  wrote (`SELECT MigrationId FROM __EFMigrationsHistory`) for `20261106155906_ResourceSummaryTables`, and delete
  or re-migrate any that carries it, or that Gateway will come up with statistics off and the resource rows
  "not stored". From here on, the ids must not change again.

### 16. Note: a refused window carries no crossings, so a spike in a refused window leaves the two-year record

- The red crossing of a window refused for, say, a session figure is lost from `crossings_json` on both the
  five-minute row and the hour. With per-figure refusal (finding 13) a machine crossing would survive a session
  problem; a machine processor problem would still, rightly, lose the processor crossing it may have invented.
