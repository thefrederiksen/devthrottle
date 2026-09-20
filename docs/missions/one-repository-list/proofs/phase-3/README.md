# Phase 3 - one list, one order, one route

Mission: One repository list, held on the Gateway. Phase 3 of section 6.

**The phase row:** *the Gateway serves the union already ordered, so no client sorts for itself.*

**The goal it serves, goal 2 of section 3:** *a repository under a registered root folder that has never
been opened appears on all three screens, below everything that has been used.* Phase 2 made the Gateway
HOLD it. Phase 3 serves it, in one order, on one route.

It is Critical Rule 7 (`CLAUDE.md`) applied to a list instead of a verdict: the Gateway rules, the clients
render. The order is **most recently used first, never-opened beneath** - the owner was asked directly and
chose recency over frequency, and nobody is to correct it into a count.

---

## 1. What the mission document claims, and what the code says

The mission has been wrong twice about the ground, so every claim phase 3 rests on was read in
`origin/main` before anything was written.

| The claim (section 5) | Verdict | What the code says |
|---|---|---|
| `KnownRepositoryStore` holds both halves in one table, the discovered half with a null last-used time | **True** | `ObserveDiscovered` inserts with `LastUsedUtc = null`; `KnownRepositoryEntity.LastUsedUtc` is `DateTime?`. |
| `GET /directors/{id}/known-repositories` serves the catalog, and the phone already reads it | **True** | `GatewayEndpoints.cs`; `packages/client-core/src/api/client.ts` `getKnownRepositories`, called from `apps/mobile/src/pages/NewSession.tsx`. |
| `ReadForMachine` filters the discovered half out, and phase 3 lifts that filter | **True** | The filter was `.Where(row => row.LastUsedUtc is not null)`, and it is what this phase removes. |
| Nothing else in the product reads `ReadForMachine` | **True** | The only caller is that one endpoint. A search of the repository for `known-repositories` finds the endpoint, the client function, the generated schema entry and tests - no Python tool, no command-line tool, no second client. |
| The sort is in memory and C# puts a null last | **True, and load-bearing** | Proved by test rather than taken on trust - see section 4. |

**Nothing in section 5 was found to be wrong this time.** The one correction phase 2 made to it (the
root-folder scan already reaches the Gateway) was re-read against `origin/main` and still holds.

One thing worth recording that section 5 does not say: **the client sorts the list again after reading
it** (`client.ts`, `list.sort(...)` in `getKnownRepositories`). It is left alone here - it belongs to the
phone, which phase 5 owns - and it is harmless, because a client re-sorting a list that is already in that
order cannot change it. Section 3 says why it is nevertheless phase 5's job to delete.

## 2. The decision this phase had to make: change the route, or add one beside it

**The route's shape is CHANGED. No second route was added.** The phone reads this route today and phase 5
is the phase that moves it, so this was the risk worth thinking about rather than deciding by reflex.

**Why changing it is the right answer, and adding one is not.** A second route serving the union while the
old one served half of it would mean the phone and the Cockpit reading two different lists for one
machine - which is the exact complaint the mission opens with, reintroduced by the phase whose title is
"one list, one order, one route". The mission's own record already expects this: phase 2 wrote *"phase 3
lifts the filter and owns the order"* on `ReadForMachine`, in the code and in its proof.

**What changed on the wire, precisely:**

| | Before | After |
|---|---|---|
| Which rows | only rows with a last-used time | every row for the machine, both halves |
| `lastUsed` | a `DateTime`, always present | `DateTime?` - null means never opened |
| `neverOpened` | did not exist | the Gateway's verdict, stamped |
| The order | most recently used first | most recently used first, **then** never-opened, by name and then path |

**What this does to the phone, checked rather than assumed.** `getKnownRepositories` maps
`String(repository.lastUsed ?? "")`, so a null time reads as an empty string, which is exactly what the
`RepoInfo` contract already documents ("or empty"). Its own re-sort puts an empty string last, so the
Gateway's order survives it. That is now pinned by a test that did not exist before -
`packages/client-core/src/api/newSession.test.ts`, *"reads the Gateway's one ordered list, never-opened
repositories and all"* - which feeds the reader the new shape, nulls included, and asserts the order and
the empty time. **No phone or Cockpit code was changed.** The phone will show never-opened repositories in
its search results from this phase, which is goal 2 arriving early on one surface rather than a
regression; the shape of its screen is untouched, which is what the owner asked be left alone.

`packages/client-core/src/api/schema.ts` is generated from a running Gateway's OpenAPI document and
already carries this route with no response shape (`content?: never`), so it needs no regeneration for
this change and did not get one.

## 3. What changed

| File | Change |
|---|---|
| `src/CcDirector.Gateway.Contracts/KnownRepositoryDto.cs` | `LastUsed` becomes nullable and `NeverOpened` is added: the Gateway's verdict, stamped rather than left to a client to infer from an absent date. |
| `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` | `ReadForMachine` serves the union. The ordering moves into `OrderOneList`, a pure static function over a materialized list - see section 4 for why that signature is the guard. A last tiebreak on path makes the list a total order. |
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | Comment only: what the route is now, that it is deliberately ONE route, and that the order is the ruling. |
| `src/CcDirector.Gateway.UnitTests/History/OneRepositoryListOrderTests.cs` | **New**, 10 tests. The order, and the failure cases. |
| `src/CcDirector.Gateway.Tests/OneRepositoryListTunnelProofTests.cs` | **New**, 4 tests. The same thing end to end, through a real tunnel and real HTTP. |
| `src/CcDirector.Gateway.UnitTests/History/DiscoveredRepositoryCatalogTests.cs` | The phase-2 test that pinned "stored and not served yet" becomes "served beneath the used half". |
| `src/CcDirector.Gateway.Tests/DiscoveredRepositoryTunnelProofTests.cs` | The two phase-2 assertions that pinned the old shape now read the served list. |
| `packages/client-core/src/api/newSession.test.ts` | **New test**, no production change: the reader the phone uses, against the new shape. |

**No writer changed, no migration, no Director-side change, and no client code.** Phase 3 is a read.

### Why `NeverOpened` exists when `lastUsed == null` says the same thing

Because Critical Rule 7 says the client is dumb. Without it, every client writes a conditional deciding
what a missing date MEANS, and a client that decides for itself renders something plausible the first time
it meets a row it did not expect - which is how the Voice screen came to offer a button that could never
work. It is set in exactly one expression, from the one field, so the two cannot disagree. The date itself
is still a date, because formatting one is layout and belongs to the screen.

## 4. The null-ordering trap, and what actually guards against it

Phase 2 left a written warning on the lines this phase edits, and it is the thing most likely to have sunk
it: `OrderByDescending` on a nullable date puts null **last** in C#, and `ORDER BY ... DESC` puts nulls
**first** in PostgreSQL. Pushing the sort into the database - the natural thing to do the moment the filter
is lifted - would have inverted the list on the hosted Gateway alone, and **every database-backed test in
this repository runs on SQLite, which agrees with C#.**

**The sort stayed in C#, over rows that are already materialized.** That is the decision, and it is said
out loud here because the brief asked for it: no `ORDER BY` was added, nothing was pushed into the
database, and the existing `.ToList()` before the ordering is now load-bearing rather than incidental.

**What guards it, given that no test here can catch a sort pushed into SQL:**

1. **The signature.** `OrderOneList(IReadOnlyList<KnownRepositoryEntity>, string)` takes a materialized
   list, not a query. Moving the sort into the database means deleting this function and the tests that
   hold it, which is a visible act in a review rather than a quiet optimisation.
2. **A test that feeds it the inverted order.**
   `OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast` hands the ordering the rows
   in the exact order PostgreSQL's `DESC` would produce - nulls first, then newest to oldest - and proves
   the served order is the mission's and not the input's. It asks no database anything, so it says nothing
   comforting about SQLite; it proves the property that makes the provider irrelevant.
   `OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame` does the same over every permutation
   of a small set, and ends by asserting the result is the MISSION's order rather than merely a stable one
   - without that last assertion it would pass straight through the defect, which revert B confirmed.
3. **The comment, rewritten in place**, on the lines an optimiser would edit, now saying that the sort was
   deliberately kept in C# and what it must say if it ever moves.

Phase 2 deliberately wrote no SQLite test asserting "nulls sort last", because such a test certifies the
comforting answer. None was added.

## 5. The behavioural proof - the flow AND the failure cases

### The order itself - `src/CcDirector.Gateway.UnitTests/History/OneRepositoryListOrderTests.cs` (10)

| Test | What it proves |
|---|---|
| `ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom` | **The case that matters most.** A machine with three used repositories and two never-opened ones: the used three in recency order at the top, the never-opened two at the bottom, and the verdict flag right on all five. |
| `OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast` | The trap, met head on - section 4. |
| `OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame` | The order is decided by the Gateway and not by the order rows arrive in, over every permutation. |
| `OrderOneList_TwoNeverOpenedRepositoriesShareAName_AreOrderedByPath` | A total order: two repositories with one name do not reshuffle between reads. |
| `ReadForMachine_RepositoryIsBothFoundAndUsed_IsServedOnceInTheUsedHalf` | The two halves meeting on one repository - one entry, with its time. |
| `ReadForMachine_TheDirectorThatFoundThemIsGone_StillServesThem` | **Failure case.** The Director that reported them is gone and the Gateway has restarted; the list is still there. This is why the catalogue is a push and not a pull. |
| `ReadForMachine_NothingIsKnownAboutTheMachine_ServesAnEmptyList` | **Failure case.** A new machine is an empty list, not an error. |
| `ReadForMachine_AnotherMachineWasScanned_ServesOnlyThisMachinesList` | **Failure case.** One machine's repositories never appear on another's list - a path from another machine is not a path a session can start in. |
| `ReadForMachine_TwoDirectorsOnOneMachine_ServeOneListBetweenThem` | Two Directors on one machine are one list, and a repository both of them found appears once. |
| `OrderOneList_OneRepositoryWrittenTwoWays_IsServedOnceWithItsTime` | Path comparison decides Windows-ness from the path's own shape, and the USED row wins the de-duplication. |

### End to end - `src/CcDirector.Gateway.Tests/OneRepositoryListTunnelProofTests.cs` (4)

A real `GatewayHost`, a real SignalR tunnel, a Director registered at an endpoint nothing listens on, and
reads over real HTTP through the route a client calls.

| Test | What it proves |
|---|---|
| `MixedMachine_IsServedAsOneList_WithTheNeverOpenedRepositoriesAtTheBottom` | **The flow.** Session snapshots make two repositories used; the root-folder scan finds those two and two more. One list of four, never-opened at the bottom, flags right, and the served list never goes up in last-used time. |
| `TheDirectorGoesAway_TheOneListIsStillServedInTheSameOrder` | **The failure case the catalogue exists for.** The tunnel closes; the same list, in the same order, both halves intact. |
| `ANeverOpenedRepositoryIsOpened_RisesOutOfTheBottomHalfAsTheSameEntry` | **The moment the halves meet, end to end.** A never-opened repository is opened and rises to the top as the SAME entry - two rows would have been a two-store design's defect. |
| `AMachineNothingIsKnownAbout_IsServedAnEmptyListRatherThanAnError` | **Failure case.** An empty list with an OK status, so a client can tell "nothing here" from "this failed". |

**Why no end-to-end test asserts that one push is newer than another.** The Gateway stamps the last-used
time from its own clock, and `DateTime.UtcNow` is coarse on Windows - two pushes milliseconds apart can
carry the same instant. A test that asserted their relative order would be measuring how busy the machine
is. What is asserted end to end is the property that holds whatever the clock does: the list never goes UP
in last-used time, and every never-opened repository is beneath every used one. Recency ordering over
controlled times is proved where the times can be controlled, in the unit tests above.

### The phase-2 pins, moved rather than deleted

Phase 2 wrote two assertions whose job was to say "and phase 3 has not happened yet". They now say what
phase 3 does: `DiscoveredRepositoryCatalogTests.ReadForMachine_DiscoveredRepository_IsServedBeneathTheUsedHalf`
and the two served-list assertions in `DiscoveredRepositoryTunnelProofTests`. Nothing phase 2 proved about
what is WRITTEN was touched.

### Watched failing

Four reverts, each aimed at a different load-bearing part, with the predicted symptom committed BEFORE any
of them ran (commit `6a9c393b1`). See `predicted-symptoms.md` and `watched-it-fail.md`. The one worth
carrying forward: with the null pushed to the front - the PostgreSQL behaviour, reproduced in C# - the
end-to-end proof failed showing `["D:\Repos\bravo", "D:\Repos\alpha"]` at the BOTTOM of the list, which is
the owner's two working repositories underneath two he has never opened.

## 6. The mission check, section 7, as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0, `dotnet` at
`~/.dotnet/dotnet`. Run from the worktree root, exactly as section 7 writes them. **These are the counts
for the branch AFTER the rebase onto `origin/main` (`8134a680b`)**, so they are the counts for the branch
as it stands rather than for an earlier base - the whole check was run twice for that reason.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces. |
| `npm test --workspaces --if-present` | **Green. 2,118 passed, 0 failed** - client-core 1,454, cc-assistant 106, cockpit 457, mobile 101. |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **Green. 0 failed, 6,490 passed, 8 skipped.** |
| `dotnet test src/CcDirector.Core.Tests` | **Green. 0 failed, 4,480 passed, 18 skipped.** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **Green. 0 failed, 554 passed.** |

**Zero failures, not "zero new failures", and no baseline is quoted.** The fourteen macOS failures phases
1 and 2 had to name are gone: `c55cf0d5d` fixed them on `main` and this branch sits on it.

### The parked suite, run explicitly

`CcDirector.Gateway.Tests` is PARKED and is not in the mission check, and this phase's end-to-end proof
lives in it, so it was run in full rather than left to continuous integration. See section 8.

### Two intermittent events, named rather than buried

Running `CcDirector.Gateway.UnitTests` seven times on this branch produced five clean runs and two events,
neither of them in anything this phase touches:

1. **`Wingman.TurnVerdictVoiceMergeTests.AVoiceSessionsStop_CostsOneJudgeCall_AndZeroTranslatorCalls_AndItsAudioIsTheVerdictsSpokenSection`
   failed once**, on the first run after a fresh build. It passes alone (5 of 5) and passed in the six
   other full runs. The failure message was not captured - that run was logged at quiet verbosity - and
   **I will not guess at the mechanism from the test's name**, so I cannot say here whether the defect is
   in the test or in the product. It is handed to the Delivery Lead with that said plainly.
2. **The test host crashed once**, aborting a run after 507 tests:
   `System.ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'` from
   `SqliteTransaction.RollbackExternal` ← `rollback_hook_bridge_impl`. That is a SQLite connection being
   finalized rather than disposed while a rollback hook is registered - a defect in the test host's
   lifecycle, not in product code, and not in a test this phase wrote or touched.

**Neither is this phase's**, and that was checked rather than assumed: a worktree cut from `origin/main`
(`ff6933f55`) ran the same suite three times, green each time, and this branch then ran it three more
times, green each time. Both events are in areas with no connection to a repository catalogue. They are
recorded because an intermittent failure is a defect in either the test or the product and somebody has to
own it - not as a baseline excusing anything, because the check above is green.

## 7. What this proof does NOT cover

- **PostgreSQL.** No PostgreSQL server was started - this machine has no Docker - so every database
  assertion here ran on SQLite. The ordering is proved not to depend on the provider (section 4), which is
  a different and stronger claim than "it was tried on PostgreSQL", but it is not that claim. **A
  PostgreSQL-backed test was deliberately NOT written**: it would have skipped silently on this machine,
  and a check that certifies a run that never happened is worse than no check.
- **Any screen.** No client code changed. The Cockpit still reads `/directors/{id}/repos` and the
  Director's dialog still builds its own union - phases 4 and 6. What the phone will now show is reasoned
  from its code and pinned by a test of its reader; nothing was seen running on a phone.
- **A real Director.** The push comes from `FakeTunnelDirector` over a real tunnel, speaking the real hub
  method with the real DTO, exactly as phase 2's proof did. Nothing here drove `RepositoryMonitor`.
- **Volume.** No test says what a machine with a thousand repositories under its root folders does to the
  size of this response or to the screens that render it. The endpoint has deliberately never had a result
  cap; this phase does not add one, and does not measure the consequence either. Phase 4 draws the screen
  that would feel it first.
- **The seventeen deferred path-comparison defects** listed in `proofs/green-check-dotnet/` were not
  swept, as instructed. None of them sat in this phase's way: every path this phase reads goes through
  `KnownRepositoryStore.NormalizePathKey`, which was already there.

## 8. The parked suite, run in full - and what it says about itself

This phase's end-to-end proof lives in `CcDirector.Gateway.Tests`, which is PARKED: it is not in the
mission check, `scripts/test-local.ps1` does not run it by default, and continuous integration is the only
other place it runs. So it was run in full here rather than left to somebody else.

**This phase's four tests pass, and so do phase 2's four and the three endpoint tests** - 11 of 11, run
again after the rebase.

**The suite as a whole is RED on macOS, and it is red on `origin/main` too.** That is stated with a
measurement rather than asserted:

| Run | Result |
|---|---|
| This branch, full suite | **Failed: 21**, Passed: 2,616, Skipped: 56, Total: 2,693 (36m 29s) |
| `origin/main` (`8134a680b`), a worktree cut for the purpose, full suite | **Failed: 23**, Passed: 2,614, Skipped: 56, Total: 2,693 (21m 52s) |

**Nineteen failures are identical, name for name.** The six that differ are intermittent members of the
same two families - three `TunnelExplicitRouteProofTests` and one `WingmanMenuGuardProofTests` failed only
on `main`; one `SessionWsProxyEndpointsTests` and one more `TunnelExplicitRouteProofTests` failed only
here. Nothing this phase touches appears anywhere in either list: they are fleet spawn, workflow seats,
tunnel route proofs, the hosted process-control denials, the voice sweep, and the suite's own
machine-wide lock test.

**This is not a baseline being quoted to excuse a red run**, and it must not be read as one. The mission
check in section 6 is green with zero failures. This is a separate, worse fact found while doing the job:
**a parked suite of 2,693 tests is 23 red on this platform, nobody is looking at it, and by its own
charter continuous integration is the only place it runs.** It is reported to the Delivery Lead rather
than swept up here - it is several seats' worth of work in six families and none of it is this phase's -
but it is recorded so that the next seat who runs this suite does not spend the afternoon I spent working
out whether the red was theirs.
