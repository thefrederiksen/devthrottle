# Phase 2 review - the root-folder scan reaches the Gateway

Reviewed 19 September 2026. Verdict: **no findings that prove harm.** The change is what it claims
to be - a third observer on the accepted repository snapshot, not a fourth feed - and each of the
mission's known killers is answered in the code and pinned by a test. The proof's central claim was
reproduced by this review, not taken from its own report.

## Scope

**Read.** The mission document (sections 4, 5, 6 and 7), all four phase-2 proof documents, the
complete difference of `1070e2722` against `736d9afe2` (25 files), `docs/CodingStyle.md`, the
repository root instructions, and the DevThrottle Method's Reviewer section and laws. Read outside
the difference, for context: `RepoHistoryStore` (the rule the new observer mirrors), the
`GET /directors/{id}/known-repositories` route in `GatewayEndpoints.cs`, `GatewayDbContext`'s index
on the catalog, the `DirectorHub` wiring in `GatewayHost`, and the web green-check review sitting in
this same folder.

**Ran, in this review's own worktree cut at `1070e2722`.**

| What | Result |
|---|---|
| the four end-to-end tunnel proof tests (`CcDirector.Gateway.Tests`) | 4 passed, 0 failed |
| the four new and adjacent unit classes (`DiscoveredRepositoryCatalogTests`, `DiscoveredRepositoryObserverTests`, `KnownRepositoryMigrationTests`, `KnownRepositoryStoreTests`) | 32 passed, 0 failed |
| the migration-chain guards and the boot smoke tests (`*MigrationTests`, `*MigrationChainTests`, `GatewayHostBootSmokeTests`) | 58 passed, 1 skipped - the skipped one is the PostgreSQL selector that skips without a connection string, by its own design |
| the full `CcDirector.Gateway.UnitTests` suite | 6,384 passed, 7 failed - every failure named below, all pre-existing |

**Ran as an experiment.** The proof's revert 1, re-performed by this review: the hub's one observer
call commented out, tests run, file restored and the restore verified. All four end-to-end tests
failed with the predicted symptoms; the unit classes stayed green. Confirms the proof's own finding:
a missing wire between the accepted push and the catalog is invisible to every unit test over the
store and the observer.

**Could not reach.** A real PostgreSQL - there is no Docker on this machine, the same wall the
author hit, and the one skipped test above is the same wall. The npm, Core and Avalonia suites - no
web, Core or Avalonia file is touched by this change, and the Tech Lead was running the full mission
check in the shared tree in parallel. A live Director driving `RepositoryMonitor`.

The seven failures in the full suite run are pre-existing and match the proof's named set exactly:
the `RulePrimitives` link-path test, the `RuleCandidateFilter` candidate test, the three
`SessionCommandExecutorLiveness` tests, the `CronJobStoreTests` rename-recovery test and the
`WorkListStorePersistenceTests` rename-recovery test. None of them touches anything this change
changed. They are another seat's, as the proof states.

## The mission's known killers, checked one by one

**1. Path comparison decided by the wrong machine - clean.** Both new types route every path
through the existing `KnownRepositoryStore.NormalizePathKey`, which decides Windows-ness from the
path's own shape (drive letter or Universal Naming Convention prefix) and only then uppercases.
There is no `Path.GetFileName`, no `Path.GetFullPath`, no `Path.DirectorySeparatorChar`, no
`OperatingSystem` check anywhere in the new code - this review grepped for all of them and the
grep is empty. No fourth copy of the rule was written. The repository name is carried from the
Director's observation into the row and never recomputed on the Gateway; the end-to-end test pushes
Windows paths at a Gateway running on macOS and asserts the names arrive as the Director computed
them. The spelling test (`D:\Repos\Project\` against `d:/repos/project` staying one row) exercises
shape-based Windows-ness specifically.

**2. The two ends agree on the machine name - honoured.** The writer resolves the machine from
`Registry.Get(tenant, directorId)?.MachineName`, the delegate `GatewayHost` hands the observer; the
reader resolves the owned Director and reads `director.MachineName` from the same registry. The
end-to-end test does not restate the name as a literal: it reads it back through `GET /directors`
and asserts the rows are written under exactly that name. The unit test pushes a payload machine
name that no registration holds and proves nothing is written under it. When the registration
carries no machine name the observer writes nothing at all, and the endpoint answers 409 - the
two ends fail the same way in the same place, so no row can exist where no screen can find it.
The claim in the proof is true as stated.

**3. The used half is untouchable from the discovered path - no path found where it is not.** In
`ObserveDiscovered`, a row with a last-used time is skipped before anything is assigned, and the
reconcile pass is scoped to rows with no last-used time owned by the reporting Director. A second
Director pushing the same path inserts nothing and changes nothing. This review looked specifically
for a route around both guards and found none. The reverse direction is also right: the used half's
own `Observe` lets a discovered row gain a time and stay one row, keeping its discovered facts -
which is the whole reason both halves share one table, and it is tested.

**4. The reconciliation guard - mirrored, not approximated.** `RepoHistoryStore.ObserveSnapshot`
reconciles only when at least one accepted row was seen and no row anywhere in the push was
provisional. The new observer computes `found.Count > 0 && !sawProvisional` over the same filtered
set - the same rule, arrived at the same way, and it is tested at all three of its edges: empty,
all-provisional, and mixed. One detail is better than its model: a row dropped from the snapshot
for being pathless or over-long is also dropped from the reconcile set, so a row that cannot be
keyed cannot delete the row it would have matched.

**5. Null ordering - nothing pushes the sort into the database.** `ReadForMachine` materializes
with `ToList()` and orders in memory; `OrderByDescending` on a nullable time puts null last, which
is the mission's goal 2. In phase 2 the question is not even reachable - the discovered half is
filtered out before the ordering - and the branch tip (see below) additionally writes the warning
for phase 3 on the method itself, at the lines a phase 3 seat would edit. This review found no
other reader of the catalog and no ordering pushed into structured query language anywhere in the
difference.

**6. Write amplification - the difference is computed first, and the test proves it the right way
round.** Two layers: the observer keeps a signature per tenant and Director covering everything a
fold writes (the machine, every normalized path key, every name) and answers an unchanged re-push
without touching the database at all; the store compares every field before assigning and saves
only on a real change, with the last-seen stamp deliberately coarse at one hour. The test that
proves the skip proves an absence without trusting it: it deletes the row behind the observer's
back, re-pushes the identical snapshot, and asserts the row does not come back - which can only be
true if the fold never reached the database. A changed scan defeats the skip immediately, and that
is tested too. Steady state is one write pass per Director per hour, not one per push.

**7. The migrations - honest pins, nothing weakened.** Both migrations carry the same three
operations, both snapshots agree with the model, and the chain guards compare both providers
operation for operation with the count raised from eighteen to twenty-one - the three new columns,
counted. The completeness assertions (`Assert.Equal(index + n, all.Count)`) were kept, not
removed. The five guard-test updates all re-pin "the newest migration" to
`AddDiscoveredRepositories`, which is what those tests are for; no assertion was relaxed to make
them pass. The upgrade test is honest about the hard part: it migrates only to the migration
before this one, writes a catalog row as raw SQL (the database deliberately one model behind the
entity), migrates to head, and proves the row keeps its last-used time and is still served - which
matters because making a column nullable rebuilds the table on SQLite. The existing index on
(TenantId, MachineKey, LastUsedUtc) tolerates nulls in both providers.

**8. The proof itself - it covers what it claims.** The predicted symptoms were committed before
any revert was run (`585b36a44` precedes the implementation commit, verified in the history), and
this review re-ran the first revert independently and saw the same picture: four end-to-end
failures, thirty-two unit tests still green. The author's own finding - that the unit tests cannot
see a missing wire - is correct and is the reason the end-to-end suite exists. The staleness
contrast in the end-to-end test fails rather than passes if the pushed snapshot never goes stale,
so it cannot certify a state it never reached.

## The three gaps the author named, judged

- **No real PostgreSQL - matters, and it is bounded.** Every database assertion ran on SQLite. The
  one place this can bite is the live hosted table: the `ALTER COLUMN` carries a type clause, so it
  is not guaranteed to be a metadata-only change, and it takes an exclusive lock however brief.
  The catalog is small enough that this should be quick, but that belief is exactly what the
  release gate's parked run exists to check - it builds its own throwaway PostgreSQL. Nothing here
  blocks the merge; the release gate must run before any of this ships, and it is already the
  repository's own rule.
- **No real Director driving the scan - acceptable for phase 2.** The mapping from
  `RepositoryMonitor.Snapshot()` to the pushed snapshot is unchanged, shipping product code that
  `RepoHistoryStore` has consumed in production; the new observer consumes the same shape. The
  residual risk is inherited, not introduced, and phase 6 exercises it live.
- **Two Directors on one machine proved at the store only - acceptable.** The invariant lives
  entirely inside `ObserveDiscovered`; a second live tunnel would test the hub's fan-out, not the
  invariant, and the two-Directors tests do reach the store through the observer's own entry point.

## Gaps the author did not name

- **An observation for phase 3, not a finding.** The fold runs synchronously inside the accepted
  push's hub invocation, and on a cold-start Gateway - the skip signature is lost on restart - every
  Director's next push re-folds: one machine-catalog read per Director, inside the store's lock,
  which the used half's `Observe` also takes for every session start on every surface. It is bounded
  by the skip in steady state and this review could not construct a harm at realistic scale, so it
  is recorded here rather than raised. Phase 3 makes this path hotter and should keep it in view.
- **Discovered rows have no sweep.** A machine that is decommissioned leaves its never-opened rows
  held forever. The used half has the same property by design ("not part of that sweep"), and
  surviving the Director is the mission's point - not a finding.
- The reconciliation guard's accepted cost - a genuinely emptied root folder is not cleared until
  the next complete observation - is the same cost the original rule already pays and is written
  down in both places.

## The branch moved while this review ran

The mission branch advanced from `1070e2722` to `43b65ea72` ("the two traps named, answered and
written down") and then `644b18c4b` (a tense fix in the same comment). This review diffed both new
commits: documentation and code-comment lines only - the null-ordering warning in the proof's
README and on `ReadForMachine` itself. No code changed, so the review stands for the tip; the test
runs above were made against code whose only difference from `1070e2722` is comments.

## Process note

This review's first several commands ran in the shared checkout by mistake - each command starts in
the repository root and the reset was not noticed in time - before everything moved to this
review's own worktree via absolute paths. Consequences: incremental build outputs were written into
the shared tree (no source file left changed - verified); one end-to-end test run collided with the
Developer's commit at 22:58 and failed four out of four, and a rerun on the clean tree passed four
out of four, so that failure was this review's collision and not a product defect; the revert-1
experiment was performed and restored in the shared tree and the restore verified by grep and by
status. The instruction to cut a separate worktree existed precisely to prevent this, and it is
recorded here rather than left to be discovered.
