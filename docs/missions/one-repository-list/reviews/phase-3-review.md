# Phase 3 review - one list, one order, one route

**Reviewer seat:** opened by the Delivery Lead for phase 3, run under the mission workflow's
Reviewer conduct (DevThrottle Method, section 3). A different agent family from the seat that wrote
the code.

**Scope of this review:** the diff `origin/main...origin/mission/one-repo-list-phase-3`
(3 commits, 11 files), the phase's three proof documents, the mission document, the DevThrottle
Method, `docs/CodingStyle.md` (skimmed for the rules this diff touches: null safety, logging, no
fallbacks) and the repository `CLAUDE.md`. What I ran is in the verdict section. What I could NOT
reach: PostgreSQL (no Docker on this machine), the full parked `Gateway.Tests` suite, a real phone
or Director, and any screen rendering.

---

## Verdict: no blocking findings. The phase is what it claims to be.

I went looking for the null-ordering trap first, because phase 2 left a written warning about it
and because a green run in this repository proves nothing about it. The trap is not there.

### The trap, answered explicitly

**Where does the sort actually happen?** In C#, in `KnownRepositoryStore.OrderOneList`, a pure
static function over rows that are already materialized. The database query in `ReadForMachine`
ends in `.ToList()` under a lock, and `OrderOneList` takes an `IReadOnlyList<KnownRepositoryEntity>`
- not a query, not an `IQueryable`. No `ORDER BY` was added anywhere in the diff, no SQL was
written, and the comment on `ReadForMachine` now says the sort was deliberately kept in C# and what
any later move into SQL must say (`NULLS LAST` explicitly).

**Could a paging or limit clause run in SQL first?** No. I grepped the whole store for
`Take`/`Skip`/limit - there is none, and the phase's diff does not touch the query. Every row for
the machine's candidate keys is materialized before any ordering, so SQL cannot pick the wrong rows
before C# sees them. The subtler version of the defect is not present either.

**Is there a SQLite test asserting nulls sort last?** No test asserts anything about SQL ordering.
The database-backed tests (`ReadForMachine_*`) do run on SQLite, but the sort they exercise is the
C# one, so they assert the product's behaviour and not the database's mood. The provider
independence is proved the only way it can be here: `OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast`
hands the function rows in the exact order PostgreSQL's `DESC` would produce, without a database in
the room, and `OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame` does the same over
every permutation and ends by asserting the MISSION's order rather than merely a stable one. That
last assertion is load-bearing - the author says revert B would have passed through without it, and
my own re-run of revert B confirms the permutation test fails on that final assertion.

**I re-ran revert B myself.** With `.OrderByDescending(row => row.LastUsedUtc ?? DateTime.MaxValue)`
- the PostgreSQL behaviour, reproduced in C# - the unit filter fails exactly 4 of 35, the same four
tests the author recorded, with the same symptoms: the never-opened repositories at the top, the
owner's used ones at the bottom. Restored, the filter is green again (35 of 35, 0 skipped). I did
not re-run reverts A, C and D; those I take on the author's evidence, and I say so.

### The predicted symptoms were committed first - verified, not believed

`predicted-symptoms.md` is the whole content of commit `cdc98dbb3` (2026-09-19 23:45), and
`watched-it-fail.md` first appears in the code commit `225485ab9` (2026-09-20 01:25). The prediction
genuinely precedes the observation in history. One nit: both documents cite the hash `6a9c393b1`,
which is the pre-rebase hash; after the rebase the prediction commit is `cdc98dbb3`. The ordering
claim holds; the cited hash is stale. Not worth a round trip.

### The phone's route

The route's shape is CHANGED, not added alongside - the author was asked to choose and did, and the
reasoning (two routes for one machine reintroduces the mission's founding complaint) is sound. The
endpoint's own tests (`KnownRepositoryEndpointTests`, 3 tests) are untouched and still pass - I ran
them. The NEW shape is pinned by the tunnel proofs, which deserialize the real HTTP response into
`KnownRepositoryDto` and assert the null times and the `neverOpened` flags. I verified the phone's
reader against the new shape rather than taking it: a null `lastUsed` maps to `""` in
`client.ts getKnownRepositories`, and I ran the re-sort in node - the empty strings sort last and
the stable sort preserves the Gateway's order among the never-opened rows. `lastUsed` is a string
throughout the phone, so no `new Date(null)` crash path exists. The new client-core test pins all of
this. No phone or Cockpit production code changed - confirmed by the diff's file list.

### Does any client still sort for itself?

Yes, and it is deliberate and documented rather than missed:

- `client.ts getKnownRepositories` re-sorts (descending by `lastUsed` string). Harmless today -
  proved above - and the README names it as phase 5's to delete. The mission's own phase table gives
  the phone to phase 5, so this is a deferred consequence, not a defect of this phase. **It must not
  survive phase 5**: a client re-sort is a client ruling, and the whole point of the mission is that
  the Gateway rules once.
- `apps/mobile NewSession.tsx mergeRepositories` re-sorts with a different tiebreak
  (`repositoryLabel` under `localeCompare`) and re-deduplicates paths in JavaScript (`repositoryKey`,
  which decides Windows-ness from the path's own shape - pre-existing, untouched by this diff, and
  recognisably a wild copy of the path rule this mission keeps tripping over). Among never-opened
  rows the phone's tiebreak can disagree with the Gateway's name-then-path order in principle. Also
  phase 5's.
- The Cockpit's `DirectorDetailView.tsx` sorts `[...repos]` client-side - but that reads
  `GET /directors/{id}/repos`, not this route, and the Cockpit's New Session tab is phase 4's.
- The Avalonia dialog is untouched by this diff and still builds its own local union - phase 6's,
  exactly as the mission document sequences it.

None of these is in this phase's scope to fix; all three are recorded here so the later phases'
reviewers find them already named.

### The recurring path-comparison defect - no fifth copy

The only new comparisons in the diff are ordering tiebreaks (`Name` with `StringComparer.OrdinalIgnoreCase`,
`Path` with `StringComparer.Ordinal`), which decide sequence, not Windows-ness or identity. Machine
and path identity still go through `KnownRepositoryStore.NormalizeMachineKey` and `NormalizePathKey`,
which already existed and already decide from the path's own shape. Nothing in this diff looks at
`Environment` or the machine it runs on to decide what a path is. No fifth copy.

### What I ran (all on this worktree, macOS, Apple silicon, Node v26.9.0, dotnet at `~/.dotnet`)

| Run | Result |
|---|---|
| `CcDirector.Gateway.UnitTests`, filter: the phase's three test classes | **35 passed, 0 failed, 0 skipped** - matches the author's count |
| Revert B applied by me (nulls first), same filter | **Failed 4, passed 31** - the same four tests the author recorded, same symptoms |
| Restored, same filter | **35 passed, 0 failed, 0 skipped** |
| `packages/client-core` tests (`npm test`) | **1,454 passed, 0 failed** - matches the author's count, includes the new reader test |
| `CcDirector.Gateway.Tests` (PARKED), filter: the two tunnel proof classes plus the endpoint tests | **11 passed, 0 failed, 0 skipped** - over a real Gateway, a real tunnel and real HTTP |
| `npm run typecheck` | Ran as the Gateway build's pre-build step and passed (the build completed) |

**The skipped counts are stated above and they are all zero** for everything I ran. The author's
own parked-suite numbers (56 skipped of 2,693, and 21-23 failures on both this branch and
`origin/main` on macOS) are self-testimony I did NOT re-derive - running the full parked suite costs
twenty to forty minutes and the brief told me not to re-derive the author's experiments. I note that
the author measured main against a worktree cut for the purpose, which is the right shape of
evidence, and that none of the named failure families touch anything this phase wrote. The parked
suite being red on macOS at all is the Delivery Lead's problem, not this phase's, and the README
already says so plainly.

### Taken on the author's evidence, and said here

- Reverts A, C and D (I re-ran only B, the one the warning was about).
- The full parked-suite runs and their failure lists.
- The intermittent `Wingman.TurnVerdictVoiceMergeTests` event and the SQLite disposal crash in the
  test host - both outside this phase's blast radius, both handed upward rather than buried.
- The pre-rebase check counts; I ran the post-rebase branch only.

### Minor observations, none blocking

1. **The stale hash `6a9c393b1`** in the two proof documents (it is `cdc98dbb3` after the rebase).
2. **`KnownRepositoryEndpointTests` does not itself pin the new shape** - no never-opened rows, no
   flag assertions. The tunnel proofs do pin it over real HTTP, so the route is covered; the
   endpoint test would be stronger with one mixed row in it. Optional.
3. **`OrderOneList`'s never-opened tiebreak is `Name` (ignore case) then `Path` (ordinal)** while the
   phone's surviving tiebreak is label-under-locale. Two tiebreaks over one list is a second place
   the one order can fork - it resolves the moment phase 5 deletes the phone's re-sort, which is
   another reason phase 5 must not slip.

---

**Summary for the Delivery Lead.** The trap this phase was warned about is absent, and the guard
against it is real - I broke the ordering the way PostgreSQL would and watched it fail, then
watched it pass again restored. The route change is the right choice and the phone's reader is
pinned against the new shape. Nothing blocks the pull request. Three phases' worth of client-side
re-sorting remains in the wild by design (phases 4, 5 and 6); this review names each instance so
those phases can be checked against it.
