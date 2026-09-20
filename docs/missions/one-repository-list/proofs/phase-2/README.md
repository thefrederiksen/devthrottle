# Phase 2 - the root-folder scan reaches the Gateway and is held as found-but-never-opened

Mission: One repository list, held on the Gateway. Phase 2 of section 6.

It serves goal 2: *a repository under a registered root folder that has never been opened appears on all
three screens, below everything that has been used.* Phase 2 makes the Gateway HOLD it. Phase 3 serves it.

---

## 1. The mission document was wrong about the ground, and is corrected

Section 5 said "The root-folder scan has no route to the Gateway at all." **It was false.** The sentence
was written from a search for the words "root folder" on the Gateway, which returns nothing because the
scan travels under a different name - as a repository snapshot. The road exists and it ships.

Every line of it was checked against `origin/main` before a line of code was written, and the correction is
now in the mission document itself (section 5, "The root-folder scan already reaches the Gateway - what is
actually there"), naming the files, so phases 3, 5 and 6 do not have to rediscover it. The inferred
decision in section 4 - that the Director PUSHES rather than the Gateway pulling - turned out to be RIGHT
and was already built.

## 2. What was built: a THIRD OBSERVER, not a fourth feed

No new Director-to-Gateway feed, no new endpoint, no new tunnel verb, and **no Director-side change at all**
beyond a test helper. A second pusher on a second cadence would be two feeds describing one machine, and two
feeds describing one machine disagree - which is the defect the mission exists to end.

`DirectorHub.PushRepoSnapshot` already hands each ACCEPTED push to two observers. There are now three, and
each keeps the snapshot for a different length of time:

| Observer | Where it keeps it | How long |
|---|---|---|
| `PushedRepositoryStore` | in memory, per Director | until that Director has been silent past the staleness window |
| `RepoHistoryStore` | a file of daily rows | 26 weeks, for the morning report |
| **`DiscoveredRepositoryObserver`** (new) | the durable machine catalogue | until the repository stops being found |

### One table, not two

`KnownRepositoryEntity` - the catalogue the phone already reads - gains the found-but-never-opened half
rather than a second store beside it:

- `LastUsedUtc` becomes **nullable**. Null IS "found under a registered root folder and never opened". That
  is already the Director dialog's own rule (`NewSessionDialog.BuildRepositoryList` leaves the last-used
  time unset for a discovered repository) and the mission's third inferred decision.
- `DiscoveredByDirectorId` records which Director reported it - the ownership and reconciliation scope.
- `LastSeenUtc` records when that Director last did.

One table because a repository that is found today and opened tomorrow stays ONE row that gains a time,
rather than becoming a de-duplication problem across two stores. It makes phase 3 a SORT rather than a
MERGE.

### The store's new entry point, and the three invariants

`KnownRepositoryStore.ObserveDiscovered` - full-snapshot semantics, scoped to (tenant, machine, reporting
Director, no last-used time):

1. A repository with no row is inserted with **no** last-used time.
2. **A row that already has a last-used time is left alone** - not refreshed, not re-stamped, not claimed.
   The used half is untouchable from here. A discovered observation never creates, moves or clears a
   last-used time.
3. **A never-opened row belongs to the Director that reported it**, and another Director reporting the same
   path leaves it alone. Two Directors on one machine cannot rewrite or delete each other's rows.

Removing a root folder takes effect through reconciliation: never-opened rows for THAT Director that are no
longer in the snapshot are removed.

### The reconciliation guard, copied from the observer that already paid for it

An EMPTY push and an ALL-PROVISIONAL push both look exactly like "every repository was removed", and
neither is: a cold start before the first live scan pushes nothing, and a warm-cache push carries only
entries the Director has not re-verified. A MIXED push is a partial view. So reconciliation runs only when
at least one entry was verified AND no entry anywhere in the push was provisional - the rule
`RepoHistoryStore.ObserveSnapshot` states in its own remarks, mirrored rather than re-invented.

### The machine name comes from the REGISTRATION

`GET /directors/{id}/known-repositories` resolves the owned Director and looks rows up by
`director.MachineName`, never by anything in a payload. So the writer resolves it from the same source -
`Registry.Get(tenant, directorId)?.MachineName`, the delegate `GatewayHost` hands the observer. A row
written under the payload's machine name would exist while no screen could ever show it, and a store-level
test that wrote and read with the same string would pass while proving nothing about that. It is proved
through the two ENDS instead: written through the hub, read through the endpoint's own path.

**When the registration reports no machine name, nothing is written at all.** There is no second-best name
to fall back on, so a row under the wrong machine is worse than no row.

### Paths, and what the Gateway is not allowed to ask the host

Every path goes through the existing `KnownRepositoryStore.NormalizePathKey`, which decides Windows-ness
from the PATH'S OWN SHAPE (a drive letter, or a UNC prefix) and only then uppercases. No
`Path.GetFileName`, no `Path.GetFullPath`, no `Path.DirectorySeparatorChar`, no `OperatingSystem` check
anywhere near a pushed path: the Gateway is a Linux container holding paths written by Windows and macOS
machines, and is never the machine a path describes. **The name is never recomputed from a pushed path** -
the Director computed it on the machine that owns the path and it rides in the observation.

### Phase 2 stores; it does not serve

`GET /directors/{id}/known-repositories` returns exactly what it returned before: only rows with a
last-used time, same order, same shape. The phone reads it and is not touched until phase 5. Phase 3 lifts
the filter and owns the order. There is a test pinning it in both suites.

## 3. Files changed

| File | Why |
|---|---|
| `src/CcDirector.Gateway/Data/Entities/KnownRepositoryEntity.cs` | nullable last-used, reporting Director, last-seen stamp |
| `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` | `ObserveDiscovered`, and `Observe`/`ReadForMachine` made honest about a null time |
| `src/CcDirector.Gateway/History/DiscoveredRepositoryObserver.cs` | **new** - the third observer: provisional filter, reconciliation guard, machine-name resolution, unchanged-re-push skip |
| `src/CcDirector.Gateway/Streaming/DirectorHub.cs` | the one call, on the accepted push, contained |
| `src/CcDirector.Gateway/GatewayHost.cs` | constructs the observer with `Registry.Get` and shares it with the hub |
| `src/CcDirector.Gateway/Data/Migrations/20260920021757_AddDiscoveredRepositories.cs` (+ Designer, snapshot) | SQLite |
| `src/CcDirector.Gateway.Migrations.Postgres/Migrations/20260920021806_AddDiscoveredRepositories.cs` (+ Designer, snapshot) | PostgreSQL |
| `src/CcDirector.Gateway.Tests/FakeTunnelDirector.cs` | gains `PushRepoSnapshotAsync` |
| five migration-chain guard tests | they pin "the newest migration", by design, and a new migration moves that name |
| `docs/missions/one-repository-list/MISSION.md` | section 5 corrected |

No Director-side product code was changed.

## 4. The proof - the flow AND the failure cases

One success run is not a proof. Every row of the mandate has a test, in one of two places.

**End to end, through the real hub and the real endpoint** -
`src/CcDirector.Gateway.Tests/DiscoveredRepositoryTunnelProofTests.cs`. A real SignalR tunnel to a started
`GatewayHost`, a Director registered at an endpoint nothing listens on, and reads through real HTTP.

| What is proved | Test |
|---|---|
| a repository found under a root folder and never opened reaches the Gateway and is held with no last-used time | `RootFolderScan_PushedUpTheTunnel_IsHeldAsFoundButNeverOpened_UnderTheMachineTheEndpointReads` |
| the rows are written under exactly the machine name the read side resolves, read back from the registration through `GET /directors` rather than restated as a literal | same test |
| the endpoint still serves only the used half | same test, `Assert.Empty(await ServedAsync())` |
| it survives the Director going offline, while the in-memory copy goes stale and returns nothing | `DiscoveredRepositories_OutliveTheDirector_WhileThePushedSnapshotGoesStale` |
| a repository that IS used keeps its last-used time when the scan runs over it, as one row and not two | `RepositoryThatIsUsed_KeepsItsLastUsedTime_WhenTheRootFolderScanRunsOverIt` |
| removing a root folder removes the never-opened rows and leaves the used ones | `RootFolderRemoved_RemovesTheNeverOpenedRows_AndAnEmptyPushRemovesNothing` |
| an empty push and an all-provisional push remove nothing | same test |

The staleness contrast is real rather than simulated: the test writes `staleAfterSeconds: 1` into
`config.json` before the Gateway starts - the same key a real install uses - then polls `GET /repositories`
until it is empty, and **fails rather than passes if that never happens**, so it cannot certify a state it
never reached.

**Unit** - `src/CcDirector.Gateway.UnitTests/History/DiscoveredRepositoryCatalogTests.cs` (14) and
`DiscoveredRepositoryObserverTests.cs` (11): each invariant on its own, both path spellings, two Directors
on one machine, the name never blanked, blank machine or Director refused rather than written somewhere
wrong, and the unchanged-re-push skip.

**The upgrade** - `KnownRepositoryMigrationTests.AddDiscoveredRepositories_CatalogAlreadyHasRows_KeepsEveryLastUsedTime`
migrates a database to the migration immediately before this one, writes a catalogue row **as SQL** (the
entity is the current model; the database is deliberately one behind), migrates to head, and proves the row
keeps its last-used time and is still served. Making a column nullable rebuilds the table on SQLite, so
that the rows survive it is a thing to prove rather than assume.

**Watched failing:** three one-line reverts, each aimed at a different load-bearing part, with the symptom
written down and committed first. See `predicted-symptoms.md` and `watched-it-fail.md`. The finding worth
keeping: with the hub's one call removed, all 32 unit tests still pass and only the end-to-end four fail.

## 5. The mission check, as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0. `dotnet` at
`~/.dotnet/dotnet`.

| Command | Result |
|---|---|
| `npm run typecheck` | green |
| `npm test --workspaces --if-present` | **2,117 passed, 0 failed**, exit 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 6,384 passed, **7 failed** - none of them mine |
| `dotnet test src/CcDirector.Core.Tests` | **0 failed**, 4,445 passed, 18 skipped |
| `dotnet test src/CcDirector.Avalonia.Tests` | 543 passed, **7 failed** - none of them mine |

The fourteen .NET failures are another seat's and land before this branch rebases: seven in
`Gateway.UnitTests` (`RulePrimitives` and `RuleCandidateFilter` link paths, three
`SessionCommandExecutorLiveness`, two rename-failure simulations) and seven in `Avalonia.Tests` (six audio,
one `LegacyWorkspaceImport`). They are named, not quoted as a baseline. Every failure beyond them would be
mine, and there are none: the first full run after this change had nineteen `Gateway.UnitTests` failures,
twelve of which WERE mine - the migration-chain guards that pin "the newest migration" by design - and all
twelve are fixed.

`Gateway.Tests` is a PARKED suite and is not in the mission check, so the end-to-end proof above was run
explicitly rather than riding the gate: `Failed: 0, Passed: 4`.

## 6. What this proof does NOT cover

- **PostgreSQL.** The migration pair is generated from one model and the chain guards compare both sets
  operation for operation, but the PostgreSQL migration was not applied to a real PostgreSQL server here -
  no Docker was started for this work. Every database assertion above ran on SQLite.
- **A real Director.** The push comes from `FakeTunnelDirector` over a real tunnel, speaking the real hub
  method with the real DTO. Nothing here drove `RepositoryMonitor` itself, so it proves what the Gateway
  does with a root-folder scan, not that a live Director's scan produces the DTO shape this expects - that
  mapping (`ControlApiHost.SnapshotRepositories` -> `RepositoryDtoMapper.Map`) is unchanged and untested by
  this work.
- **Two Directors on one machine, end to end.** That invariant is proved at the store, not through two live
  tunnels.
- **Anything a screen shows.** Phase 2 deliberately changes no screen, and the endpoint test asserts
  exactly that nothing changed.
