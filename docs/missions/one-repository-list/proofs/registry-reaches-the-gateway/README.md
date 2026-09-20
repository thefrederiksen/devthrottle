# The registry reaches the Gateway - closing the catalogue's completeness gap

Mission: One repository list, held on the Gateway. **Not one of the six phases.** Found by the phase 5
Developer, and it lands before phase 6.

It serves goal 2 of section 3 - *a repository ... appears on all three screens* - for a kind of
repository the Gateway could not previously have heard of at all.

---

## 1. The defect, and the ground it sits on

The Gateway's catalogue held two things:

| Half | Written by | Source on the Director |
|---|---|---|
| repositories observed in a session (phase 1) | `SessionHistoryRecorder` | every session start, on every surface |
| repositories found under a registered ROOT FOLDER (phase 2) | `DiscoveredRepositoryObserver` | `RepositoryMonitor` - the root-folder scan |

It never received the Director's own REGISTRY. `ControlApiHost.SnapshotRepositories()` mapped
`RepositoryMonitor.Snapshot()` - the scan - and nothing else.

So **a repository added to a Director by hand, not used since the Gateway started recording, and under
no registered root folder existed only in that machine's `config/director/repositories.json`.** It was
missing from the Cockpit and the phone, and phase 6 - which makes the Director's own dialog read the
Gateway list - would have lost it from the one screen that shows it correctly today. The desktop dialog
builds registry UNION scan (`NewSessionDialog.BuildRepositoryList`); the Gateway had only the second
half of that union.

**The ground was as the brief described it.** Every claim was read in `origin/main` before anything was
written: `SnapshotRepositories` at `ControlApiHost.cs`, the `WireRepositoryPush` /
`GatewayStreamClient.PushRepoSnapshot` / `DirectorHub.PushRepoSnapshot` road, the three observers on the
accepted push, and `BuildRepositoryList`'s union. Nothing in the mission document needed correcting this
time.

**One detail worth recording that phase 2's proof states slightly loosely.** It says the push happens
"plus the ten-second reseed in `GatewayStreamClient.ReseedAsync`". `ReseedAsync` itself runs on connect
and reconnect only; what makes it periodic is `RePushTick` -> `RePushAsync` -> `ReseedAsync`, on a timer
of `DefaultStreamStaleAfterSeconds / 2`. The effect is the one phase 2 describes, and this work depends
on it: **a repository added to the registry by hand reaches the Gateway on the next tick**, about ten
seconds, without a Director restart and without a new trigger.

## 2. The four questions the brief said to answer deliberately

### 1. Ride the existing snapshot, or observe the registry separately at the Gateway?

**It rides the EXISTING snapshot. The union is done on the DIRECTOR, in `SnapshotRepositories`.** No
fourth push, no new endpoint, no new tunnel verb, no change to the hub method's signature.

**The reason is reconciliation, and it is the same reason phase 2 added an observer rather than a feed.**
The Gateway treats a Director's repository push as that Director's full current view and reconciles
against it: never-opened rows for that Director that are no longer in the push are removed, which is how
un-watching a folder takes effect. **Two observations describing one machine would each reconcile the
other's rows away** - the scan's push would delete the registry's rows, the registry's push would delete
the scan's, and which survived would depend on which arrived last. Two feeds describing one machine
disagree, which is the exact defect this mission exists to end. One list, pushed once, reconciled once.

There is also a hard constraint that rules the alternative out independently: **the hub method's
signature cannot change.** `DirectorHub.PushRepoSnapshot(long, RepoStatusDto[])` is matched by SignalR on
name and argument count, so a third parameter carrying the registry would make every Director in the
field fail its push outright. Adding a property to the DTO is compatible in both directions; an older
Director simply never sets it, and `false` is exactly right for those - everything an older Director
pushes came from the scan and carries a status.

### 2. What happens when the same repository arrives from both sources?

**It is one row, and neither source can delete the other's.** Three things make that true, and they are
layered on purpose:

1. **The Director de-duplicates before it pushes.** A registered entry whose path the scan already has is
   dropped, not added beside it; the scanned row wins because it is the one carrying a status. The
   comparison is `WorktreeReaperService.NormalizePath` - see question 4.
2. **The Gateway does not depend on that.** `KnownRepositoryStore.ObserveDiscovered` keys its snapshot by
   `NormalizePathKey`, so two spellings in one push collapse to one row regardless.
3. **Reconciliation cannot be collateral damage, because there is only one scope.** Both halves arrive in
   ONE push, so the reconciliation that removes never-opened rows sees them together. Un-watching a
   folder removes the scan's rows and leaves the hand-added ones; taking a repository off the registered
   list removes its row and leaves the watched ones. Both are proved, end to end, in
   `OneSourceShrinking_RemovesOnlyItsOwnRows`.

### 3. Does a repository that has been used keep its last-used time?

**Yes, and nothing here can blank it.** This is phase 2's second invariant, unchanged and not weakened:
`ObserveDiscovered` leaves a row that already has a last-used time alone - not refreshed, not
re-stamped, not claimed - and it removes never-opened rows only. A registry row for a repository someone
has worked in therefore finds an existing used row and changes nothing about it. Proved at the hub
(`PushRepoSnapshot_AHandAddedRepositoryThatHasBeenUsed_KeepsItsLastUsedTime`,
`..._AUsedRepositoryDropsOutOfTheRegisteredList_IsNotRemovedFromTheCatalog`) and end to end
(`AHandAddedRepositoryThatIsThenOpened_RisesToTheTopAsTheSameEntry_AndKeepsItsTime`, which then re-pushes
the registry to prove the ten-second tick does not undo it).

**The registry's OWN last-used stamp is deliberately never sent.** A registered entry carries a
`LastUsed` written by that Director, and pushing it would undo the first thing this mission did: phase 1
made the last-used time the Gateway's, observed from every session start on every surface, and the local
file stopped being what decides the order. So a repository this Director's file remembers but the
Gateway has never seen used arrives as never-opened and sits at the bottom until it is next used. That is
the mission's decision (section 4), stated here because it looks like an omission and is not.

### 4. Path comparison - this mission's recurring defect

**Two different questions, two existing helpers, and no fifth copy of the rule.**

| Where | Question | Helper | Why that one |
|---|---|---|---|
| the Director, in `Union` | "is this registered path already in the scan?" | `WorktreeReaperService.NormalizePath` | it is what `RepositoryMonitor` keys its own model by, so the answer agrees with what "already in the scan" means. It resolves against the filesystem, which is right HERE and only here: the Director IS the machine that owns these paths, so a junction, a symbolic link or a short name is correctly seen as the same folder |
| the Gateway, in `ObserveDiscovered` | "is this the same repository?" | `KnownRepositoryStore.NormalizePathKey` | it decides Windows-ness from the PATH'S OWN SHAPE, because the Gateway is a Linux container holding paths from Windows and macOS machines and is never the machine a path describes |
| the name for a registered entry that has none | "what is this folder called?" | `Core.Utilities.RepositoryPaths.FolderName` | both separators, host-independent. `Path.GetFileName` handed `D:\ReposFred\devthrottle` on macOS finds no separator and returns the whole path |

Nothing on the Gateway side of this change asks the host anything about a path, and the NAME is never
recomputed downstream - it is computed once, on the machine that owns the path, and rides in the
observation.

## 3. What was built

### The Director sends everything it knows, on the one feed

`DirectorRepositorySnapshot.Union` is a pure function - no monitor, no registry, no clock, no
environment - over the scan's model, the registered list, and one fact: has the first scan finished.
`ControlApiHost.SnapshotRepositories` is now three arguments to it.

A registered entry the scan did not reach becomes a row marked `RepoStatusDto.StatusNotComputed`:
**identity only**. Path, name, machine and Director are real; every status field is a default and
describes nothing. It is deliberately not `Provisional`, and the difference is written on the property:
provisional means "there is a status and it has not been re-verified", and resolves itself the moment
the scan runs; this means "no status was ever computed, and none will be until this repository comes
under a watched folder". A provisional row is unverified status; an identity-only row is verified
identity.

### The one guard that is not obvious: the registry waits for the first scan

**A registered entry is folded in only once `RepositoryMonitor.HasCompletedAScan` is true**, a new fact
on the monitor set in the same gated decision that raises `ScanCompleted`.

Without it, a Director that came up with no warm-start cache would push its registry - and nothing else -
before its first scan had run. That push has no unverified row in it, so the Gateway reads it as a
complete statement of what the Director knows and **reconciles every repository under every watched
folder away.** A warm-start cache normally masks this, because its entries are provisional and a
provisional entry suspends reconciliation; on a machine whose cache is missing or was deleted there is
nothing to mask it. Revert C watched it happen: three served repositories became one.

Neither fact the monitor already published can answer the question, which is why a third exists.
`IsScanning` is false both after a scan and before the first one, and those are opposite states; an
empty `Snapshot()` means "nothing found" after a scan and "nothing looked at" before one. Revert D
substituted the plausible-looking `Snapshot().Count > 0` and took down the machine with no watched
folders at all.

The accepted cost is that a hand-added repository reaches the Gateway on the first push after the first
scan settles rather than on the very first push. The guard is also why the machine with NO watched
folders works: its scan runs, finds nothing, and "looked and found nothing" is a settled view.

### The Gateway keeps identity-only rows away from the two observers that report status

One edit, in `DirectorHub.PushRepoSnapshot`, in the one place that already fans the accepted push out to
three observers:

| Observer | What it is for | What it is given now |
|---|---|---|
| `PushedRepositoryStore` | `GET /repositories`, `GET /worktrees` | the MEASURED rows only |
| `RepoHistoryStore` | the morning report's daily drift rows | the MEASURED rows only |
| `DiscoveredRepositoryObserver` | the durable machine catalogue | **the whole set** - identity is all it ever wanted |

Handed an identity-only row, the first two would publish its defaults as measurements: a branch nobody
read, "not clean" for a repository nobody looked at, zero uncommitted files, zero commits behind main.
Those are facts the product would be making up, and the morning report reads them. **Both surfaces
therefore show exactly what they showed before this change** - making a hand-added repository appear on
the fleet's repository status view would be a product decision nobody has taken, and it is not taken
here.

### Files changed

| File | Why |
|---|---|
| `src/CcDirector.ControlApi/DirectorRepositorySnapshot.cs` | **new** - the union, as a pure function, with the design decision written on it |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | `SnapshotRepositories` calls it; the push is no longer gated on the monitor alone; `internal` so the wiring can be pinned |
| `src/CcDirector.ControlApi/RepositoryDtoMapper.cs` | `IdentityOnly` - the row for a repository nobody measured |
| `src/CcDirector.Core/Git/RepositoryMonitor.cs` | `HasCompletedAScan` |
| `src/CcDirector.Gateway.Contracts/RepositoryDtos.cs` | `RepoStatusDto.StatusNotComputed` |
| `src/CcDirector.Gateway/Streaming/DirectorHub.cs` | the split, and a log line that counts both kinds |

**No migration, no change to `KnownRepositoryStore`, no change to `DiscoveredRepositoryObserver`, no
change to the route, and no client code.** Phase 2's store took this without modification, which is the
strongest thing that can be said for the shape phase 2 chose.

## 4. The proof - the flow AND the failure cases

### The Director's half - `DirectorRepositorySnapshotTests` (12), Gateway.UnitTests

The union's rules, with no machine to scan: the hand-added repository is pushed and marked; an
identity-only row invents no status at all (every field asserted); a machine with no watched folders
still pushes its list; the scanned rows are byte-for-byte what they were; a repository in both sources is
pushed once with its status; the same folder registered twice is pushed once; **the cold start pushes the
scan alone**; a warm-start cache is still pushed untouched; a pathless entry is dropped; a nameless entry
takes its name from the path's own shape (a Windows path, read on this machine).

### The wiring - `TheDirectorPushesItsRegisteredListTests` (5), Gateway.UnitTests

Separate from the above, for the reason phase 1's wiring test gives: those prove the union does the right
thing when something calls it; **this proves something calls it.** A real `RepositoryRegistry` over a
real `repositories.json`, a real `RepositoryMonitor` that has really scanned, and the host's own
`SnapshotRepositories`. Revert A is what says this test is not redundant: with the fix lifted back out of
the Director, twenty-three other tests in this change still passed.

### The Gateway's half - `TheRegistryReachesTheCatalogTests` (11), Gateway.UnitTests

Driven through the REAL `DirectorHub` with all three real observers. Both kinds of row reach the
catalogue; an identity-only row **never** reaches the status store and **never** becomes a daily drift
row; a push of nothing but identity-only rows leaves both status surfaces empty while the catalogue gains
a row; a push of measured rows alone is untouched by any of this; one source shrinking removes only its
own rows, in both directions; a used repository keeps its time and is not removed; the same repository
written two ways is one row.

### End to end - `RegistryReachesTheGatewayTunnelProofTests` (7), the PARKED `Gateway.Tests`

A real `GatewayHost`, a real SignalR tunnel, a Director registered at an endpoint nothing listens on, and
reads over real HTTP through the route a client calls.

**What makes this proof different from phases 2 and 3**, both of which recorded that nothing in them
drove `RepositoryMonitor` or the Director's snapshot mapping: **the rows here are built by the Director's
own code.** A real registry over a real file on disk, a real monitor that has really run a scan, and
`ControlApiHost.SnapshotRepositories` itself is what goes on the wire. The mapping phases 2 and 3 named
as untested is now driven.

| Test | What it proves |
|---|---|
| `AHandAddedRepositoryUnderNoWatchedFolder_IsServedByTheOneRoute_BeneathEverythingUsed` | **The flow.** The defect, closed: the hand-added repository is on the route, beneath the used half |
| `AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway` | the user for whom the defect was total |
| `ARepositoryTheRegistryAndTheScanBothReport_IsServedOnce` | the two sources meeting on one repository |
| `AHandAddedRepositoryThatIsThenOpened_RisesToTheTopAsTheSameEntry_AndKeepsItsTime` | the used half is untouchable, and the ten-second re-push does not undo it |
| `OneSourceShrinking_RemovesOnlyItsOwnRows` | **Failure case.** Neither source is collateral damage, in both directions |
| `ARestartBeforeTheFirstScanHasRun_DoesNotEraseWhatTheScanHadAlreadyFound` | **Failure case.** The cold start that would have deleted the other half |
| `AHandAddedRepository_DoesNotAppearOnTheStatusSurface` | **Failure case, and a silent one.** A repository nobody measured stays off the surface that reports measurements |

### The monitor's new fact - `RepositoryMonitorHasCompletedAScanTests` (6), Core.Tests

False before any scan even though no scan is running; true after one, agreeing with `ScanCompleted`; a
scan that found nothing still counts; a warm-start cache alone does not; a cancelled scan does not; and a
later scan starting does not make a settled view unknown again.

### Watched failing

Four reverts, each aimed at a different load-bearing part, **predictions written and committed before any
ran** - they ride in this branch's first commit, alongside the code. See `predicted-symptoms.md` and `watched-it-fail.md`. Two predictions were wrong and the
record says so. The two findings worth carrying:

- **Revert B took down no catalogue assertion at all.** A proof that looked only at the catalogue would
  pass straight through the containment defect - and what it would miss is the morning report quietly
  gaining measurements for repositories nobody read.
- **Revert C made the end-to-end test better.** It first failed on the GUARD rather than on the damage
  the guard prevents, so the assertions were reordered to put the consequence first. It now reports
  `Expected: 3, Actual: 1` - the two watched repositories erased by a Director that had not yet looked at
  its own disk.

## 5. The mission check, as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0, `dotnet` at
`~/.dotnet/dotnet`. Run from the worktree root, exactly as the MISSION document's section 7 writes them
(not this file's section 7, which is what the proof does not cover).

**These are the counts for the branch as it stands, rebased onto `origin/main` at `ab2770c4a`** - the
tip that carries phases 1 to 5, the shared repository reader, the preparatory fixes and the Smart
Director Restart work. Every command was re-run on that tip; nothing below is carried over from a run
against an older base.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces (client-core, cc-assistant, cockpit, mobile), zero errors |
| `npm test --workspaces --if-present` | **Green. 2,126 passed, 0 failed** - client-core 1,456, cc-assistant 106, cockpit 457, mobile 107 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **Green. 0 failed, 6,591 passed, 8 skipped**, total 6,599 |
| `dotnet test src/CcDirector.Core.Tests` | **Green. 0 failed, 4,491 passed, 18 skipped**, total 4,509 |
| `dotnet test src/CcDirector.Avalonia.Tests` | **Green. 0 failed, 646 passed, 0 skipped**, total 646 |

The same five commands were also run once on the earlier base `5749c7e12`, with the same shape and the
same zero failures (Gateway.UnitTests 6,575 passed then, 16 fewer because `origin/main` moved beneath the
branch mid-check rather than because anything here changed). **The table above is the run that counts**,
and it is the one against the branch's current tip.

The skipped counts are stated beside the passed counts on purpose: a skipped test reads exactly like a
passing one in every report we produce, and **26 of those 11,754 dotnet tests did not run.** They were
read rather than waved past, and none of them is this change's:

- **Gateway.UnitTests, 8 skipped, and 7 of them are the Docker gap.** Six
  `HostedSchemaRefusesAnUnownedRowTests` and
  `GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres`
  need a PostgreSQL server and there is none on this machine - see section 7. The eighth is
  `TenantGateArchitectureTests.DT_TEN_3_background_workers_touch_stores_only_through_TenantScopedSweep`.
- **Core.Tests, 18 skipped**, all of them the suite's own platform and environment guards: five
  Windows-only path and shim tests (`LinkDetectorTests`, `NulFileWatcherTests`,
  `CommandLineLauncherTests`, `ExecutableResolverTests`), five session-spawn tests whose race is not
  reliable under load, a real-transcription end-to-end, a pyenv tool inventory, and a worktree reaper
  test that needs an enforced file lock (`FileShare` is not enforced on this platform).
- **Avalonia.Tests, 0 skipped**, and the web workspaces report no skipped tests either.

**The new tests this work adds all ran**, which was measured by name rather than inferred from a suite
total: on this tip, a filtered run of `DirectorRepositorySnapshotTests`,
`TheDirectorPushesItsRegisteredListTests` and `TheRegistryReachesTheCatalogTests` reports **28 passed, 0
skipped**, and a filtered run of `RepositoryMonitorHasCompletedAScanTests` reports **6 passed, 0
skipped**. The parked suite's seven are in section 6. A suite total cannot tell you this: 34 passing
tests and 34 skipped tests produce the same green banner.

**Zero failures. No baseline is quoted.** Two reds were met on the way there and neither is carried:

### The Avalonia red was real, and somebody else fixed it first

`SmartShutdownSessionReaderTests.Read_NamedAndUnnamedSessions_UseTheNameTheRailShows` failed
deterministically, on **`origin/main` as well as here** - measured in a worktree cut for the purpose, so
the brief's statement that the check is green on main was not true of this suite on macOS. The cause was
the fifth instance of this mission's recurring defect: `SmartShutdownSessionReader` read a repository's
folder name with `Path.GetFileName`, which honours only the host's separator, so the Windows path its
test feeds came back whole - `Expected: "devthrottle", Actual: "D:\ReposFred\devthrottle"`.

It was fixed here with `RepositoryPaths.FolderName`, and then **pull request #3195 landed the identical
fix on main while this branch was being written.** Theirs is taken and mine dropped at the rebase; this
branch no longer touches the file. Worth stating plainly: **the product was not reachably wrong** - a
Director only ever holds its own machine's paths, so no user ever saw the wrong name. It made a
cross-platform suite pass on Windows and fail on macOS, and that is what was fixed.

### The Core.Tests red was intermittent, is not this change's, and I could not name its mechanism

`SessionManagerTests.SaveCurrentState_ConPtySession_IsPersisted` failed **once, in the first of five full
runs of that suite on this branch.** It then passed in four consecutive full runs here, passed alone, and
`Core.Tests` was green in a full run on a worktree cut from `origin/main`.

It is not in anything this change touches - it spawns a real stand-in process and reads back a persisted
session. **The failure message was not captured** (that run was logged at quiet verbosity), and three
further full runs with a results file attached did not reproduce it, so **I will not name the mechanism
from the test's name and the code around it.** What can be said: the test asserts the state of a real
spawned process immediately after spawning it, and the sibling test directly above it is SKIPPED with a
note saying that exact race is not reliable under load.

It is handed to the Delivery Lead with that said plainly rather than fixed blind, because changing a test
whose failure cannot be reproduced risks masking a real race in session startup. An intermittent failure
is a defect in either the test or the product and somebody has to own it; I could not establish which,
and saying so is the honest answer.

**It did not recur on the rebased tip.** The `Core.Tests` run recorded in the table above - a full run
on `5749c7e12` plus this branch - was green first time, with that test passing. That is one more
non-reproduction, not a diagnosis: it neither names the mechanism nor clears it, and the hand-off above
stands exactly as written.

## 6. The parked suite, run in full

`CcDirector.Gateway.Tests` is PARKED: it is not in the mission check and `scripts/test-local.ps1` does
not run it by default. This work's end-to-end proof lives in it, so it was run explicitly rather than
left to ride a gate that never looks at it.

**This work's seven tests pass**, and so do phase 2's four, phase 3's four and the two endpoint fold
tests: **17 of 17, with 0 skipped**, re-run together on the branch's current tip
(`RegistryReachesTheGatewayTunnelProofTests`, `DiscoveredRepositoryTunnelProofTests`,
`OneRepositoryListTunnelProofTests`, `RepositoriesEndpointServeFoldTests`).

The suite as a whole is recorded here with its SKIPPED count stated as loudly as its passed count,
because a skipped proof reads identically to a passing one:

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.Tests`, full suite, this branch on `5749c7e12` | **Failed: 24**, Passed: 2,624, **Skipped: 56**, Total: 2,704, 37m 22s |

**That full-suite run was measured on `5749c7e12`, one base behind the tip section 5 reports**, because
`origin/main` moved while the 37-minute run was going. It was not repeated on the newer tip: the branch's
own code is byte-for-byte identical across that rebase, the seventeen tests this mission owns were re-run
on the newer tip and are 17 of 17 there, and the two commits in between are a session-restore change and a
mission document. **That is a reasoned claim about which failures could have moved, not a measurement of
them** - a reader who needs the failure list against the exact tip has to run the 37 minutes again.

**Fifty-six tests did not run, and about forty-four of them are the Docker gap.** The skipped classes
are almost entirely PostgreSQL-named - `GatewayStatsWritePathPostgresTests` (15),
`GatewaySessionConcurrencyPostgresTests` (8), `PostgresProviderProofTests` (6),
`GatewayDatabaseLivePostgresProofTests` (4) and nine smaller `...PostgresTests` classes - plus
`HostedStatsServeTests` (8) and `DoorbellEndToEndProof` (4). **None of them is a proof of this work**,
which is the only reason that is tolerable here; it is not a reason to read the run as complete.

**The twenty-four failures are not this work's, and they are not new.** They fall entirely inside the
families phase 3 measured on `origin/main` itself and recorded in `../phase-3/README.md` section 8
(23 failed / 2,614 passed / 56 skipped on main at `8134a680b`): fleet spawn origin and mission attach,
workflow seats, the tunnel explicit-route and roster-push proofs, the hosted process-control denials,
the voice sweep and serving-loop isolation, the session websocket proxy, the context-less route census,
and the suite's own machine-wide lock test. **Nothing in a repository catalogue, a repository snapshot,
a registry or a discovered-repository path appears anywhere in that list**, which was checked name by
name rather than inferred.

Those were not chased, as instructed. This is **not a baseline quoted to excuse a red run** - the mission
check in section 5 is green with zero failures. It is the separate, worse fact phase 3 already reported:
a parked suite of 2,704 tests is two dozen red on this platform and nobody runs it.

## 7. What this proof does NOT cover

- **PostgreSQL - nothing here was proved against it.** This machine has **no Docker at all** (no daemon
  running and no `docker` binary on the path), and the `-Parked` gate that builds a throwaway PostgreSQL
  needs it. So every database assertion in every run recorded above ran on SQLite, and the
  PostgreSQL-backed proofs that exist **did not run - they reported SKIPPED**, which in a report reads
  exactly like a pass. The seven are the six `HostedSchemaRefusesAnUnownedRowTests` and
  `GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres`,
  named in section 5. This work adds no migration, no `ORDER BY` and no query, so it does not reopen
  phase 3's null-ordering trap - **but that is a reasoned claim about the code, not a measurement, and it
  must not be read as one.**
- **The gate the mission check is, and is not.** The five commands in section 5 are the mission check and
  nothing more. They do not run `CcDirector.Gateway.Tests` - where this work's end-to-end proof lives, so
  it was run separately in section 6 - and they are not the release gate
  (`.\scripts\test-local.ps1 -Parked -Configuration Release`), which cannot run on this machine at all:
  the solution holds two Windows-only projects and the `-Parked` suites need the Docker this machine does
  not have. **A green section 5 says nothing about either.**
- **Real git.** The `RepositoryMonitor` in the end-to-end proof is real - its scan, its streaming
  publishes, its reconciliation and its completed-scan fact are its own - but its enumeration and its git
  compute are injected, as they are in Core's own tests. Nothing here ran `git` against a repository on
  disk.
- **A live Director process.** `ControlApiHost.SnapshotRepositories` is driven directly. The debounce and
  the periodic tick that carry it up the tunnel (`WireRepositoryPush`,
  `GatewayStreamClient.RePushTick`) are unchanged by this work and are not exercised here, so **the
  claim that a hand-added repository reaches the Gateway within about ten seconds is read from that timer's
  code, not measured.**
- **Anything a screen shows.** No client code changed and nothing was seen running on a phone, a Cockpit
  or a desktop dialog. What phase 6 will find on the Director's own dialog is the point of this work and
  is phase 6's to prove.
- **Volume.** A machine with a large hand-built registry adds rows to a response that has never had a
  result cap. This work does not add one and does not measure the consequence, exactly as phase 3 did
  not.
- **Two Directors on one machine, for the registry half specifically.** Phase 2 proved that invariant at
  the store, and this work changes nothing about it - but no test here drives two live tunnels with two
  different registered lists.
