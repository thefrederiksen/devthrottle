# Review: the catalogue forgets, and names a repository usefully

Mission: One repository list, held on the Gateway. Branch
`mission/one-repo-list-the-catalogue-forgets`, reviewed at `1ec385d93` (the branch tip, based on
`c2bbf7d36`, which `origin/main` contains).

Reviewer seat: Worker, run under the mission workflow (pinned version 29), different agent family from
the seat that wrote the change. **This change deletes rows**, so the review went after the delete first.

---

## Scope - what I read, what I ran, what I could not reach

**Read:** the whole branch diff against `origin/main` (19 files, about 2,800 added lines); every new and
changed source file in it (`DirectorRootFolders.cs`, `KnownRepositoryStore.cs`,
`DiscoveredRepositoryObserver.cs`, `DirectorRepositorySnapshot.cs`, `ControlApiHost.cs`,
`App.axaml.cs`, `RepositoryDtos.cs`); every new test file (`TheCatalogueForgetsTests`,
`DirectorRootFoldersTests`, `TheNameARepositoryIsServedUnderTests`, the additions to
`DiscoveredRepositoryObserverTests` and `TheDirectorPushesItsRegisteredListTests`, and the parked
`TheCatalogueForgetsTunnelProofTests`); the change's record (`proofs/the-catalogue-forgets/`, all three
files, 419 lines plus the two proof files); `proofs/phase-2/README.md` (the one-feed pattern); the mission
document; `proofs/the-parked-suite-nobody-runs.md`; the DevThrottle Method Reviewer section;
`docs/CodingStyle.md`; the repository instructions. I also read the surrounding unchanged code the change
sits on - `DirectorHub.PushRepoSnapshot`, `NormalizePathKey`, `RepositoryPaths.FolderName`,
`FleetWorktreeFold` and its callers - because the delete's safety depends on them.

**Ran (all from the worktree root, on this machine):**

| Command | Exit code | Count |
|---|---|---|
| `npm run typecheck` | **0** | green, all four workspaces |
| `npm test --workspaces --if-present` | **0** | **2,161 passed, 0 failed** - client-core 1,459, cc-assistant 106, cockpit 489, mobile 107 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **0** | **0 failed, 6,731 passed, 8 SKIPPED, 6,739 total** |
| `dotnet test src/CcDirector.Core.Tests` | **0** | **0 failed, 4,500 passed, 18 SKIPPED, 4,518 total** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **0** | **0 failed, 646 passed, 0 skipped, 646 total** |
| `dotnet test src/CcDirector.Gateway.Tests` (parked, run in full, explicitly) | **1** | **26 failed, 2,631 passed, 56 SKIPPED, 2,713 total** |
| `dotnet test src/CcDirector.Gateway.Tests --filter ...TheCatalogueForgetsTunnelProofTests` | **0** | **7 passed, 0 failed, 0 skipped** |
| same, filtered to the mission's four other end-to-end classes | **0** | **18 passed, 0 failed, 0 skipped** |

**The skipped are stated beside the passed on purpose.** The eight Gateway skips are its PostgreSQL
proofs (this machine runs no Docker, so they skip rather than run); the eighteen Core skips are its own
environment guards. Neither set belongs to this change.

**Aborted-run check, done on every log:** all five full logs grepped for `aborted`, `crashed`,
`was canceled` and `test host` - **zero hits**. The counts were then checked against reality rather than
trusted: the Avalonia suite's 646 matches its own `--list-tests` discovery exactly (I re-listed it); the
Gateway suite discovers 2,713 and ran 2,713; Core's 4,500 matches the record's own count; the record's
Gateway.UnitTests count of 6,705 has grown to 6,731 because the branch's base moved (see finding 3) - the
change's own 57 unit tests were verified BY NAME in filtered runs (48 in the three new classes, and the
two existing classes together hold 25), so no test of this change silently skipped.

**The 26 parked-suite failures are not this change's, and I say how I know.** They fall into exactly the
six families the mission record already documents as macOS-red on `main`
(`proofs/the-parked-suite-nobody-runs.md`, where a clean `origin/main` measured 23 failures): fleet spawn,
workflow seats, tunnel route proofs (including the route census and the handover and roster proofs),
hosted process-control denials (they try to start `powershell`, which does not exist on this machine), the
voice sweep, and the suite's own machine-wide lock test. Not one failing name touches a repository, a
catalogue, a root folder or a known-repositories route. **I did not re-run `origin/main` to reproduce the
26 myself - that is a 40-minute run and the record's family-by-family measurement on main, plus the
name-for-name family match, is taken as the attribution.** What I did run myself is the part that matters
for this change: its seven end-to-end tests pass 7 of 7 with zero skipped, and the mission's four other
end-to-end classes pass 18 of 18 with zero skipped - 25 of 25, nothing skipped. No wall-clock flake failed
in any run I made.

**Could not reach:** PostgreSQL (no Docker on this machine - same limitation the record states); the live
hosted Gateway (nothing was written to it, and nothing should be from a review seat); a real Director
process end to end (the debounce and periodic tick are read from code, not driven); and a Windows machine
(every Windows-path behaviour is decided from the path's own shape and tested from macOS, but no run here
was made on Windows).

---

## 1. Can it delete something live?

**I could not make it, and I believe the rule cannot.** This was the whole review and it is where the time
went: every failure mode I was asked to go after fails in the KEEP direction, and each one is held by a
test I read and then watched pass.

The delete lives in one place, `KnownRepositoryStore.ObserveDiscovered`, inside `if (reconcile)`, and a
row goes only when ALL of these hold at once:

- the push is a real observation (`reconcile`: at least one verified entry, nothing provisional, so
  never an empty push, never a warming cache, and never from silence - removal happens only ON a push);
- the row is machine-scoped first (`rows` is loaded through the machine candidates and re-filtered on
  the machine key, so another machine's rows are not in the candidate set at all);
- the row's direct PARENT, after `NormalizePathKey`, is a root the Director could positively LIST just
  now - a root that answered null was dropped from the listing entirely, so it never becomes a covered
  root;
- the row's path is in neither the pushed snapshot nor that root's child listing.

I attacked it along every axis the brief named:

- **An empty listing** (a Director that lists nothing): `coveredRootKeys` is empty, so `gone` is empty.
  Pinned by the whole class of "outside every reported root" tests.
- **A missing listing, a null, an old Director**: no listing reaches the store; `rootFolders` is null;
  nothing is forgotten. Tested at the store (`APushWithNoRootFolderListingAtAll_ForgetsNothing`), at the
  observer, and end to end (`ADirectorThatSendsNoRootFolderListing_ForgetsNothing`).
- **A root that fails to enumerate halfway**: `Directory.GetDirectories` is all-or-nothing; a throw is
  caught, logged, and answered **null**, and a null root is dropped by `DirectorRootFolders.Build` before
  it can become a covered root. An all-roots-unreadable push carries NO listing at all (`Union.Stamp`
  requires a non-empty listing), so it authorises nothing. Tested at the builder
  (`Build_ARootThatCouldNotBeListed_IsNotReportedAtAll`), on the real lister
  (`ListChildFolders_AFolderThatIsNotThere_AnswersNullRatherThanAnEmptyList`), and end to end
  (`ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt`, whose assertions put the deleted-row
  consequence first).
- **A Director that pushes nothing / has gone quiet**: no push, no fold, no removal. Tested at the store
  and end to end (`ADirectorThatGoesQuiet_ForgetsNothingWhileItIsAway`, with the tunnel actually closed).
- **Two Directors on one machine where only one reports**: each push's permission is its OWN roots; the
  silent Director's rows sit under roots the reporter never listed, so their parents are not covered keys.
  Tested (`AnotherDirectorOnTheSameMachine_ForgetsNothingOutsideItsOwnRoots`). The delete is also
  machine-scoped before it is root-scoped, so a same-spelled path on another machine cannot be touched.
- **A path under no reported root, a root that stopped being watched, a repository two levels down**:
  each is a parent that is not a covered key, and the comparison is the PARENT, not a prefix - the
  specific defence against the `/Users/soren` broad-root trap, pinned by exactly one unit test
  (`ARepositoryDeeperThanOneLevelUnderARoot_IsKept`), which is the right number: one is what stands
  between the implemented rule and the plausible widening.
- **The original defect's premise**: the new listing does NOT inherit the scan's blind spot. It is a plain
  `Directory.GetDirectories`, so a worktree (`.git` is a file) is listed - proved against a real worktree
  on a real disk at the lister, at the wired Director host, and end to end
  (`ALiveWorktreeTheScanCannotSee_IsNotForgotten`, the trap's gravestone). Hidden folders and symlinks are
  ordinary directory entries and are listed; a child that cannot be stat'ed makes the whole listing throw,
  which drops the root - the keep direction.
- **The memo skip swallowing the delete push**: the observer's unchanged-re-push signature now includes
  the listing, so the one push that must forget a folder is the one that cannot be skipped - tested in
  both directions (a shrunken listing defeats the skip; the same listing re-ordered does not).
- **A stale push**: the hub hands the observer only ACCEPTED pushes (the sequence and connection gate run
  first), so a superseded Director connection cannot delete anything.

I looked hard for a spelling where a live row could die and found one class only: **a root that EXISTS and
answers an empty listing while the disk is lying** - a volume still mounting whose mount point is present
but not yet populated would read as present-and-empty and would authorise forgetting what it held. Nothing
distinguishes that from a genuinely emptied root, and no rule built on a listing can; the record does not
claim otherwise (it claims the unmounted, unreadable and un-watched shapes, and those it handles). The
exposure is bounded by every other condition (the push must still be a real observation, the row's parent
must match, and the folder must be gone from both the snapshot and the listing) and by the accepted cost
below. I name it so the Delivery Lead knows it is the one residual, not so anything changes.

**Verdict on the safety property: it holds, and it is named in the code in the ruled words** - on
`DirectorRootFolders` ("a root the Director CANNOT list is omitted entirely, so nothing beneath it is
ever forgotten"), on `RootFolderListingDto`, and on `ObserveDiscovered`. The `ListChildFolders` failure
answer (null, never an empty list) is the load-bearing half and it is tested against the real lister, not
just the injected one.

## 2. What is lost, and does it come back?

The record's claim, checked against the code: a forgotten row is **hard-deleted**
(`context.KnownRepositories.Remove`), and what goes with it is the **last-access time** - this mission's
whole signal. There is no tombstone and no undo in the code; nothing writes a deleted-path marker. **If
the folder returns, its history does not return**: the scan re-finds it, `ObserveDiscovered` inserts it
with a null `LastUsedUtc`, and `OrderByDescending` on a nullable time puts it beneath everything used
until a session runs in it again. The record says exactly this and the code does exactly this. The
record's further claim - that nothing else a screen reads is lost - also holds: the served name is folded
from the path at read time, so even the stored slug's loss is not visible on a screen, and the machine and
path are re-supplied by the next observation. The record states the cost as accepted and states that every
condition leans to keep. The one operational consequence worth the Delivery Lead's attention (already in
the record): a folder renamed and renamed back, or deleted and re-created, loses its recency - a
worktree's row goes to the bottom of the list on its return. That is the design working as ruled, not a
defect.

## 3. The name fold

- **It is in the ONE fold.** `KnownRepositoryStore.OrderOneList` is the only producer of the served list;
  `ReadForMachine` calls it; the one route (`GET /directors/{id}/known-repositories`) calls that; and the
  diff touches no client code at all - no `apps/cockpit`, no `apps/mobile`, no `client-core` conditional.
  Critical Rule 7 is satisfied by construction: the client is handed the folded name and the folded order.
- **No sixth copy of the path rule.** The folder name comes from the shared
  `RepositoryPaths.FolderName`, which reads the path's own shape and is the thing pull request 3195
  already fixed; the Gateway-side comparisons all go through the shared `NormalizePathKey`. The one
  host-dependent call (`WorktreeReaperService.NormalizePath`, which uses `Path.GetFullPath`) is on the
  DIRECTOR side, for de-duplicating roots the Director itself owns - and the code says so in so many
  words. I found no new "decide from the machine running the code" anywhere on the Gateway path.
- **The order move is disclosed.** The record has a dedicated passage - "THE ORDER CHANGED, and that is
  accepted rather than incidental" - saying the `ThenBy(Name)` tiebreak now sorts by the name that is
  SHOWN, so the phone and the Cockpit move with the Director. It also warns phase 6 that the served order
  moved. The tests pin both directions (a tie decided by the served name, and a row whose slug and folder
  name sort opposite ways). The record keeps the ruling's contrast honestly too: a name unique to its row
  keeps its stored spelling, which is why the served list is not uniformly folder names.

## 4. One feed

Held. No new hub method (`DirectorHub.PushRepoSnapshot` is byte-unchanged - the listing is a nullable
property on `RepoStatusDto`, stamped on the first row and read as the first non-null anywhere, because the
hub method is matched by name and argument count), no new endpoint, no new tunnel verb, no second pusher
and no second cadence. The listing rides the push that already fires, and a push that carries none forgets
nothing - which is what every Director in the field does until it upgrades. `FleetWorktreeFold` is the one
place the DTO is re-shaped, and it deliberately drops `RootFolders` because it exists to serve status, not
to feed the catalogue; the hub hands the observer the raw set, so the fold cannot come between the
listing and the catalogue. The write-amplification answer (the listing is in the observer's memo
signature, so an unchanged listing still costs nothing, and the same listing re-ordered still skips) is
tested in both directions.

## 5. The guards

The author says six reverts, including two wrong rules nothing removed. **Both of those are genuine
substitutions, not reverts in disguise.** Revert D substitutes a prefix test for the parent comparison -
no line of the change is removed, and the delta is a plausible "tidy-up" a later reader would make; it is
caught by exactly one unit test while all seven end-to-end tests stay green, which is the most valuable
line in `watched-it-fail.md`. Revert F substitutes "always the folder name" for the shared-or-blank rule -
again nothing removed, and it is caught because a rule that always prefers the folder name will serve a
repository with NO name when the path has no folder name in it. The prediction file was committed before
the reverts ran and was never edited afterwards - I verified that on the branch history itself
(`b713da058` carries `predicted-symptoms.md` and the code; `watched-it-fail.md` lands in the later
`36d16fe15`; the predictions file has zero diff since).

**A wrong rule I invented, beyond the author's six:** move the forgetting out of the `if (reconcile)`
gate (the plausible "tidy" of hoisting the block). It is caught three times over -
`AnEmptyPush_ForgetsNothing` and `APushThatIsNotAFullObservation_ForgetsNothing` at the store, and the
observer's provisional-push test. A second: make the sharing count case-sensitive in the name fold -
caught by `NamesThatDifferOnlyInCase_AreBothReplaced`. A third: answer an empty list instead of null for
an unreadable root - that is revert B, caught at the builder, the store and end to end.

**One guard-coverage note, not a demanded change** (finding 2 below): no test pins that a Director's
listing may not delete ANOTHER Director's never-opened rows under a covered root. Every two-Director test
passes `rootFolders: null`, so the delete's scope over never-opened rows is pinned only by the
`LastUsedUtc is not null` filter's presence, not by a test of the behaviour. I could not prove harm from
it: a widened rule would delete only rows whose folders the listing positively says are gone, which is
what the catalogue wants anyway - so this is an observation about how tightly the implemented scope is
pinned, not a finding that something live can die.

---

## Findings

Nothing blocking. The change is safe in the direction that matters, the record is honest about its costs
and its gaps, and every claim I was asked to check against the code checked out. Three minor findings, all
for the seat that owns the code to accept or decline:

1. **A code comment overstates the delete scope** (`KnownRepositoryStore.ObserveDiscovered`, the "AND
   THE FOLDER THAT NO LONGER EXISTS IS FORGOTTEN, used or not" comment). The code forgets only rows with
   a last-used time; a never-opened row of ANOTHER Director under a covered root is kept. The comment
   says "used or not" and the code says "used only". The direction of the error is safe (the comment
   claims more deletion than the code does), and the record's own four conditions state the truth
   correctly - but a comment that says the opposite of its code on the one destructive block in the
   change is worth one line from the next reader, because that reader will be the most careful person
   in the file. Fix the comment, not the code.
2. **The cross-Director never-opened scope is unpinned** (section 5 above). No test would go red if a
   later reader widened the `gone` query to another Director's never-opened rows under a covered root.
   Harm is not provable - such a rule deletes only rows whose folders are provably gone - so this is
   recorded as coverage, not as a defect. If the next seat touches this block, a one-test pin is cheap.
3. **The record's check was run on a base the branch has since moved off, and its two cited
   prediction-commit hashes are unreachable from the branch.** The record says its counts were run on the
   rebase at `217b79f63`; the branch now sits on `c2bbf7d36`, and `origin/main` has since merged past it
   (phase 6, pull request 3218, is now on main - which raises this change's stakes: the Director's dialog
   already reads this route). My own runs are on the current tip and are green everywhere this change is
   tested, so this is not a defect - but the merge should rebase onto current `origin/main` and the
   mission check re-run on that base, because the record's numbers are a reading of an older tree. The
   two hashes cited for the predictions-first commit (`403052e4a`, `55a9dd855`) are pre-rebase objects
   that no branch contains; a reader of the branch cannot follow either. The discipline itself IS
   verifiable from the branch (see section 5), so the claim stands - the citations are what do not.

## Verdict

**I return no defect that can delete something live.** The safety property the Delivery Lead ruled is
implemented as ruled, named in the code in his words in three places, proved at the builder, at the wired
Director, at the store and end to end through the real tunnel and the real route, and it survived every
attack this review could mount, including two wrong rules of my own beyond the author's six. The two gaps
the change closes are real and measured; the record - which I was the first seat to re-read after it was
committed on the Developer's behalf - proved accurate against the code on every claim I checked, and its
section of what the proof does NOT cover is unusually honest. The three findings above are minor and none
of them should hold the merge; finding 3's rebase-and-rerun is the standard pre-merge step anyway.
