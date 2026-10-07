# Review - round 2 track B: act on what waits, and archive a factory (pull request 3608)

Reviewed 2026-10-07 by a separate review session (read only; no edits, no sub-agents).

## Verdict

**Approve. No blocking defect found.** Two low-severity items and three observations below; none of
them marks an item the bulk clear did not count, lets a non-owner through, disables a schedule the
archive does not name, deletes history, or lets the Cockpit choose a word. The Lead may take the two
low items as follow-ups or ignore them.

## Scope

- **What was read:** `git diff origin/main...HEAD` at merge-base `b7802982f` (one commit, `2115b081b`),
  all 31 files: `FactoryOwnerActions`, `FactoryOwnerActionEndpoints`, the `FactoriesScreenFold` and
  `FactoryAgentsFold` changes, `FactoryActivityRecord` (the many-row Append), `FactoryRegistryStore`
  (Archive, Restore, Register keeping the archived mark), `CronJobStore.SetEnabled`, the migration
  `20261007035034_ArchiveFactories` plus its Designer and the snapshot, the DTOs, `GatewayHost` wiring,
  the Cockpit (`FactoriesView`, `FactoryView`, `FactoryParts`, `factoriesScreenClient`), and every test
  file in the diff.
- **What was read around it, on this branch:** `FleetManagerOwnerDevice.Require`,
  `SessionOriginSurfaces.FromDeviceType`, `DeviceRegistry.DefaultDeviceType`, `SessionKeyGuard` (an
  allow list), `FactoryAgentsGate.IsOnFor`, `FactoryAgentsViewEndpoints.Inputs` / `ReadAll` /
  `Corrections`, `FactoriesScreenEndpoints.Inputs`, `GatewayDatabase.CreateContext(tenant)` and the
  tenant query filter, `CronEngine.EvaluateDueAsync` and its `MarkFired` write-back, the existing
  `FactoryWaitingView` Cockpit component and `ConfirmDialog`.
- **What was run:** the five touched or new Gateway unit test classes
  (`FactoryOwnerActionsTests`, `FactoryOwnerActionEndpointTests`, `FactoryRegistryStoreTests`,
  `CronJobStoreTests`, `FactoryActivityRecordTests`, `FactoryAgentsFoldTests`): 155 passed, 0 failed,
  in this worktree. The Cockpit and client tests were read, not run. The full Gateway unit suite, the
  local gate and `-Parked` were not run here; the pull request body states the default gate and the
  filtered Gateway run were green for the author.
- **Live evidence (read only):** `GET /gateway/factory/registry` on the hosted Gateway with this
  session's key. Ten factories; no schedule id is named by seats of two different factories; Tallyhand
  (3 seats) names **no schedule**, so its archive switches nothing off.
- **Not reviewed:** track A's status reasons (a separate pull request), the data step after deploy,
  the mobile app (no change there).

## The questions the brief asked, answered

**Can the bulk clear mark an item it did not count, one newer than 7 days, or one in another factory
or account?** No.
- The route reads with `FactoriesScreenEndpoints.Inputs(..., f.Factory)`, which queries Asked and
  Escalated rows with the factory filter and no time window, then filters again by factory id
  (`FactoryOwnerActionEndpoints.cs:52-54`). The page uses the same `Inputs` with the same factory
  (`FactoriesScreenEndpoints.cs:52`), so the set the confirm counted is the set the action sees.
- The cut-off comes back from the client, but it is refused unless it is at least 7 days before the
  Gateway's own now (`FactoryOwnerActions.cs:100-101`), so nothing newer than 7 days can be marked
  whatever the body says. A cut-off further in the past can only mark a subset, and the count must
  still match.
- The count the confirm showed must equal the count now, or it is a 409 with nothing written
  (`FactoryOwnerActions.cs:105-108`), proven over HTTP by
  `HandledOlder_WhenTheCountChanged_Is409_AndWritesNothing`.
- Tenant: every store call uses `CreateContext(tenant)`, which sets the query filter; the registry
  `Find` is tenant-scoped; `ResolveReadTenant` binds the device's account.
- A cut candidates or corrections read is refused before anything is written
  (`a.WaitingTruncated` covers both reads, `FactoryAgentsViewEndpoints.cs` Inputs: `t1 || t2 || t3`).

**Can the count in the confirm differ from what is marked?** Only by a 409. Both are
`OlderThan(open, cutoff).Count` on the same read shape; the cut-off round-trips as an ISO UTC string
with full tick precision (`DateTime` Kind Utc), so the comparison is exact. The rows are written in
one `SaveChanges` (`FactoryActivityRecord.cs` Append many), so the owner row's number is the number
of handled rows beside it, or nothing is written.

**Can the owner-only refusals be bypassed?** No, for the three credentials named.
- Session key: refused first in `FleetManagerOwnerDevice.Require` (`CallingSession` set), and
  independently never reaches the route because `SessionKeyGuard` is an allow list and does not
  list `/gateway/factories/*` for POST (test `ASessionsOwnKey_NeverReachesTheRoute`). A raised
  session key passes only three named lists, none of which is this.
- Director device key: a Director enrols as device type `workstation`
  (`DeviceRegistry.DefaultDeviceType`), which `SessionOriginSurfaces.FromDeviceType` maps to
  `Unknown`, so `Require` refuses it. The endpoint test uses exactly `workstation`, so it proves the
  real case.
- Machine token: no device identity in `ctx.Items`, refused by the same branch.
- The check runs before the switch, the tenant and the registry are read, so nothing is touched on a
  refusal (`Handle`, `FactoryOwnerActionEndpoints.cs:136-152`).

**Can archive disable a schedule it does not own, or one already off? Can restore re-enable one the
owner switched off himself?**
- Archive only switches schedules named by the factory's own seats that are currently on
  (`ArchivePlan` -> `Plan(..., wantEnabled: false)`); those already off are named as left off and not
  touched; ids the account does not hold are named as missing. `CronJobStore.SetEnabled` is
  tenant-scoped, so another account's schedule id reads as missing. Proven over HTTP with a fake
  store asserting the exact switch list (`cj_off` untouched, `cj_else` untouched).
- Restore switches on only the ids recorded in `ArchivedSchedules` that are still off
  (`RestorePlan`); a schedule off before the archive is never touched (proven: `cj_off`, later
  switched on by someone else, is not in the restore list). See low item 1 for the one sequence
  where a schedule the owner switched off himself would be switched on.
- No schedule id is shared between two live factories today (registry read above), so archiving one
  factory cannot silence another's seat.

**Is an archived factory still counted anywhere?** Not on the Factories list, its status order, or
the duplicate-CEO check (`FactoriesScreenFold.List`, `listed`/`archived` split; proven by three
tests). See observation 1 for the old account-wide waiting screen and the registry route.

**Is the migration safe?** Yes. Three nullable columns added to `factory_registry`, SQLite `TEXT`
like the previous migration, no existing row changed, `Down` drops them, Designer carries the
`[Migration]` attribute and the snapshot matches. `Register` copies the archived mark forward when a
factory is registered again (proven), so a scheduled re-registration cannot un-archive.

**Is history ever deleted?** No. There is no delete in the diff. Archive and restore write new rows;
the registry entry, seats and goal are kept (proven: 2 seats after archive). Restore nulls the
registry's three archived fields, but the archive row in the activity record still names the
schedules it switched off.

**Does the client decide words (rule 7)?** No word, count or sentence is chosen in the Cockpit:
labels, busy labels, confirm title, lines, confirm label, result text and refusal text are all the
Gateway's and are rendered verbatim (proven by the four Cockpit tests). See low item 3 for the one
non-word decision.

**Would the tests stay green if a rule were reverted?**
- Owner-only: the endpoint tests drive the real `Require`; removing it turns the nine 403 cases into
  200/201. Red.
- Count mismatch: real route, real record. Red.
- Archive switching only named, on schedules; restore switching only archived, still-off ones: the
  fake store records every switch and the tests assert the exact list. Red.
- List leaving an archived factory out; re-register keeping it archived; archive twice refused;
  restore of a live factory refused. Red.
- Bulk clear not marking the fresh item: the endpoint test keeps a 2-hour-old item and asserts it is
  the only one left. Red.
- One gap, see low item 2.

## Low-severity items

1. **Restore can switch on a schedule the owner switched off himself, in one sequence.** Archive
   switches `cj_a` off and records it; someone switches `cj_a` on; the owner then switches it off
   again by hand; Restore finds `cj_a` in `ArchivedSchedules` and off, so it switches it on
   (`FactoryOwnerActions.cs` RestorePlan). Harm: a schedule the owner deliberately stopped runs again
   after Restore. Mitigation already in place: the confirm names it ("This schedule the archive
   switched off is switched back on: ...") before anything happens, so the owner sees it; but the only
   way to keep it off is to decline the whole Restore. Low: needs three hand steps on an archived
   factory. A follow-up could record the schedule's `UpdatedUtc` at archive time and skip one changed
   since.

2. **No test proves the bulk clear leaves another factory's old items alone.** The rule holds by two
   filters (the query's factory filter in `Inputs`, pre-existing, and the `.Where(SameId)` in the
   route), and I verified both by reading. But the endpoint tests register one factory only, so a
   test cannot be pointed at that goes red if both filters were dropped. Harm if it ever regressed:
   Machine Care's bulk clear would mark Website Business's stale items. Suggest one endpoint test
   with a second registered factory holding an old item and an assertion that it is still open after
   the bulk clear.

3. **The Cockpit decides the confirm's danger tone:** `danger={action.action !== "restore"}` in
   `OwnerActionButton` (`FactoryParts.tsx`). Every word is the Gateway's; only the red-versus-neutral
   styling of the confirm button is a client branch on the action kind. Rule 7 keeps colour on the
   Gateway, so a `Danger` boolean on `FactoryOwnerActionDto` would close it. No harm today.

## Observations (not defects against the brief)

1. **Other readers of the record still see an archived factory's items.** The account-wide waiting
   screen (`/factories/waiting`, `FactoryAgentsFold.Waiting`) is record-based and will still list an
   archived factory's open items and count them in its summary; `GET /gateway/factory/registry`
   (`factory list`) returns archived factories in the same list, told apart only by the new
   `archivedAtUtc` field; the archived factory's page still offers Talk. The brief asked for the
   Factories list and its status order only, which is done. Worth a line in the report so the owner
   is not surprised by Tallyhand on the waiting screen.

2. **The activity row is written last on both archive and restore.** If `SetEnabled` or the append
   throws mid-way, the registry is archived (or restored) and the schedules switched, but no owner
   row exists and the owner sees a 500. The comment in the route explains the ordering (so Restore can
   finish a half-done archive), and the registry's `ArchivedBy`/`ArchivedAtUtc` still say who and
   when. Same database, so a failure between the two writes is unlikely; noted, not a defect.

3. **Pre-existing race, not introduced here:** `CronEngine` writes `enabled: true` back on a
   recurring job after a fire (`CronEngine.cs:224`), using the copy it read before the fire. An
   archive that switches that schedule off during the seconds its `StartAsync` takes is overwritten;
   the archive's row and registry say switched off, the schedule stays on, and Restore later says
   "Already on again". The existing pause route has the same window. Worth its own issue; nothing in
   this pull request widens it.

4. Track A edits `FactoriesScreenFold` too; whichever merges second must rebase and re-run the fold
   tests.

## Round 2

Reviewed 2026-10-07 at `76b20d728` (the head of pull request 3608 after its rebase onto `0719e1472`,
where track A, pull request 3607, merged).

### Verdict

**Approve. Nothing new found; the rebase kept both tracks' rules.** One small test gap, below, which
does not block.

### Scope

- `git diff origin/main...HEAD` at merge-base `0719e1472`, all 31 files, read in full for the files that
  changed since the first review and compared hunk by hunk for the rest.
- Run here on the rebased head: the Gateway unit test classes of both tracks (`FactoryOwnerAction*`,
  `FactoriesScreen*` including track A's `FactoriesScreenRound2Tests` and `FactoriesScreenFoldTests`,
  `FactoryRegistry*`, `CronJobStoreTests`, `FactoryActivityRecordTests`, `FactoryAgentsFoldTests`,
  `FactoryAgentsSwitchTests`): 256 passed, 0 failed. Cockpit `src/factory`: 63 passed. client-core
  `src/factory`: 17 passed. Not run: the full Gateway suite, the local gate, `-Parked`.

### The conflict resolution kept both tracks' rules

- **`HandledRow` routes through track A's `CorrectingRow`.** `CorrectingRow` gained an optional `why`;
  `HandledRow` passes `"escalation"` or `"question"` as the kind and the bulk clear's reason as `why`.
  The failures card's "Handled" (`FactoriesScreenFold.cs:578`) still calls `CorrectingRow(..., "failure")`
  with no reason, so its row text is unchanged. The "already handled" refusal, the cut-corrections
  refusal and the 500-character clip are now in one place for all three kinds.
- **Status and FAILING clearing are track A's, untouched.** The diff against the new origin/main has no
  hunk in `Status`, `OpenFailures` or `FailuresCard`; the page carries `Failures = FailuresCard(...)`
  beside the round 2 `Waiting` card with its bulk clear (`FactoriesScreenFold.cs:180-189`), and the
  Cockpit mounts `FailuresCard` above the waiting panel (`FactoryView.tsx:164`).
- **The head's title is track A's, untouched.** `CeoText = ceo is null ? NoHead : HeadText(ceo)` and
  `NoHead = "No head named"` are as merged.
- **The list's and the page's duplicate-head check both leave archived factories out.** The list did
  already; the last commit (`76b20d728`) changes the page's `Talk` to use the same live-only set, so an
  archived Tallyhand no longer turns mindzie AI Reports' page button into the no-name form.

### Round 1 low items

- **Item 2 (cross-factory bulk clear) answered.** `HandledOlder_LeavesAnotherFactorysOldItemsOpen`
  registers Machine Care with a 13-day-old escalation beside Website Business's, runs Website's bulk
  clear, and asserts the corrected set is exactly Website's item. Dropping either factory filter would
  make the page count 2 (a 409) or correct both (a different set): red either way.
- **Item 3 (danger tone) answered.** `FactoryOwnerActionDto.Danger` is set on the Gateway (true for the
  bulk clear and archive, false for restore); the Cockpit passes `action.danger` through, and a test
  flips the fixture's flag and checks the button class follows it.
- Item 1 (restore after a hand switch-off) and the observations stand as written; nothing in the
  rebase changes them.

### New, minor

- **No test for the page's duplicate-head fix.** `76b20d728` changes one line of the fold and adds no
  test; `List_AnArchivedFactorysCeo_NoLongerMakesALiveCeoReadAsADuplicate` covers the list only.
  Reverting that line keeps every test green. Harm if it regressed: the page's Talk button on a live
  factory loses the head's name when an archived factory's head shares it. One assertion on
  `FactoriesScreenFold.Page(reports, ...).Talk.Label` in that test closes it.
