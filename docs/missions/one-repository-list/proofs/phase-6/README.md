# Phase 6 - the Director reads the same list

Mission: One repository list, held on the Gateway. Phase 6 of section 6, and the last.

**The phase row:** *the dialog reads the Gateway list, falling back to the local scan with a line on
screen when the Gateway cannot be reached. Lands last, because it is the screen that works today and has
the most to lose.*

**The goal it serves, goal 5 of section 3:** *the Director's dialog still opens and still lists
repositories when the Gateway cannot be reached, and says on screen that it is showing its local
fallback rather than quietly showing a different order.* And, with phases 1 to 5 beneath it, goal 1 and
goal 2 on this screen as well: a session started from the Cockpit or the phone now moves a repository up
the Director's own list, and a repository under a registered root folder that nobody has opened appears
on it.

The owner chose this shape himself when asked directly - Gateway first, the local scan as the fallback,
and saying so on screen - over Gateway-only and over leaving the Director alone.

---

## 1. The ground, checked before the screen was touched

The mission document has been wrong twice about what exists, so nothing below was taken on trust.
Everything was read in `origin/main` (this worktree, zero commits ahead and zero behind at the start),
and the two live checks were made against the hosted Gateway.

| The claim | Verdict | What was actually found |
|---|---|---|
| The dialog builds its list as a union of the registry and the root-folder scan | **True** | `NewSessionDialog.BuildRepositoryList` - `_registry.Repositories`, then `RepositoryMonitor.Snapshot()`, de-duplicated by path |
| `GET /directors/{id}/known-repositories` serves the one ordered list | **True** | `GatewayEndpoints.cs` → `KnownRepositoryStore.ReadForMachine` → `OrderOneList` |
| **A Director's own token may call that route** | **True, and verified live** | Called against the hosted Gateway with this machine's Director token: **200**. Worth recording: a SESSION key is refused on it (`session_key_out_of_scope`), so the route is the Director's to read and not an agent's |
| The catalogue now holds everything the union holds, after #3203 | **True for MEMBERSHIP** | Both halves ride the one push: the scan (phase 2) and the registry (#3203). The catalogue is a superset |

**So the membership question the brief asked me to verify rather than trust is answered: nothing the
union holds is missing from the catalogue.** What I found instead are two gaps of a different kind -
not missing rows, but rows the catalogue cannot keep true. They are reported in section 2, they are
GATEWAY-side, and they are deliberately not fixed here.

---

## 2. Two findings handed up, not swept and not fixed on one screen

Both were measured on a real machine against the live catalogue for it. Both affect the Cockpit and the
phone exactly as much as the Director - the phone has read this route since phase 5 - so fixing either
one inside the desktop dialog would make one screen disagree with the other two, which is the defect
this mission exists to end. They belong in the Gateway's one fold, and that is a mission-level decision,
not phase 6's to take alone. **They are reported to the Delivery Lead with a recommendation.**

### Finding 1 - the catalogue never forgets a folder that no longer exists

On the machine measured: the dialog's union produces **3 rows**; the catalogue returns **89**, of which
**76 are paths that no longer exist on disk** - worktrees that were created and deleted. Used rows are
never removed: `ObserveDiscovered`'s reconciliation is scoped to never-opened rows by design (its
invariant 2), and nothing else prunes.

Pointing a screen at the catalogue therefore turns a three-row picker into an eighty-nine-row one whose
bulk is dead folders. That is the single biggest thing this phase had to lose, and it is not a defect
this phase introduced or can honestly close: only the machine knows whether a folder is still there, and
a client-side existence filter would fork the one list three ways.

**Recommended shape, for the Delivery Lead to rule on:** the Director's push already IS "what exists
under my registered roots". If it also carried the roots, the Gateway could remove a used row whose
folder sits under a root that Director covers and no longer reports - leaving alone any repository
outside every root, which no Director can speak for.

**Ruled on 20 September 2026: ACCEPTED, and this shape taken.** The Delivery Lead has seated a separate
Developer on it as Gateway work, and ruled that it blocks phase 6's MERGE rather than its build - *three
rows becoming eighty-nine of which seventy-six are dead folders is not a smaller version of success, it
is the mission failing at its own goal.* He also backed the refusal to filter in the client explicitly:
an existence check in the Director would make the three screens disagree again. **And he recorded the
consequence for work already merged: phase 5 put the phone on this same route, so the phone inherits the
same rows - merged but not deployed, so fixable before anyone sees it.**

### Finding 2 - the name on a used row is the session's, and the Director can never correct it

A used row's `Name` is `session.RepoName`, written by `SessionHistoryRecorder`. On the same 89 rows:
**17 are blank**, and every one of the rest is one of two repository slugs, repeated across dozens of
distinct worktree paths. The Director's push cannot fix them - `ObserveDiscovered`'s invariant 2 makes a
used row untouchable, name included - so a row whose first observation was a session start with no name
keeps a blank name for ever.

The dialog's Name column shows the folder name today. **This phase renders the Gateway's name verbatim
and deliberately does not invent a folder name to cover the gap** (there is a test pinning that), because
a name invented on one screen is one screen disagreeing with the other two about what a repository is
called. The fix belongs in the Gateway's fold - and it is not free, because the served order's tiebreak
is `ThenBy(Name)`, so correcting the names moves the order for the phone and the Cockpit too. That is
why it is handed up rather than taken here.

**Ruled on 20 September 2026: ACCEPTED.** The Gateway serves the folder name from the path when the
stored name is blank or is a slug that does not distinguish the row, in the one fold and in no client.
The Delivery Lead accepted the consequence as *correct rather than regrettable - all three screens should
sort by the name a person actually sees* - and it goes to the same Gateway work as finding 1, not into
phase 6.

### Not a finding

One repository present in the local scan was absent from the live catalogue. Explained: the HOSTED
Gateway predates phase 2, so it has no discovered half deployed yet. Nothing to fix in code - but,
in the Delivery Lead's words, **it means the mission is unproven against the DEPLOYED Gateway until it is
deployed, and deployment is the owner's decision, not ours.**

---

## 3. What was built

### The Director reads the one list, on its own credential

- `GatewayClient.GetKnownRepositoriesAsync` - `GET /directors/{id}/known-repositories`, on this
  Director's own Gateway token, with the id escaped. It hands the rows on **in the order they arrived**
  and sorts, filters and re-names nothing.
- `ControlApiHost.GetKnownRepositoriesAsync` - the same thin delegation the turn brief and the fleet
  roster already use, resolving this Director's id.
- `KnownRepositoryListResult` - four outcomes, because the dialog is allowed to fall back for some and
  must not for others. `Served`, `NotConfigured`, `Unreachable`, `Refused`.

### The fallback rule, as the Delivery Lead adopted it for this phase

Written here in the terms he ruled, because a fallback is the one thing on this screen that law 1 - no
fallback programming - would otherwise forbid, and what makes it honest rather than a mask is that it
SAYS what happened:

1. **A 200 is the list WHATEVER it contains, including empty. Never fall back on a list you dislike.**
2. **No answer at all - not configured, cannot connect, timed out - falls back.**
3. **An error status falls back AND quotes the Gateway's own words on screen, so the defect is displayed
   rather than hidden.**

The third clause is the one that matters most. The rest of this client collapses every failure to null;
this one cannot, because "the Gateway says this machine has no repositories", "the Gateway could not be
asked" and "the Gateway refused" must reach the screen as three different things. A client that treated
an empty list as a failure would hand a screen the licence to show a different list from the Cockpit and
the phone.

### The screen renders the order and never computes one

`NewSessionRepositoryList` holds the rules as pure functions, so the window holds none:

- `FromGateway` - the served rows as the dialog's row model, in the served order. The Gateway's
  `NeverOpened` verdict rides across as `IsDiscovered` rather than being re-read off the date, and every
  row is marked as the catalogue's.
- `Order` - the heading the user pressed. **While the list is the Gateway's, the Last Used heading hands
  the served order back untouched, and its ascending press is that same order reversed** - the Gateway's
  ruling read bottom-up. There is no comparison on a last-used date anywhere in the desktop for a served
  list. Name and Path still sort, because a person pressing a heading is asking for a view, not deciding
  what the data means; the served order is held separately, so pressing Name and then Last Used comes
  back to the Gateway's order intact.
- `FallbackNotice` - one terminated sentence per fallback. Phase 4 shipped *"Director not connected Try
  again."* by running a phrase into the advice after it; any words the Gateway supplies are terminated
  before the sentence that follows, and there is a test over every case.

### The screen says where its list came from

A notice sits between the search box and the column headings - amber on the "behind" badge background
from `docs/VisualStyle.md`, which is that guide's colour for *something is not as it should be*, not an
error red.

| State | What the screen shows |
|---|---|
| waiting for the Gateway | *Checking the Gateway for the one repository list...* |
| the Gateway served the list | **nothing** - the ordinary state wears no banner |
| no Gateway connected | *No Gateway is connected. This is this machine's own list, and its order may differ from the Cockpit and the phone.* |
| the Gateway could not be reached | *The Gateway could not be reached.* + the same second sentence |
| the Gateway answered and refused | *The Gateway could not give the repository list: <the Gateway's own words>.* + the same second sentence |

**The machine's own list goes up first, synchronously, and the dialog never waits on a network to draw
(Critical Rule 1).** The Gateway's list replaces it when it arrives, keeping the user's search text and
the row they had already chosen. While it has not arrived the screen says so, rather than letting the
user believe they are looking at the one list everybody else sees.

### Four consequences worth naming rather than discovering later

1. **Remove is not offered on a row that came off the catalogue.** It only ever removed from this
   Director's own registry, and a used row is never removed from the catalogue by anything a Director
   sends - so the row would be back on the next read. That is the same rule a discovered row has always
   paid (`RepositoryConfig.CanRemove`), for the same reason: a button that undoes itself reads as broken.
   On the fallback, Remove works exactly as it always did. **Removing a repository from the one catalogue
   is not something the product can do yet; this phase does not pretend otherwise, and it is the third
   thing handed up.**
2. **Browse does not rebuild the list while the Gateway's list is on screen.** Rebuilding would swap one
   list for another with no notice, which is the failure the owner named. The browsed folder is in the
   path box and Start works on it immediately; it joins the one list on the Director's next push.
3. **Closing the dialog stops the ask.** A Gateway that is not answering takes as long as its timeout,
   and the user may close the window well before that; the ask carries a token cancelled on close, a late
   answer is dropped rather than applied to a window that has gone, and a cancelled ask is not reported
   to the user as a Gateway that refused. Guarded, and watched failing.
4. **The folder-name line inside the rewritten builder was fixed**, and it is disclosed rather than
   swept: `Path.GetFileName` honours only the separator of the host it runs on, so a Windows path read on
   macOS put the whole path in the Name column. It is `RepositoryPaths.FolderName` now, through
   `NameForScannedRepository`, with a theory over both separators. This is the mission's recurring defect,
   sighted a sixth time, and it is fixed here because it is one line inside the function this phase
   rewrote - not as a sweep of the seventeen lines catalogued and deferred in
   `proofs/green-check-dotnet/`.

### Files changed

| File | Why |
|---|---|
| `src/CcDirector.ControlApi/KnownRepositoryListResult.cs` | **new** - the four outcomes and the rows |
| `src/CcDirector.ControlApi/GatewayClient.cs` | the read, and the Gateway's own words off a refusal |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | the delegation, resolving this Director's id |
| `src/CcDirector.Avalonia/NewSessionRepositoryList.cs` | **new** - the rules, as pure functions |
| `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs` | asks, renders, falls back, and holds the served order |
| `src/CcDirector.Avalonia/NewSessionDialog.axaml` | the notice |
| `src/CcDirector.Core/Configuration/RepositoryConfig.cs` | `IsFromTheGatewayList`, which switches Remove off |

No Gateway change, no route change, no migration, and no change to the Cockpit or the phone.

---

## 4. The proof

**The mission check at zero failures, the eight predicted reverts, and the pictures** are in
`watched-it-fail.md`, with the counts and the symptoms. The predictions are in `predicted-symptoms.md`,
committed on its own before any of it was run.

### The pictures - the proof section 7 says must not be skipped

Taken from the REAL dialog, driven headless with real drawing, by the committed tests themselves
(`CC_PHASE6_SHOOT=<folder>` copies them out; the tests always take them and always assert they are a
picture, so nothing is skipped when the variable is absent).

| Picture | What it shows |
|---|---|
| `screenshots/the-gateways-list-on-the-director.png` | The Gateway's list in the Gateway's order: `zephyr-tools`, then a never-opened `beacon`, then `atlas-reporting`, then `cinder`. **An order no client sort produces** - a never-opened row sits second, between two used ones. No notice. No Remove buttons |
| `screenshots/the-gateway-cannot-be-reached.png` | **The one the mission singles out.** The Gateway unreachable: the dialog still lists repositories - the machine's own two - and says in amber *The Gateway could not be reached. This is this machine's own list, and its order may differ from the Cockpit and the phone.* Remove is offered again, because this list IS the registry it removes from |
| `screenshots/no-gateway-is-connected.png` | A Director with no Gateway at all - a different sentence, because it is a different problem |
| `screenshots/the-gateway-refused.png` | A Gateway that answered and refused, showing the Gateway's own words: *The Gateway could not give the repository list: The Director has not reported a machine name.* |

**The pictures caught a defect in the proof itself**, and it is written up in `watched-it-fail.md`
section 3 rather than quietly fixed: the first set were pictures of the wrong moment, and the tests they
came from passed. What I first believed the cause was turned out to be wrong, that is recorded too, and
the guard that does catch it was watched failing.

### What the proof does NOT cover

- `AskTheDirectorsGatewayAsync`, the production resolution from the running Director - it needs the real
  desktop application object. Both sides of it are covered, and the route was called by hand against the
  hosted Gateway with this machine's Director token (200).
- **The deployed Gateway.** It predates phase 2, so nothing in this mission's Gateway work is live yet.
  Deploying is the owner's decision and no part of this mission.
- The two findings in section 2. They are measured, reported and deliberately not fixed here.
