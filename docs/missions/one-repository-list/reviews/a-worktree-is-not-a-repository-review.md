# Review: a worktree is not a repository

**Reviewed:** `origin/main...origin/mission/one-repo-list-a-worktree-is-not-a-repository`, the seven commits
from `36ce5cd05` to `31c79ce64` (27 files, about 3,300 lines). Nothing already merged to `main` is
re-opened here.
**Reviewer seat:** Worker on the mission workflow, a different agent family from the seat that wrote the
code. I changed no line of the work; the two attacks below were applied, run, and reverted with
`git checkout --`, and the tree was clean afterwards. No pull request was opened and nothing was merged.
**Date:** 21 September 2026. **A release (2.8.2) is queued behind this review.**

---

## Verdict

**Ship it.** Nothing found that must change. I went after the collapse with the six questions I was handed,
ran the whole mission check myself, and attacked two guards the author's eight did not include; both were
caught by exactly the tests that should have caught them. Three observations follow at the end for the
seat that built the work to accept or decline; none proves harm, none blocks the release.

---

## Scope

### What I read

The full diff; all of `LinkedWorktree.cs` (the rule), the collapse block and the forgetting block in
`KnownRepositoryStore.ObserveDiscovered`, `DiscoveredRepositoryObserver.cs`, `RepositoryUsage.cs`,
`SessionManager.RaiseSessionCreated` and `StampPrimaryRepository`, `Session.cs`, `SessionDto.cs`,
`ControlEndpoints.Map`, `RepositoryRegistry.TryAdd`/`TryAddExact`, and
`RepositoryMonitor.DefaultResolvePrimary` with the `gitIsFile` guard it kept; all six new or touched test
files in full (`LinkedWorktreeTests`, `AWorktreeIsNotARepositoryTests`,
`TheCatalogueCollapsesAWorktreeTests`, `AWorktreeIsNotARepositoryWireTests`,
`AWorktreeIsNotARepositoryTunnelProofTests`, and the diffs of `RepositoryUsageTests`,
`KnownRepositoryObservationTests`, `DiscoveredRepositoryObserverTests`, `TheCatalogueForgetsTests`,
`DiscoveredRepositoryCatalogTests`, `OneRepositoryListOrderTests`); the mission document; the record
(`proofs/a-worktree-is-not-a-repository/`: README, MEASUREMENTS, predicted-symptoms, watched-it-fail) and
the forgetting work's record (`proofs/the-catalogue-forgets/README.md`) for the posture this is held to;
the DevThrottle Method, Reviewer section; `docs/CodingStyle.md`; the repository rules.

I verified that the worktree statements the collapse consumes are built by `WorktreeInventoryService` from
`git worktree list --porcelain` — git's own answer, on the machine that owns the disk — and that
`RepoStatusDto.Worktrees` predates this change (no diff to `RepositoryDtos.cs`).

### What I ran, counts and exit codes

All on this Mac (macOS, Apple silicon), from the worktree root, `dotnet` at `~/.dotnet/dotnet`, web
workspaces on Node v22.23.2 from `.nvmrc` (this machine's default Node 26 is refused by the workspaces'
own guard, exactly as the record says — I did not try to force it).

| Command | Exit | Result |
|---|---|---|
| `npm ci` | 0 | fresh worktree populated |
| `npm run typecheck` | **0** | green, all four workspaces |
| `npm test --workspaces --if-present` | **0** | **2,205 passed, 0 failed** — client-core 1,484, cc-assistant 106, cockpit 506, mobile 109 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **0** | **0 failed, 6,959 passed, 8 skipped, 6,967 total** |
| `dotnet test src/CcDirector.Core.Tests` | **0** | **0 failed, 4,564 passed, 18 skipped, 4,582 total** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **0** | **0 failed, 800 passed, 0 skipped, 800 total** |
| parked: `dotnet test src/CcDirector.Gateway.Tests --filter "...TunnelProof|...KnownRepositoryEndpointTests"` | **0** | **31 of 31 passed, 0 skipped**, 27 s |

Every log was searched for `aborted`, `crashed`, `test host` and `was canceled`: **zero hits in all six
runs.** Every count was checked against the suite sizes I was handed (Gateway ~6,740+, Core ~4,500+,
Avalonia ~725+): all three are at or above, so no run is short. The counts match the author's record
exactly, and the new tests were measured **by name** in filtered runs — 41 Core (29 `LinkedWorktreeTests` +
12 `AWorktreeIsNotARepositoryTests`) and 49 Gateway unit (3 wire + 17 collapse + 20 observer + 9
observation), both **0 skipped**, plus the parked six — all RAN.

No test failed under load; no wall-clock-timeout failure appeared.

### What I could not reach

**Windows, PostgreSQL (no Docker on this machine — the database-backed proofs report skipped, which reads
like a pass), the live Gateway, a live Director process, and anything a screen shows.** The record states
the same limits itself, and I have nothing to add to them. The author's eight attacks I take on their
committed evidence rather than re-deriving them; my own two attacks below are the ones I did not take on
trust.

---

## The six questions

### 1. Can it collapse something that is not a worktree? No.

**The guard is `.git` is a FILE, everywhere it decides anything.** `LinkedWorktree.IsOne` is
`File.Exists(path/.git)` — and `File.Exists` answers false for a directory, so a repository proper never
reaches the rest of the chain. `RepositoryMonitor` keeps its own pre-existing `gitIsFile` guard before it
canonicalizes. **The sentence is in the code beside the guard** — `IsOne`'s documentation says, in full
capital letters, that the guard "is not the same as 'git can answer'" and why — which is the Delivery
Lead's condition 1 and condition 4 satisfied, not just in the record.

I walked the chain against every shape I was handed:

| Case | Where it stops | Test |
|---|---|---|
| an ordinary sub-folder inside a repository | `IsOne` — no `.git` file | `APlainFolderInsideARepository_IsLeftAlone`, with the trap sentence in the test |
| a repository proper | `IsOne` — `.git` is a directory | `ARepositoryProper_IsNotAWorktreeAndIsLeftAlone` |
| a bare repository's worktree | `PrimaryWorkingTreeOf` — the git directory is not called `.git` | `ABareRepositorysWorktree_IsLeftAlone...` |
| a dangling `.git` file | the disk check at the end | `AGitFilePointingAtNothing_IsLeftAlone` |
| a `.git` file pointing at a deleted parent | `IsRepositoryProper` — the positive proof | `AWorktreeWhoseRepositoryWasDeleted_IsLeftAlone`, with REAL `git worktree add` |
| a folder that was never a repository | `IsOne` | `AFolderThatDoesNotExist_IsLeftAlone`, `NoPathAtAll_IsLeftAlone`, and the real-row case at the Director |
| a **submodule** | `RepositoryGitDirectoryOf` — second-to-last segment is `modules`, not `worktrees` | `ASubmodule_IsLeftAlone_BecauseASubmoduleIsARepository`, **pinned by a test**, plus the `RepositoryGitDirectoryOf_AnythingElse_IsNull` row for `modules` |

The submodule is pinned, not lucky — the Delivery Lead's condition 2, done as ruled. The only text
comparison in the class is against `worktrees`, documented in the code as git's own layout constant and
with the submodule named as the thing it excludes. A relative `gitdir`, a file that says something else
entirely, and both separators are all tested.

And the other half — the collapse on the Gateway — does not decide worktree-ness AT ALL. It acts only on
statements that arrived on the push, and I verified those statements are built by `git worktree list` on
the Director. So even a complete failure of the Gateway's imagination cannot fold a plain folder: there is
nothing to fold with.

### 2. Positive resolution only. Confirmed.

The collapse runs inside `ObserveDiscovered`, which runs only on a push; a silent Director produces no
push, no call, and no collapse. Within a push it needs `reconcile` (never a cold start, never an
all-provisional warm-cache push), a non-blank statement, the named repository in THIS push's snapshot, and
a row on THIS machine. Silence in every form is a test: no statements at all, a folder not named, a
non-real push, a repository this push does not report, blank statements, another machine's row, another
Director that said nothing about it. I reverted the store's own `reconcile` gate myself (attack I below) —
the author proved the observer's guard by attack G but nobody had reverted the store's, and it is held:
one test, exactly the right one.

A second Director on the same machine can only collapse worktrees of repositories IT scanned, and the
scope of every lookup is the machine's own rows (`AnotherMachinesRowIsNeverCollapsed`,
`AnotherDirectorOnTheSameMachine_CanCollapseItsOwnWorktrees`). The record-time half leans the same way: a
null stamp records the folder exactly as before, pinned at the Director, at the catalogue and end to end.

### 3. What is lost. Exactly what was accepted, and nothing more.

The repository row takes `children.Max(LastUsedUtc)` — the NEWEST — and only when it is newer than what
the repository already has, or the repository has nothing. It cannot take an older time over a newer one
(`ARepositoryUsedMoreRecentlyThanItsWorktree_KeepsItsOwnTime`), cannot invent a time out of a never-opened
row (`AWorktreeThatWasOnlyEverFound_...`), and touches nothing but the statement-named paths and the
snapshot-named repository. I attacked the newest-wins guard myself with the obvious wrong rule nobody
wrote — overwrite unconditionally — and two of seventeen tests failed, the flow and the
older-worktree case. A row that is BOTH gone and named keeps its time on the repository rather than being
deleted by the forgetting rule beside it (`AWorktreeThatIsBothGoneAndNamed_KeepsItsTimeOnTheRepository`,
with an honest comment about what that test does and does not hold). No undo, no tombstone — the accepted
cost, stated in the code in the same words as the record.

### 4. Only the Director may decide, and one feed. Confirmed.

The Gateway holds no rule that smells a path: the collapse is driven entirely by `WorktreeOfRepository`
statements from the push, and the record-time answer travels on `SessionDto.PrimaryRepoPath` because only
the machine holding the disk can read the `.git` file. `D:\ReposFred\devthrottle-p5-run-a` and
`D:\ReposMindzie\worktrees\idle-p5-a` are both handled with no test on the path's text — the two tests that
cover the owner's two examples are built so the first has no "worktrees" segment anywhere in it, which is
the direct refutation of the seventh instance of the family I was told to look for. **One feed**: no new
endpoint, no new hub method, no new push, no new tunnel verb — `RepoStatusDto.Worktrees` already existed and
is untouched; the diff adds one property to `SessionDto` and one line to the mapper. The signature change
to `RepositoryUsage.StartedIn` has **no default**, so the compiler forces every caller to state what it
knows — both callers are updated, and I found no other.

### 5. Did it break what already works? No.

The pooled rule keeps the FIRST precedence — pooled, then worktree-resolved, then the folder — pinned by a
wrong-rule-nothing-removed test at the rule itself (`StartedIn_APooledSlotThatIsAlsoAWorktree...`), at the
catalogue (`A_pooled_session_that_also_carries_a_resolved_repository_still_records_the_pools`) and at the
Director (`APooledSession_StillCreditsTheRepositoryTheSlotCameFrom`). `RepositoryMonitor`'s
`DefaultResolvePrimary` now delegates its last step to `LinkedWorktree.PrimaryWorkingTreeOf` — the same
logic it had, written once, with the same bare-repository refusal — so the monitor and the session path
cannot drift apart. The forgetting rule's tests are touched only mechanically (the new `worktrees:`
argument); every one of its guards still stands in the suite, and the interplay with the collapse is
tested in both directions (a named-and-gone row, and an unnamed-gone row still forgotten). The
`collapsedAway` set keeps the forgetting rules from counting or double-removing rows the collapse already
took — I checked both exclusion filters by hand.

### 6. The guards — the two that did not fail, and the two I added.

The author's two non-failures are **correct surprises, not gaps**, and I say so having read the
explanations: (F) the collapse/forgetting order changes only the log line and the counts, because both
rules read the same materialized rows and the time is taken off the entity either way — and rather than
leave a false claim standing, the author corrected the comment and renamed the test to what it actually
proves; (G) the provisional filter beside the worktree loop is belt and braces, because the reconciliation
guard is what holds that door — and the author measured that by moving the loop above the filter, then
wrote which guard is load-bearing into the code. Both are the honest outcome of the mission's own lesson
(the proof covers the wrong thing), found by attacking their own work.

I attacked two guards the author's eight did not include, each applied alone and reverted:

- **Attack I — revert of the store's `reconcile` gate on the collapse** (the author proved the observer's
  guard; nobody reverted the store's own). `APushThatIsNotARealObservation_CollapsesNothing` failed —
  one test, exactly the right one; 59 others stayed green, which says the rest of the class is not
  accidentally holding this door.
- **Attack II — wrong rule, nothing removed: newest-wins becomes overwrite-always.** Two of seventeen
  collapse tests failed (the flow and the older-worktree case). The ruled property in question 3 is held.

Both attacks were reverted; the tree was clean before I committed this review.

---

## Observations for the seat that built the work (none proves harm, none blocks the release)

1. **A bare repository literally named `.git` is the one shape the chain would mis-read.** If a bare
   repository's directory is itself called `.git` — so its worktrees' gitdir runs
   `<parent>/.git/worktrees/<id>` — then `PrimaryWorkingTreeOf` names `<parent>` and `IsRepositoryProper`
   accepts it (the bare repo IS a `.git` directory), so a session in that worktree would be credited to a
   folder that is not a repository. Git reserves `.git` and no sane layout stores a bare repository there;
   I would not write a line of code for it, and I record it only so the corner is known rather than found.
2. **The registry door's sub-folder case is pinned one level down, not at the door.**
   `AFolderThatIsNotAWorktree_IsRegisteredExactlyAsGiven` uses a clone and a plain folder OUTSIDE any
   repository; a folder INSIDE a repository added through Browse is left to
   `LinkedWorktreeTests.APlainFolderInsideARepository_IsLeftAlone`, because `TryAdd` delegates. Any
   refactor of `TryAdd` that stopped delegating — "just ask git", the sixth instance of the family —
   would not be caught by a registry test naming it. One test naming it would close that; it is the same
   instinct as the Delivery Lead's condition 2 (pin what is currently free), and it is a nice-to-have,
   not a defect.
3. **The branch is three commits behind `origin/main`** (`bd0393acb`, `0829e6bf4`, `312b3bd4a` — the Smart
   Restart release work; I checked the file lists: no overlap with anything this change touches). The
   mission document asks for a rebase before the pull request, and that is the Delivery Lead's to do, not a
   finding against the code.

---

## The checks I owe the seat that opened me

Stated so an empty review and a review that never ran cannot look identical: I read the full diff and all
six test files; I ran the five mission-check commands plus the parked filtered run, all green with the
counts and exit codes above and zero `aborted`/`crashed`/`test host`/`was canceled` hits; I measured the
new tests by name; I ran two attacks of my own and reverted them; I could not reach Windows, PostgreSQL,
the live Gateway, a live Director process, or any screen. The release can go on this merge.
