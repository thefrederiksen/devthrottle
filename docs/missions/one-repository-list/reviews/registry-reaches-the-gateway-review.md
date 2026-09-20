# Review: the registry reaches the Gateway catalogue

Mission: One repository list, held on the Gateway. The completeness-gap change on branch
`mission/one-repo-list-registry-reaches-gateway`.

Reviewed by a different agent family from the seat that wrote it. Read-only: nothing was built,
nothing was fixed.

---

## Scope

**What I read.** The mission document; this change's record (`proofs/registry-reaches-the-gateway/` -
README, predicted-symptoms, watched-it-fail) and the phase 2 record it was held to
(`proofs/phase-2/README.md`); every source file in the diff (`DirectorRepositorySnapshot.cs`,
`ControlApiHost.cs`, `RepositoryDtoMapper.cs`, `RepositoryMonitor.cs`, `RepositoryDtos.cs`,
`DirectorHub.cs`) and the phase 2 code it rides on (`DiscoveredRepositoryObserver`,
`KnownRepositoryStore.ObserveDiscovered`, `GatewayStreamClient`'s reseed path); all five test files
the change adds; the two path helpers it names (`WorktreeReaperService.NormalizePath`,
`RepositoryPaths.FolderName`); the DevThrottle Method, Reviewer section; `docs/CodingStyle.md` and
the repository `CLAUDE.md`.

**What I ran.** `npm ci`, then the mission check in full: `npm run typecheck`,
`npm test --workspaces --if-present`, and the three dotnet suites - plus this work's end-to-end
proof classes in the parked `CcDirector.Gateway.Tests`. Counts below, SKIPPED stated as loudly as
passed.

**What I could NOT reach.** A full run of the parked `Gateway.Tests` suite - abandoned after
eighteen minutes against my time budget, so the suite-wide passed/skipped counts are not mine
(separate proof classes were run and are below; the suite-wide "red on main for unrelated reasons"
claim is taken on its evidence, not re-derived). PostgreSQL, in any form: this machine has no
Docker, which I verified (`docker: command not found`) - the record's claim is environmental fact.
A live Director process, anything a screen shows, and the two-live-tunnel two-Director scenario the
record's own section 7 names as untested.

---

## The four questions the brief posed

### 1. Did it follow phase 2's pattern, or build a second feed?

**It followed the pattern. No second feed.** Verified against the diff itself, not the record:

- The union is folded into the ONE existing snapshot, on the Director, in
  `ControlApiHost.SnapshotRepositories`. No new push, no new cadence, no new trigger - the
  reseed that carries a hand-added repository up is the pre-existing `RePushTick` ->
  `RePushAsync` -> `ReseedAsync` timer at `DefaultStreamStaleAfterSeconds / 2`, which I read in
  `GatewayStreamClient` and which is unchanged by this diff.
- `DirectorHub.PushRepoSnapshot(long, RepoStatusDto[])` keeps its name and argument count, so the
  field-compatibility argument holds: `RepoStatusDto.StatusNotComputed` is one added property,
  deserialising to `false` from every older Director in the field - which is exactly right, since
  everything an older Director pushes came from the scan and carries a status. The record's
  SignalR constraint is real, not decoration.
- The only Gateway-side change is a filter inside the existing hub method. No new endpoint, no new
  tunnel verb.

The record's reconciliation argument for why a second observation was rejected is correct on the
code: the Gateway does treat an accepted push as a complete view (reconciliation in
`ObserveDiscovered` removes never-opened rows of the pushing Director that left the snapshot), so
two separately-reconciled observations of one machine would indeed delete each other's rows.

### 2. The collision case

**No double-count, and neither source can delete the other's rows.** Three layers, each verified
in code:

1. The Director de-duplicates through `WorktreeReaperService.NormalizePath` - and this is genuinely
   the scan's own keying, not a new rule: `RepositoryMonitor` keys `_byPath` and its lock and
   deferred-recompute dictionaries by exactly that function with `OrdinalIgnoreCase`. The
   `HashSet` in `Union` uses the same comparer, so "already in the scan" answers truthfully.
2. The Gateway collapses spellings independently, in `ObserveDiscovered`'s `NormalizePathKey`
   snapshot dictionary, so even a Director that failed to de-duplicate could not produce two rows.
3. Both halves ride ONE push, so reconciliation sees them together. Un-watching a folder removes
   the scan's never-opened rows and cannot touch the registry's (they are in the push); removing a
   registry entry removes its row and cannot touch the scan's (they are in the push). Both
   directions are proved end to end in `OneSourceShrinking_RemovesOnlyItsOwnRows`, which I read
   line by line and which does what its name says.

One observation, offered as an observation and NOT as a finding: a never-opened row belongs to the
Director that first reported it (invariant 3). If Director A un-watches a folder while Director B
on the same machine still lists that repository in its hand-built registry, A's reconcile removes
the shared row and B's next reseed (~ten seconds) re-inserts it owned by B. That is pre-existing
phase 2 mechanics - this diff changes nothing about it - and it heals within one reseed; the
record's section 7 already names the two-Director end-to-end case as untested. Not a defect this
review asks anyone to act on.

### 3. A repository that has been used keeps its last-used time

**Yes, from either source.** `ObserveDiscovered`'s invariants are untouched by this diff: a row
with `LastUsedUtc` set is skipped entirely (invariant 2), and reconciliation is scoped to
`LastUsedUtc is null` rows of the pushing Director only. The registry's OWN last-used stamp is
deliberately not sent - and I verified the strongest form of that claim: `RepoStatusDto` has no
last-used field at all, so an older or careless caller could not send one either. The hub tests
and the end-to-end re-push test (`..._KeepsItsTime`, which pushes the registry again after the
repository is used, exactly as the ten-second tick does) prove it.

### 4. Path comparison - is there a sixth copy of the rule?

**No.** The only new path logic in the diff is on the Director, and it is the machine that owns
the paths: `NormalizePath` there (filesystem resolution is correct on the machine that owns the
path, and it is the monitor's own keying), `KnownRepositoryStore.NormalizePathKey` on the Gateway
(decides Windows-ness from the path's shape - unchanged by this diff), and
`RepositoryPaths.FolderName` for a nameless entry's name (both separators, host-independent). The
name is computed once and never recomputed downstream - verified: the identity-only row's `Name`
rides to the catalogue untouched. I grepped the whole diff for `Path.GetFileName`,
`Path.DirectorySeparatorChar` and `OperatingSystem.`: the one hit is in a Core test helper feeding
`"/r/a"`-style paths to a monitor, which decides nothing about any real path. Nothing on the
Gateway side asks the host anything about a path.

### 5. The record's honesty - and the one place it fails its own standard

**Every claim I checked against the code held.** The reseed cadence correction (phase 2's "ten-second
reseed" is really `RePushTick` at half the staleness window), the observer split and what each
observer is handed, `HasCompletedAScan`'s set-in-the-same-gated-decision-as-`ScanCompleted`, the
`RepositoryRegistry.Repositories` snapshot-under-lock the union reads, the claim that the registry's
own stamp has no field to travel in - all true. The record is also honest about its failures: two
wrong predictions stated as wrong, an intermittent Core red handed to the Delivery Lead unnamed
rather than diagnosed, and a section 7 that states the Docker gap before I had to look for it.

**One finding, against the record, not the code:**

> **Section 6 promises a measurement it does not contain.** It says "The suite as a whole is
> recorded below with its SKIPPED count stated as loudly as its passed count" - and below sits a
> bare `<!-- PARKED-SUITE-RESULT -->` placeholder, with nothing in it. The same sentence cites
> `../the-parked-suite-nobody-runs.md`, and **no file of that name exists anywhere in this
> repository** - I searched the whole tree.
>
> **The harm:** the next seat reading this record believes the parked suite's overall state was
> recorded, when the sentence that says so points at an empty comment and a dead path. It matters
> because this mission's own method says a skipped test reads exactly like a passing one in every
> report we produce - the record's own loudest rule - and here the record claims to have stated a
> count it never states. The process fact explains it (the Developer went idle before committing;
> the Delivery Lead committed the content unedited, and no author re-read it): this reads like a
> sentence written for a paste that never came.
>
> **What it does NOT invalidate:** the claim that matters for THIS work - "this work's seven tests
> pass... 17 of 17, with 0 skipped" - I reproduced (see counts below; fifteen of the seventeen are
> the parked-suite classes I ran, the other two are endpoint-fold tests I could not identify by
> that name but which live in a suite I ran green in full). The finding is confined to the record's
> section 6 and to the dead citation.

### The revert lesson, applied

The brief warned that a revert proves the guard catches that DEFECT, not the CLASS. I asked it of
all four:

- **Revert C** is the one the lesson was written for, and the record already answered it the right
  way: the test first failed on the GUARD (`Assert.Empty(coldPush)`), was reordered to assert the
  CONSEQUENCE first, and now reports `Expected: 3, Actual: 1` - the damage, not the mechanism.
- **Revert D** found an unpredicted SECOND failure (`OneSourceShrinking`, via the empty model the
  un-watching leaves behind). A suite that catches the same defect class by a route the author had
  not seen is the best evidence available that it catches the class and not the instance.
- **The other direction of the guard** - `HasCompletedAScan` stuck false forever, which would keep
  the registry off the Gateway silently - is covered by the flow tests, not the reverts: the
  wiring test asserts the registry arrives the moment the scan settles, and every end-to-end test
  scans first and expects its registry rows. That direction is not unguarded.
- **Revert B's carried finding** - a catalog-only proof passes straight through the containment
  defect - is genuine, and the containment has its own tests in both directions (identity rows
  never reach the status surfaces; measured rows still reach everything).

---

## The check, as I ran it

Machine: Sorens Mac mini, Apple silicon, `dotnet` at `~/.dotnet`, Node v26.9.0, from the worktree
root, on the branch tip `3322f201c`.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green**, all four workspaces, zero errors |
| `npm test --workspaces --if-present` | **Green. 2,126 passed, 0 failed, 0 skipped** - client-core 1,456, cc-assistant 106, cockpit 457, mobile 107. Identical to the record's counts |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **Green. 0 failed, 6,591 passed, 8 SKIPPED**, total 6,599. The record's 6,575 was on the older base it names; the sixteen extra are main moving, and the 8 skipped match its named list (six `HostedSchemaRefusesAnUnownedRowTests`, the Postgres boot smoke, and `DT_TEN_3`) |
| `dotnet test src/CcDirector.Core.Tests` | **Green. 0 failed, 4,491 passed, 18 SKIPPED**, total 4,509. Identical to the record |
| `dotnet test src/CcDirector.Avalonia.Tests` | **Green. 0 failed, 646 passed, 0 skipped**, total 646. Identical to the record |

**The parked `CcDirector.Gateway.Tests`, where this work's end-to-end proof lives:** run
explicitly, filtered to the three proof classes. **15 passed, 0 failed, 0 skipped** in 13 seconds -
this work's seven `RegistryReachesTheGatewayTunnelProofTests`, phase 2's four
`DiscoveredRepositoryTunnelProofTests`, and phase 3's four `OneRepositoryListTunnelProofTests`. The
record's "17 of 17" includes two endpoint fold tests I could not identify by that name; they are
not in `Gateway.Tests`, and whatever suite holds them is one I ran green in full. **A whole-suite
run was abandoned after eighteen minutes** against this review's time budget, so the suite-wide
counts - the very numbers section 6's placeholder was supposed to hold - are not mine to report;
the suite's known-red-on-main state is taken on the record's evidence and was not chased, as
instructed.

**The intermittent Core red the record hands to the Delivery Lead**
(`SessionManagerTests.SaveCurrentState_ConPtySession_IsPersisted`) **did not recur**: my full
`Core.Tests` run was green first time, 0 failed. That is one more non-reproduction, not a
diagnosis - the hand-off stands.

**PostgreSQL: nothing here ran against it.** No Docker on this machine (verified - no `docker`
binary), so the seven PostgreSQL-backed proofs named in the record reported SKIPPED in my runs
exactly as in its own, and this review adds no evidence about them.

---

## Verdict

**Nothing found in the code.** The change does what the record says it does, follows the one-feed
pattern it was held to, keeps the used half untouchable, contains the identity-only half away
from every surface that reports measurements, and adds no new copy of the path rule. The proof is
real and it passes.

**One finding against the record:** section 6's unfilled `<!-- PARKED-SUITE-RESULT -->` placeholder
and its dead citation to `../the-parked-suite-nobody-runs.md` - a claimed measurement that is not
in the document, and a file that is not in the repository. That is for the seat that owns the record
to fill or correct; this review does not touch it.
