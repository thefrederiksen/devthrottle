# The catalogue forgets a folder that is gone, and names a repository usefully

Mission: One repository list, held on the Gateway. **Not one of the six phases.** Two Gateway-side gaps
found by the phase 6 Developer, which block phase 6 from merging.

They serve the mission's goal directly: *all three New Session screens, pointed at the same machine,
list the same repositories in the same order*. A list of eighty-nine rows of which seventy-six are dead
folders, all of them called the same thing, is the same on three screens and useless on all three.

---

## 1. The two defects, measured rather than described

Measured against the **live hosted Gateway** on 20 September 2026, with the Director's own token, for
the machine this work was done on (`devthrottle-mac-mini`, Director
`4fbad29d-6baa-4cdd-bbee-cef6b0b50978`), by reading `GET /directors/{id}/known-repositories` and
checking every path against the disk:

| | |
|---|---|
| rows in the catalogue | **90** |
| paths that no longer exist on disk | **76** |
| of those 76, direct children of the machine's one registered root folder | **76 - all of them** |
| distinct names across all 90 rows | **3** |
| rows named `thefrederiksen/devthrottle` | 66 |
| rows named `thefrederiksen/devthrottle_internal` | 6 |
| rows with a blank name | 18 |
| rows the Director's own New Session dialog shows today | 3 |

**Gap 1 - nothing ever forgets.** `KnownRepositoryStore.ObserveDiscovered`'s reconciliation is scoped to
never-opened rows (phase 2's invariant 2), and nothing else prunes, so a folder created, worked in and
deleted stays in the list for ever. **Gap 2 - the name column says nothing.** A used row's name is
`session.RepoName` - the GitHub slug, or blank - and a Director's push can never correct it, because a
used row is untouchable from the discovered half.

Phase 6 points the Director's working three-row dialog at this route. The phone has been on it since
phase 5.

## 2. THE GROUND WAS NOT AS THE BRIEF DESCRIBED, and the correction IS the design

The brief's recommended shape rested on one sentence: *"the Director's push already means 'what exists
under my registered roots'"*. **It does not, and the difference would have deleted live folders.**

`RemoteRepoProvider.ScanLocalRepos` accepts a direct child only when its `.git` is a **directory**:

```csharp
foreach (var dir in Directory.GetDirectories(rootPath))
{
    var gitDir = Path.Combine(dir, ".git");
    if (Directory.Exists(gitDir))            // <- a worktree's .git is a FILE
        results.Add((Path.GetFileName(dir), dir));
}
```

A git **worktree**'s `.git` is a file, so a worktree has never appeared in a repository push at all.
On the same measurement:

| of the 14 catalogue rows whose folder still existed | |
|---|---|
| direct children of the one registered root | 13 |
| **of those, worktrees the scan cannot see** | **11** |

Eleven live folders - including `/Users/soren/ReposFred/devthrottle-repo-list-forget`, the worktree this
work was written in.

**Re-verified on disk on 20 September 2026, against the scan's actual rule rather than the catalogue:**
of the 29 folders directly under `/Users/soren/ReposFred`, **26 are worktrees whose `.git` is a FILE and
3 are clones whose `.git` is a DIRECTORY.** So `ScanLocalRepos` can see three of the twenty-nine folders
on this machine's one registered root. The `Directory.Exists(gitDir)` line above is quoted verbatim from
`src/CcDirector.Core/Git/RemoteRepoProvider.cs`. The brief's premise was not slightly optimistic; on this
machine the snapshot it proposed to forget against covers about a tenth of what is there. **"Forget a used row under a covered root that the Director no longer reports"
would have deleted all eleven.** That rule is revert C in `watched-it-fail.md`: applied, it deletes the
worktree and **94 of 97 unit tests and six of seven end-to-end tests still pass.**

It was reported to the Delivery Lead with the measurement before a line was written. **He ruled the
corrected shape in**, named the omitted-root property as the thing he wanted written into the code and
this record in his own words, and asked for three things that were already in flight: gap 2 stays in
this change, the record says what is lost when a row is forgotten, and the forgetting guard is attacked
with a wrong rule nothing removed. All three are below.

### What was built instead, keeping every constraint the Delivery Lead ruled

**The Director answers the question its scan cannot.** The same push - **one feed, no new push, no new
endpoint, no new tunnel verb** - also carries, for each registered root folder the Director could
positively **LIST**, the full path of every direct child folder that exists right now: a plain directory
listing that knows nothing about git, so a clone, a worktree and a folder that is not a repository at
all are equally "there".

**THE PROPERTY THAT MAKES IT SAFE, in the Delivery Lead's own words, and carried into the code in
these words: a root the Director CANNOT list is omitted entirely, so nothing beneath it is ever
forgotten.** That is the right direction for a destructive operation - it acts only on what it can
positively prove is disposable, and enumerates what to DELETE rather than what to skip. A root present
with no children authorises the Gateway to forget everything it holds under that root, and an unmounted
disk, an unreadable folder and a root that has stopped being watched all produce exactly that shape from
a naive listing. Each of them means **"I know nothing here"**, never **"nothing is here"**. The lister
answers null for a root it could not read, and a null root is dropped (`DirectorRootFolders`).

It is written on `DirectorRootFolders`, on `RootFolderListingDto` and on
`KnownRepositoryStore.ObserveDiscovered`, and it is proved twice - at the builder
(`Build_ARootThatCouldNotBeListed_IsNotReportedAtAll`) and end to end
(`ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt`) - because it is precisely the case the first
version of this rule got wrong, and it is the one that turns a tidy-up into data loss.

**The Gateway forgets a used row only when all four hold** (`KnownRepositoryStore.ObserveDiscovered`):

1. **it is a real observation** - phase 2's existing `reconcile` guard, so never on an empty push, never
   on an all-provisional one, and never from a Director that has gone quiet, because removal only ever
   happens ON a push;
2. **the row's folder's direct PARENT is one of the roots in this push's listing** - a root this
   Director could read just now;
3. **its path is in neither the pushed snapshot nor that root's child listing** - the Director has
   positively said the folder is not there;
4. and **ownership does not enter into it**, because a used row has no owner. The ROOT is the scope.
   Another Director on the same machine reports its own roots and can only ever forget what is under
   those.

**Why the direct parent and not a prefix.** The scan and the listing both reach exactly one level, so a
root speaks for its direct children and nothing deeper. A prefix test would let a broad root such as
`/Users/soren` claim `/Users/soren/ReposFred/devthrottle`, a folder it never looked at. That is revert D
- a wrong rule nothing removed - and **one unit test stands between the two; all seven end-to-end tests
pass under it.**

**What is lost, and it does not come back.** A forgotten row is **deleted**, not hidden, and what goes
with it is the **last-access time** - this mission's whole signal. **If the folder comes back, its
history does not come back with it:** a worktree re-made under the same name arrives as a new
never-opened row and sits at the bottom of the list until it is next used. There is no undo and no
tombstone. Nothing else is lost, because the row holds no other fact a screen reads - a name the
Gateway now folds from the path anyway, and a machine and a path the next observation supplies. That is
the accepted cost of a list that describes the disk, and it is why every one of the four conditions
above leans to keep.

**What is NOT forgotten, deliberately.** A repository under no reported root; a repository under a root
this Director does not watch, could not read, or has stopped watching; a repository two levels down; and
anything at all while a Director is silent. Un-watching a root therefore does **not** erase the
last-access times of what was under it, because an un-watched root is not in the listing. Every one of
these is a test in section 5.

### Why the listing rides on one row of the push

`DirectorHub.PushRepoSnapshot(long, RepoStatusDto[])` is matched by SignalR on **name and argument
count**: a third parameter would make every Director in the field fail its push outright. Repeating the
listing on every row would cost a machine with N repositories N copies of an N-entry list, on a push
that fires all day - the noisy-neighbour cost phase 2 already refused once. So it is a nullable property
on `RepoStatusDto`, stamped by the Director on the first row and on no other, and the Gateway takes the
**first non-null it finds anywhere in the set**, so no re-ordering or filtering of the push can lose it.

A Director that predates this carries none, and **a push with none forgets nothing** - which is exactly
what every Director in the field does until it is upgraded (revert A's end-to-end symptom, and a test of
its own).

### Gap 2 - the name, folded once on the Gateway

**The Delivery Lead's ruling, implemented as given:** the Gateway serves the **folder name from the
path** when the stored name is blank, or when it is a slug shared with another row and therefore tells
them apart from nothing. A name that is unique keeps its stored spelling.

It is in `KnownRepositoryStore.OrderOneList` - the one fold - and never in a client: Critical Rule 7. A
client deciding for itself when a name is worth showing would decide differently from the next client,
and three screens showing one machine would disagree again. The folder name comes from
`Core.Utilities.RepositoryPaths.FolderName`, which reads the path's own shape; `Path.GetFileName` handed
a Windows path on Linux returns the whole path, which is the fifth instance of this mission's recurring
defect (fixed on main in pull request 3195). No sixth copy of that rule was written.

**THE ORDER CHANGED, and that is accepted rather than incidental.** The `ThenBy(Name)` tiebreak now
sorts by the name that is SHOWN, so the served order changes for the phone and the Cockpit as well as
the Director. All three screens sort by the name a person actually sees. A row that keeps a unique slug
sits where that slug sorts; a row given its folder name sits where the folder name sorts.

**The two rules meet, and the order is right.** The sharing count is taken over the list AS SERVED -
after de-duplication, after another machine's rows are dropped, and (in the Gateway's own reading) after
the forgetting. Simulated against the live 90 rows, the served list becomes:

```
devthrottle-repo-list-p6              2026-09-20T11:52:07
devthrottle                           2026-09-20T11:51:07
devthrottle-repo-list                 2026-09-20T11:50:27
devthrottle-repo-list-forget          2026-09-20T11:49:57
devthrottle-repo-list-p4              2026-09-20T11:49:57
devthrottle-repo-list-fifth           2026-09-20T10:01:17
devthrottle-repo-list-fifth-review    2026-09-20T09:59:27
devthrottle-repo-list-sr-review       2026-09-20T09:59:27
devthrottle-fast-ci                   2026-09-20T05:52:53
devthrottle-ci-p4                     2026-09-20T05:06:22
devthrottle-ci-p1                     2026-09-20T05:04:22
devthrottle-ci-p3                     2026-09-20T05:04:12
ReposFred                             2026-09-17T03:54:10
thefrederiksen/devthrottle_internal   2026-09-16T19:45:52
```

Ninety rows become fourteen, every one of which existed on disk when it was measured, and fourteen
names that are all different.

**Re-checked at 09:22 on 20 September 2026, and the churn had already moved it: THIRTEEN of the
fourteen now exist.** `devthrottle-repo-list-p4` - a worktree, alive at the measurement - has been
deleted in the hour since. Nothing in the code moved between the two readings; the disk did. That is
the defect this work exists for, measured twice: under the rules on this branch that row becomes
forgettable on the next push carrying its root, and under the rules on `main` today it would sit in the
catalogue for ever. The 90/76/14 figures are a dated reading of a list that changes all day, not a
constant, and they are reported as one. `thefrederiksen/devthrottle_internal` keeps its slug because it is the only row with it -
that is the ruling as given, and it is why the list is not uniformly folder names.

## 3. What changed

| File | Change |
|---|---|
| `src/CcDirector.Gateway.Contracts/RepositoryDtos.cs` | `RootFolderListingDto` - **new** - and `RepoStatusDto.RootFolders`, the push-level fact and why it rides on one row |
| `src/CcDirector.ControlApi/DirectorRootFolders.cs` | **new** - builds the listing as a pure function with the directory listing injected, and the real lister that answers null for a root it could not read |
| `src/CcDirector.ControlApi/DirectorRepositorySnapshot.cs` | `Union` gains the listing and stamps it on the first row |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | `SnapshotRepositories` builds it; a new `rootFolders` delegate read fresh on every push; nothing reported until the monitor has completed a scan |
| `src/CcDirector.Avalonia/App.axaml.cs` | passes `RootDirectoryStore.Roots` through that delegate |
| `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` | `WatchedRootFolder`; `ObserveDiscovered` gains the listing and the four-condition forgetting; `ParentPathKey`; `OrderOneList` folds the served name and orders by it |
| `src/CcDirector.Gateway/History/DiscoveredRepositoryObserver.cs` | reads the listing off the push, passes it through, and puts it in the unchanged-re-push signature |

**No migration, no new endpoint, no new hub method, no change to the route's shape, and NO CLIENT CODE.**
Phase 2's store and phase 3's fold took this without either being restructured.

### Write amplification, answered

The observer skips an identical re-push without touching the database at all (phase 2's memo). The
listing is now **in that signature**, and it has to be: a folder deleted under a watched root changes
nothing about the pushed repositories - the scan never reported it - so a signature covering only the
repositories would skip the very push that was meant to forget it. That is a test of its own
(`ObserveSnapshot_AFolderDisappearsUnderAWatchedRoot_DefeatsTheUnchangedRePushSkip`), and so is the
saving it must not lose: the same listing in a different order is still skipped, because a directory
listing publishes in whatever order the filesystem gave it.

The cost this accepts: a folder appearing or disappearing anywhere under a watched root now defeats the
skip, where before only a repository change did. That is a real change to what the Gateway should know,
and the store's own `SaveChanges` is still gated on something actually moving.

## 4. The mission check (MISSION.md section 7), as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0, `dotnet` at
`~/.dotnet/dotnet`. Run from the worktree root, exactly as section 7 writes the five commands.

**These are the counts for the branch rebased onto `origin/main` at `217b79f63`** - the tip that carries
phase 4's Cockpit work (#3205) and the Smart Director Restart work behind it (#3202, #3204). The whole
check was re-run on that tip; nothing below is carried over from either earlier base.

**The exit code is reported beside every count, because the banner and the exit code can disagree.** A
`dotnet test` run whose test host crashes prints `Passed!` at the BOTTOM while the run was aborted at the
top - it happened on this mission and is recorded in `proofs/an-aborted-run-reports-passed.md`. Every log
below was searched for `aborted`, `crashed`, `test host` and `was canceled`: **zero hits in all five.**

| Command | Exit | Result |
|---|---|---|
| `npm run typecheck` | **0** | **Green.** All four workspaces, zero errors |
| `npm test --workspaces --if-present` | **0** | **Green. 2,161 passed, 0 failed** - client-core 1,459, cc-assistant 106, cockpit 489, mobile 107 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **0** | **Green. 0 failed, 6,705 passed, 8 skipped, 6,713 total** |
| `dotnet test src/CcDirector.Core.Tests` | **0** | **Green. 0 failed, 4,500 passed, 18 skipped, 4,518 total** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **0** | **Green. 0 failed, 646 passed, 0 skipped, 646 total** |

**ZERO FAILURES. No baseline is quoted, on any platform.**

**The Avalonia count was checked rather than assumed, because it looked short.** 646 is below the ~676
this seat was handed as that suite's size, and a short run is a failed run. It is not one: `dotnet test
--list-tests` DISCOVERS 646 on this tip, so every test that exists ran. The suite has grown rather than
shrunk - it stood at 550 earlier in this same mission (`proofs/green-check-dotnet/`) - and this branch
changes nothing under `CcDirector.Avalonia.Tests`, so the ~676 figure is stale, not a missing thirty.

The skipped counts are stated beside the passed counts on purpose, because a skipped test reads exactly
like a passing one in every report we produce. They are the suites' own environment guards and none of
them belongs to this change: the Gateway's are the PostgreSQL-and-Docker gap (see section 7), and
Core's are its Windows-only path and shim tests, the session-spawn races its own notes call unreliable
under load, a real-transcription end-to-end, a pyenv inventory, and a worktree reaper test that needs an
enforced file lock.

**The new tests all RAN, measured by name rather than inferred from a suite total** - **57 passed, 0
skipped** in the unit suites, and the parked suite's seven in section 5. A suite total cannot tell you
this: 57 passing tests and 57 skipped tests produce the same green banner. The 57 were measured in two
filtered runs, both exit 0: **48** in the three wholly new classes (`DirectorRootFoldersTests`,
`TheCatalogueForgetsTests`, `TheNameARepositoryIsServedUnderTests`) and **9** named one by one in the two
existing classes this work added to (`DiscoveredRepositoryObserverTests`,
`TheDirectorPushesItsRegisteredListTests`). An earlier draft of this record said 48 in this line while
section 5 broke down 57; 57 is the measured number and the one that stands.

## 5. The proof - the flow AND the failure cases

### Gap 1, at the Director - `DirectorRootFoldersTests` (14), Gateway.UnitTests

The flow (each root reported with the child folders that exist; the children NOT filtered to git
repositories, which is the whole point); the failure cases (a root that could not be listed is left out
entirely; a readable-and-empty root IS reported, which is the contrast that makes the first mean
something; a root registered twice; a blank root; a blank child; no roots at all). Then the REAL lister
against a real disk, with a real worktree whose `.git` is a file: it reports it, a missing folder
answers **null** rather than an empty list, and a real empty folder answers an empty list rather than
null. And how it rides: on the first row and no other, none when there is no row to carry it, none when
there is nothing to say.

### Gap 1, wired - `TheDirectorPushesItsRegisteredListTests` (+4), Gateway.UnitTests

Separate from the above for the reason that file already gives: those prove the builder works when
something calls it; **these prove something calls it.** A real `RepositoryMonitor` that has really
scanned, real folders on disk, and `ControlApiHost.SnapshotRepositories` itself. The Director pushes
what exists under its roots including the worktree the scan cannot see; a Director whose scan has not
finished pushes no listing, and gains one the moment the scan settles; a host with no monitor pushes
none; and the roots are read fresh on every push. Revert A is what says these are not redundant: with
the wiring lifted out, **70 of 73 unit tests still passed.**

### Gap 1, at the Gateway - `TheCatalogueForgetsTests` (23), Gateway.UnitTests

The flow (a folder worked in and deleted is forgotten; a readable-and-empty root forgets what it held).
**The trap** (a used repository the scan never reports but the root folder still holds is KEPT). The
failure cases: a root that could not be listed, an empty push, a push that is not a full observation, a
push with no listing at all, a repository outside every reported root, another Director on the same
machine, a Director that has gone quiet, a repository deeper than one level, the same folder written two
ways, a repository the snapshot still reports, and the never-opened half still reconciling exactly as it
always did. Then `ParentPathKey` over eight spellings - both separators, either case, a trailing
separator, the POSIX root, a Windows drive root, a UNC share - and one test that proves `ParentPathKey`
and `NormalizePathKey` AGREE about a drive root rather than each being separately plausible.

### Gap 1, at the observer - `DiscoveredRepositoryObserverTests` (+5)

The listing taken off the push; taken off a LATER row, so nothing that re-orders a push can lose it; a
provisional push carrying a listing forgets nothing; the unchanged-re-push skip defeated by a folder
disappearing; and the saving not lost when the same listing arrives in a different order.

### Gap 2 - `TheNameARepositoryIsServedUnderTests` (11), Gateway.UnitTests

The flow, built from the live measurement: one slug across many folders and a blank name both become
folder names. The contrast: a name no other row shares is served exactly as stored. The order
consequence, twice - names deciding a tie, and a row whose slug and folder name sort opposite ways. The
failure cases: a Windows path read on this machine, a path with no folder name in it keeping whatever it
had, a blank name over such a path served blank rather than invented, names differing only in case, two
rows whose folder names are also the same falling back to the path, and a name shared only with ANOTHER
machine's row still being kept. And the same rows read back through the real store, so it is the list a
client is served and not only what the fold returns.

### End to end - `TheCatalogueForgetsTunnelProofTests` (7), the PARKED `Gateway.Tests`

Real folders on a real disk, a real `RepositoryMonitor` that has really scanned, the Director's own
`ControlApiHost.SnapshotRepositories` building what goes on the wire, a real SignalR tunnel to a started
`GatewayHost`, a Director registered at an endpoint nothing listens on, and reads over real HTTP through
the one route a client calls.

| Test | What it proves |
|---|---|
| `AFolderThatWasWorkedInAndThenDeleted_IsForgottenByTheOneRoute` | **The flow.** The defect, closed: two repositories worked in, one deleted, one row served |
| `ALiveWorktreeTheScanCannotSee_IsNotForgotten` | **The trap.** The push carries the clone alone and the listing carries both; the worktree survives |
| `ARepeatedSlugAndABlankName_AreServedAsDistinctFolderNames_InThatOrder` | **Gap 2 end to end**, and that the served order is the name's |
| `ADirectorThatSendsNoRootFolderListing_ForgetsNothing` | **Failure case.** Every Director in the field, until it is upgraded |
| `ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt` | **Failure case.** The unplugged drive. The damage is asserted BEFORE the mechanism - see revert B |
| `ARepositoryUnderNoWatchedRootAtAll_IsNotForgotten` | **Failure case.** Nobody's root, nobody's business |
| `ADirectorThatGoesQuiet_ForgetsNothingWhileItIsAway` | **Failure case.** Removal happens only ON a push - which is why this catalogue is a push and not a pull |

### Watched failing - six of them, two of which are not reverts

**Predictions written and committed FIRST** (`403052e4a`), before any of them ran. See
`predicted-symptoms.md` and `watched-it-fail.md`. Four are reverts of load-bearing lines. **Two are
WRONG RULES substituted for a rule nothing removed** - prefix instead of direct parent, and replacing
every name instead of only a shared one - because this mission has already paid for the lesson that a
revert proves the guard catches THAT defect and not the class its name claims.

Three findings worth carrying out of it:

- **Revert A: with the feature unwired at the Director, 70 of 73 unit tests still passed.** Every store
  test hands the listing in itself, so thirty-nine of them say nothing about whether a Director ever
  sends one.
- **Revert C (the rule the brief recommended): 94 of 97 unit tests and SIX of seven end-to-end tests
  passed while a live worktree was deleted.** A proof set that did not contain a folder the scan cannot
  see would have certified it.
- **Revert D (a wrong rule nothing removed): ONE unit test caught it, and all seven end-to-end tests
  passed.** The end-to-end proof cannot see the difference between "under this root" and "directly under
  this root" at all.

Revert B also improved the proof rather than only confirming it: the end-to-end test first failed on the
listing's SHAPE before it looked at the damage, so the assertions were reordered to put the consequence
first. It now says a row was deleted, not that a listing had the wrong number of entries.

## 6. The parked suite, run in full

`CcDirector.Gateway.Tests` is PARKED: it is not in the mission check and `scripts/test-local.ps1` does
not run it by default. This work's end-to-end proof lives in it, so it was run explicitly rather than
left to ride a gate that never looks at it.

**This work's seven tests pass, with 0 skipped**, and so do phase 2's four, phase 3's four, the registry
work's seven and the two endpoint fold tests: **24 of 24, 0 skipped**, re-run together on the branch's
current tip.

PARKED_FULL_RUN

## 7. What this proof does NOT cover

- **PostgreSQL - nothing here was proved against it.** This machine has no Docker at all, so every
  database assertion in every run above ran on SQLite, and the PostgreSQL-backed proofs that exist
  **did not run - they reported SKIPPED**, which in a report reads exactly like a pass. This work adds
  no migration, no `ORDER BY` and no query - the forgetting is a scan over rows this store already
  materializes, and phase 3's null-ordering trap is untouched because the sort is still in C# over a
  materialized list - **but that is a reasoned claim about the code, not a measurement, and must not be
  read as one.**
- **The live Gateway.** The 90/76/11 measurement above is a READ of the live catalogue; nothing was
  written to it and no hosted Gateway was deployed. The fourteen-row list in section 2 is a
  **simulation** of the new rules over those real rows, run in Python against the real disk - it is the
  strongest statement available without a deploy, and it is not the same as having seen the hosted
  Gateway serve it.
- **A live Director process.** `ControlApiHost.SnapshotRepositories` is driven directly, as the registry
  work's proof also did. The debounce and the periodic tick that carry a push up the tunnel
  (`WireRepositoryPush`, `GatewayStreamClient.RePushTick`) are unchanged and are not exercised, so the
  claim that a deleted folder reaches the Gateway within about ten seconds is read from that timer's
  code, not measured.
- **Real git.** The monitor in the end-to-end proof is real - its scan, its publishes, its
  reconciliation and its completed-scan fact are its own - but its enumeration and git compute are
  injected. The worktrees in these tests are real folders with a real `.git` FILE, which is the property
  that matters here, but no `git worktree add` was run.
- **Anything a screen shows.** No client code changed and nothing was seen running on a phone, a Cockpit
  or a desktop dialog. What phase 6 will find on the Director's dialog is phase 6's to prove - and phase
  6 should know that the served ORDER moved, because the name moved.
- **Two Directors on one machine, end to end.** That invariant is proved at the store, over the new
  root-scoped rule as well as phase 2's owner-scoped one, but no test drives two live tunnels with two
  different root listings.
- **Volume.** A machine with a thousand folders under its roots sends a listing of a thousand paths on
  every push that is not skipped. The listing is one per push rather than one per row, which is the
  reason it is shaped that way, but no test measures the size or the time, exactly as phases 3 and the
  registry work did not measure the response they grew.
- **A root whose last repository is deleted.** The rule leans to keep in one place it could have
  pruned: if a root's only remaining folder goes, the push carrying that is an EMPTY push, and an empty
  push reconciles nothing. The row survives until something else appears under that root. That is
  deliberate - a destructive operation acts only on what it can positively prove is disposable - and it
  is not measured against any real machine.
