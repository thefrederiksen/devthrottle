# Phase 5 - the phone reads the same list

Mission: One repository list, held on the Gateway. Phase 5 of section 6.

**The phase row:** *the phone's repository step takes the one ordered list. No other change to that
screen.*

**The goal it serves, goal 4 of section 3:** *the phone's New Session flow is unchanged in shape and
shows the corrected, ordered list.* And goal 1, which cannot be true without it: *all three New Session
screens, pointed at the same machine, list the same repositories in the same order.*

The owner's own words, recorded in section 4: *"Leave the phone the way it is. The phone is actually
working pretty well."* and *"Make sure that it also gets the new synchronised last used list of
repository."* Both are honoured below: the shape of the screen is the same, and the list on it is the
Gateway's.

---

## 1. What I checked before I built anything, and one thing the mission document does not say

The mission document has been wrong twice about the ground and both times the seat that checked saved
the work. So every claim this phase rests on was read in `origin/main` first.

| The claim | Verdict | What the code says |
|---|---|---|
| `client.ts getKnownRepositories` re-sorts the list the Gateway just ordered | **True when checked** | `list.sort((left, right) => right.lastUsed.localeCompare(left.lastUsed));`. Fixed since, on its own, by the shared-reader pull request - section 4. |
| `NewSession.tsx mergeRepositories` re-sorts with a different tiebreak and de-duplicates paths in JavaScript | **True** | last-used descending, then `repositoryLabel` under `localeCompare`; `repositoryKey` decides Windows-ness from the path's own shape |
| `GET /directors/{id}/known-repositories` serves the union already ordered | **True** | `GatewayEndpoints.cs` → `KnownRepositoryStore.ReadForMachine` → `OrderOneList`; phase 3 proved it |
| Nothing else in the product reads `getKnownRepositories` | **True** | the whole repository: `client.ts`, its test, and `apps/mobile/src/pages/NewSession.tsx`. No Cockpit caller. |

### The thing that is NOT in the mission document, and that changes what this phase had to be

**The phone's repository step read TWO routes, not one**, and the reviewer's note named only what each
of them did with its own list, not that there were two.

- `GET /directors/{id}/known-repositories` - the Gateway's one ordered list.
- `GET /directors/{id}/repos` - the **Director's own registry**, fetched down the tunnel
  (`CatalogReadExecutor.ReposList` over `RepositoryRegistry`).

They were merged on the device by `mergeRepositories`. And the split of work between them mattered:

```
no search typed  →  mergeRepositories(recentRepositories).slice(0, 5)   ← the Director's registry ALONE
search typed     →  mergeRepositories(recent, known).filter(...)        ← the two merged
```

**So with no search typed - the step's default state, and the only state most people ever see - the
phone showed the Director's answer and the Cockpit showed the Gateway's, for one machine.** That is the
complaint the mission opens with, alive on the phone, and goal 1 cannot be true while it is.

It is not a theoretical difference. The two records disagree in three ways at once, all of them visible
in the before-and-after screenshots in section 5:

1. The registry holds only repositories somebody added to it by hand; the catalogue holds every
   repository the Gateway has seen a session start in, on any surface.
2. The registry holds nothing found under a root folder, so a repository nobody has opened could never
   appear on the phone's default list however much the Gateway knew about it.
3. `/repos` needs a live tunnel and answers 502 without one; the catalogue is served from the Gateway's
   own storage, which is exactly the moment the other screens still need it.

## 2. The decision this phase had to make, and why

**The repository step reads the ONE route. `getRepos` leaves that screen.** This was the judgement
worth thinking about rather than deciding by reflex, so the reasoning is written out.

**Why keeping both and only fixing the sorts is not enough.** A merge of two sources cannot avoid
ruling. It has to decide which of two records of one repository wins, in what order the result comes
out, and which two paths are the same repository. Every one of those is a verdict, and Critical Rule 7
says verdicts are the Gateway's. Worse, the default view would still have been the registry's list, so
the owner would have opened the phone and seen the old answer with the new code shipped underneath it.

**Why it is not a change to the shape of the screen.** The step is the same step: same heading, same
search box with the same label and placeholder, same five rows with the same name-over-path layout,
same "Enter a path manually" panel, same review step, same footer. **No style sheet was touched and no
element was added or removed from the layout.** What changed is where the rows come from. The
before-and-after screenshots are the argument, and they are in section 5.

**Two sentences did change, because they had stopped being true**, and they are named here rather than
left for a reader to find:

| Before | After | Why |
|---|---|---|
| "Showing up to five most recently used repositories." | "Showing the top five of this machine's repository list, most recently used first." | The rows are now the top of the one list. When a machine has two repositories that have been used and ten nobody has opened, the first five are not five recently used ones, and the old sentence would have said they were. |
| "Repository history could not be loaded: …" and "Recent repositories could not be loaded: …" | one line: "The repository list could not be loaded: …" | There is one list, so there is one thing that can fail. "History" was the name it had when it was the search source beside a recent list; it is the list now. |

**And one behaviour was corrected while the failure case was being photographed.** With the load
failed, the screen showed the error banner AND "No repositories are known on this machine yet." AND
"Showing the top five…". Three sentences, two of which the screen did not know to be true. Not loaded is
not the same fact as nothing there, and a client that says the second when it only knows the first is
ruling on a state the Gateway never described - the same shape of defect as the Voice screen offering a
button that could never work. Both the note and the empty-machine sentence are now suppressed while the
list is in error. See the failure screenshot in section 5.

## 3. `repositoryKey` - the question the brief asked me to decide deliberately

**The phone does not de-duplicate paths any more, and the rule is deleted rather than moved.**

`repositoryKey` existed for one reason: two sources could hand the screen the same repository twice.
With one source that cannot happen - `KnownRepositoryStore.ReadForMachine` serves one entry per
repository for the whole machine, keyed by its own `NormalizePathKey`, and phase 3 pinned that with
`OrderOneList_OneRepositoryWrittenTwoWays_IsServedOnceWithItsTime`.

The only thing left that needed a key was React's list key, and **the path itself is that key**: one
row per path, stable across renders, which is all React asks of it.

**Why deleting it is better than leaving it.** `repositoryKey` decided whether a path was a Windows one
by looking at its shape (`/^[a-z]:\//i`, or a leading `//`) and then lower-cased it. That is the rule
this mission has now met five times in five places, and the `green-check-dotnet` proof lists thirteen
more copies of its .NET twin. Every copy is a place the product can decide differently from the one
place that is supposed to decide. Leaving a copy on the phone "because it is not obviously broken"
would have left the mission one short of what it exists to do - and it would have kept a second opinion
alive on exactly the screen this phase is about.

**What it costs, said plainly:** if the Gateway ever served two rows for one repository, the phone would
now show both instead of folding them. That is the correct behaviour under Critical Rule 7 - the screen
shows what it was sent - and it makes a Gateway defect visible instead of hiding it behind a client
repair. It is also pinned in the other direction by *"keeps case-distinct Unix repository paths as
separate choices"*: two paths that differ only in case are two repositories on a Unix machine, the
Gateway says so, and the phone no longer has a rule of its own that could fold them together.

## 4. What changed

| File | Change |
|---|---|
| `apps/mobile/src/pages/NewSession.tsx` | `mergeRepositories` and `repositoryKey` are **deleted**. The step reads one route; `allRepositories` is the served array; the default view is its first five and a search is a filter of it - both operations preserve order. The second source, its state, its error and its banner are gone. The note and the empty-machine sentence are suppressed while the list is in error. |
| `apps/mobile/src/pages/NewSession.test.tsx` | 11 tests → 17. The new ones are in section 5. |

**That is the whole change.** No Gateway code, no route, no contract, no style sheet, no Director
code, and - after the coordination described in section 7 - no shared reader and no .NET. Phase 5 is a
client that stopped having opinions, and it is a deletion.

### Two things this branch built and then gave up, because other seats owned them

Both were found by doing the work, both were right to find, and neither belongs in a phone phase. They
are recorded because a reader comparing this document to the mission's messages should not have to
guess why they are missing.

1. **The re-sort in `client-core` `getKnownRepositories`** - item 1 of this phase's brief. It was built
   here and deleted here, and then the Delivery Lead ruled that the shared reader lands ONCE, on its
   own, ahead of both this phase and phase 4, because phase 4 needs the same file and cannot render the
   Gateway's verdict without `neverOpened` on the wire. It landed as `828a182b3`. **This branch took
   that version wholesale on its rebase** - it is strictly better than what was built here, because it
   also carries the verdict through. The shape it settled on, `KnownRepoInfo extends RepoInfo` rather
   than a required field on `RepoInfo`, is the shape this screen depends on and it compiles here
   untouched: the step types its rows as `RepoInfo`, which a `KnownRepoInfo` is.
2. **The folder-name defect in `SmartShutdownSessionReader`** - section 7. Found by running the mission
   check, fixed here, and then dropped on instruction: three seats found the same red independently and
   a dedicated one owns it. It landed as `f20bbd33b` and reached this branch through main.

## 5. The behavioural proof - the flow AND the failure cases

### On the screen, in a real browser

The phone application was built and served by its own development server, against a stand-in Gateway
that answers the four routes the flow reads, and driven in Google Chrome at a 390×844 phone viewport
through the DevTools protocol. The driver walks the real flow - pick the Director, pick the agent, land
on the repository step - and prints the rows it finds, so the pictures are not the only evidence.

The stand-in Gateway serves ONE machine whose list is deliberately a mix:

| | `known-repositories` (the one list) | `repos` (the Director's registry) |
|---|---|---|
| devthrottle | used, 20 Sept 05:40 | used, 20 Sept 05:40 |
| devthrottle-internal | used, 19 Sept 18:05 | **absent** |
| mindzie-studio | used, 12 Sept 11:20 | used, 12 Sept 11:20 |
| atlas-reporting | **never opened** | absent |
| zephyr-tools | **never opened** | absent |
| old-experiment | **absent** | used, 11 Sept 08:00 |

| Screenshot | What it shows |
|---|---|
| `screenshots/before-two-lists-merged-on-the-phone.png` | **`origin/main`.** Three rows: `devthrottle`, `mindzie-studio`, `old-experiment`. The second most recently used repository on the machine is missing, a repository the Gateway has never heard of is on screen, and neither never-opened repository appears at all. Both routes were called - the stand-in Gateway logged `GET /directors/north/repos` and `GET /directors/north/known-repositories`. |
| `screenshots/after-the-one-list.png` | **This phase.** Five rows in the Gateway's order: `devthrottle`, `devthrottle-internal`, `mindzie-studio`, then `atlas-reporting` and `zephyr-tools` at the bottom. **Only `GET /directors/north/known-repositories` was called** - `/repos` does not appear in the stand-in Gateway's log at all. Same heading, same search box, same rows, same manual path, same footer. |
| `screenshots/after-searched-the-whole-list.png` | A search keeps the order rather than re-deciding it: four matches, used ones first, `atlas-reporting` and `zephyr-tools` beneath them. This is goal 2 arriving on the phone - a repository nobody has opened is reachable and it is at the bottom. |
| `screenshots/failure-the-one-list-cannot-be-loaded.png` | **The failure case.** The route answers 503. One honest sentence, a retry, and the manual path entry that has always been there. No "top five" note, no "no repositories are known on this machine", and nothing rescued from a second list - because there is not one and there is not meant to be. |

**How to run it again**, from the worktree root, which is the point of committing the two scripts:

```
node docs/missions/one-repository-list/proofs/phase-5/stub-gateway.mjs 5599 &
cd apps/mobile && MOBILE_PROXY_TARGET=http://localhost:5599 npx vite --port 5598 --strictPort &
node docs/missions/one-repository-list/proofs/phase-5/shoot.mjs http://localhost:5598 out.png
```

Add `FAIL_KNOWN=1` in front of the stand-in Gateway for the failure screen, and a search term as a
fourth argument to the driver for the searched view. The driver drives Chrome over the DevTools
protocol with Node's own WebSocket, because no browser automation package is installed on this machine
and installing one to take a picture would be a change to the machine this proof has no business making.

### The phone's own tests - `apps/mobile/src/pages/NewSession.test.tsx` (17)

| Test | What it proves |
|---|---|
| `shows the Gateway's one list with the never-opened repositories at the bottom` | **The case the brief asked for, and what the owner sees.** A machine with three repositories worked in and two nobody has opened: the used three in recency order, the never-opened two at the bottom, in the Gateway's order. |
| `renders the Gateway's order verbatim, even an order no client sort would produce` | **The guard.** The served order is deliberately one no sort produces - used oldest first, the never-opened pair out of name order. Any re-sort anywhere between the wire and the row fails here. The test above cannot catch one on its own, and `watched-it-fail.md` proves that rather than assuming it. |
| `keeps the Gateway's order through a search` | Searching filters; it does not re-order. |
| `reads one route for repositories and never the Director's own registry` | `getRepos` is never called, and the registry-only repository the mock serves never reaches the screen. |
| `says the repository list could not be loaded and still accepts a typed path` | **Failure case.** One sentence, a retry, no "no repositories are known" beside it, no "top five" note, and the manual path still works. |
| `says so plainly when the machine has no repositories at all` | **Failure case.** An empty list is a sentence, never a blank panel. |
| `distinguishes a search that matches nothing from a machine with nothing on it` | **Failure case.** Two different facts, two different sentences. |
| `keeps the loading state honest while the repository list is in flight` | In flight says "Loading repositories…" and never "no repositories are known". |
| `keeps case-distinct Unix repository paths as separate choices` | Two paths differing only in case are two rows - section 3. |
| `searches beyond the top five repositories and creates only after explicit review` | The whole flow end to end, unchanged: default five, search, review, create. |
| `caps broad search rendering, reports the full match count, and keeps every result reachable` | 50 of 60, and the 60th is still reachable by typing more. |
| the six that were already there | the Director and agent steps, the late-response guard, the empty agent list, the agent error and retry, the double-tap guard, the manual path, and the retry that preserves typed input. |

### The reader beneath it - `packages/client-core/src/api/newSession.test.ts`, **not this branch's**

The reader's own guard against the re-sort coming back is *"serves the Gateway's order untouched and
never re-sorts on lastUsed"*, and it landed with `828a182b3`. It is named here because this screen
depends on it and a reader of this proof should know where that half is proved - not because this
branch wrote it. This branch changes no file under `packages/`.

### Watched failing

Five reverts: the client re-sort, the whole pre-phase-5 screen, a surgical repeat of that one when it
proved too broad to be evidence, the default five taken from the wrong end, and the folder-name fix.
The predictions were committed on their own before any of them ran. **Three of the five - B, B2 and C -
are this branch's**; the other two attack code that has since landed separately, and `watched-it-fail.md`
says so on each. Full record in `predicted-symptoms.md` and `watched-it-fail.md`.

The one worth carrying forward: **with the pre-phase-5 screen restored, the phone's default view showed
one row, `Registry only` - a repository the Gateway's catalogue does not hold - and none of the
Gateway's five.** That is the defect, photographed and asserted.

## 6. The mission check, section 7, as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0, `dotnet` 10.0.301.
Run from the worktree root, exactly as section 7 writes them, on the committed code after every revert
was restored (`git diff HEAD` empty).

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces. |
| `npm test --workspaces --if-present` | **Green. 2,126 passed, 0 failed** - client-core 1,456, cc-assistant 106, cockpit 457, mobile 107. |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **Green. 0 failed, 6,547 passed, 8 skipped.** |
| `dotnet test src/CcDirector.Core.Tests` | **Green. 0 failed, 4,485 passed, 18 skipped.** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **Green. 0 failed, 646 passed, 0 skipped.** |

**Zero failures. Not "zero new failures", and no baseline is quoted anywhere in this document.**

These are the counts on the FINAL base, `f20bbd33b`, after the branch was rebased twice - `main` moved
four times while this phase was built, and an earlier run's numbers are a run against a base that no
longer exists. Of the six phone tests added here, all six are this branch's; every other movement from
phase 3's counts arrived on `main` from other work.

## 7. A red test on `main` that was not mine - found here, fixed here, and handed over

`CcDirector.Avalonia.Tests` was **1 failed, 623 passed** when this phase's check was first run, and the
failure was not this phase's: my diff at that moment contained four TypeScript files and no .NET at all.

```
SmartShutdownSessionReaderTests.Read_NamedAndUnnamedSessions_UseTheNameTheRailShows
Expected: "devthrottle"
Actual:   "D:\\ReposFred\\devthrottle"
```

It arrived with `642482c46` (Smart Director Restart phase 2). `SmartShutdownSessionReader.DisplayNameOf`
read the folder name with `Path.GetFileName`, which honours only the separator of the machine it is
RUNNING on - so a Windows repository path read on a Mac or on Linux contains no separator it recognises
and the whole path comes back. **That is this mission's recurring defect, and it is a fifth sighting**;
the `green-check-dotnet` proof in this folder documents the family and lists thirteen more call sites.

It was fixed here, with the shared rule the `green-check-dotnet` proof already prescribed
(`RepositoryPaths.FolderName`), and a test was added pinning both separators. Then it was **dropped from
this branch on the Delivery Lead's instruction**: a dedicated seat already owned it, opened minutes
earlier because phase 4's Tech Lead had reported the same red from its side. Three seats found it
independently, which says something about how invisible it was. It landed as `f20bbd33b` and reached
this branch through `main`, so the fix is in the counts in section 6 and none of it is in the diff.

**Why it is still written down here.** Revert D in `watched-it-fail.md` was run before the hand-over and
its observation is real evidence about that defect - the symptom, on this machine, with the fix undone.
It is recorded so the seat that owns the fix has it, rather than deleted because the code moved. What
this branch claims is the finding and the measurement, not the fix.

**What nobody did:** the other thirteen call sites. They are a sweep across two applications with no
failing test pointing at them, phases 4 and 6 rewrite several of the files, and the `green-check-dotnet`
proof already reports them to the mission to decide.

## 8. What this proof does NOT cover

- **No real phone, and no real Gateway.** The screenshots are the real phone application in a real
  browser at a phone viewport, driven against a stand-in Gateway on localhost. Nothing ran on iOS or
  Android, and nothing talked to `gateway.devthrottle.com`. What the pictures prove is that this code,
  given the shape the Gateway serves, draws what they show.
- **Nothing about the Gateway's order.** Phase 3 owns and proved it. Everything here assumes the served
  array is right and tests only that the phone does not touch it. If `OrderOneList` is wrong, the phone
  now shows that wrongness faithfully - which is the design.
- **THE CATALOGUE'S COMPLETENESS IS NOW THE PHONE'S COMPLETENESS.** This is the sentence that must not
  be skipped, because a green phase 5 otherwise reads as "the phone shows everything". It does not. It
  shows exactly what the Gateway's catalogue holds - no more, and no second source to make up the
  difference.

  And the catalogue is not complete today. The Director pushes its root-folder scan to the Gateway
  (`ControlApiHost.SnapshotRepositories` over `RepositoryMonitor.Snapshot`, verified at line 1274) and
  it **never pushes its registry**. So a repository that was added to a Director by hand, has not been
  opened since the Gateway started recording, and is not under any registered root folder exists ONLY
  in `/repos` - and after this phase it no longer reaches the phone. The manual path entry still starts
  a session in it.

  **It is not patched around here, deliberately.** A fallback to a second list would be the exact defect
  this phase deleted, and law 1 forbids fixing something by adding something. It was raised to the
  Delivery Lead before this code was written rather than discovered at the report; that seat verified it
  independently and is closing it as its own piece of work - the Director's registry reaching the
  Gateway too - **ahead of phase 6, because phase 6 makes the Director's own dialog read this same list
  and would otherwise regress the one screen that already works.** Until that lands, the case above is
  missing from the phone.
- **No test here can tell an incomplete catalogue from a complete one.** Every test serves the catalogue
  it wants. That is the limit above, said as a limit on the evidence rather than on the product.
- **Volume.** Phase 3 noted that no test says what a machine with a thousand repositories does to this
  response. The phone renders at most five without a search and at most fifty with one, so the screen is
  bounded - but the response is not, and nothing here measures it.
- **The Cockpit and the Director's dialog.** Both still read their own lists. Phases 4 and 6, exactly as
  the mission sequences them.
- **The phone's approval-prompt control.** Ruled out by the owner, not forgotten, and not added.
