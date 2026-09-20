# Phase 4 - the Cockpit's New Session tab

Mission: One repository list, held on the Gateway. Phase 4 of section 6.

**The phase row:** *Redrawn to the Director's layout: search box, Name / Path / Last Used table with
sortable headings, per-row remove, Browse, and the Director's wording throughout. Plus the machine row.*

**The goals it serves.** Goal 3 of section 3 - *the Cockpit's New Session tab is the Director's
layout, plus a machine row* - and, from section 5's out-of-scope list, *the Cockpit defect where
clicking a repository row starts the session immediately instead of selecting it*, which the mission
explicitly routed here rather than fixing twice.

**Two of the controls named in that row are NOT built, and both are decisions taken by the Tech Lead
and the Delivery Lead before any code was written.** They are section 2 below. That row was written
when the mission was chartered; what the ground turned out to say is recorded here.

**This is a QA report, not a success run.** Section 6 is the mission check. Section 7 is the flow AND
the failure cases, with screenshots. Section 8 is five reverts, each predicted in writing first.
Section 9 says what none of it covers.

---

## 1. What the ground said before anything was drawn

The mission document has been wrong about the ground more than once, so every claim this phase rests
on was read in the code first. Three of the four controls the phase row names could not be copied
across as written, and all three fail for one underlying reason:

> **The Director acts on ITS OWN machine's stores. The Cockpit acts on a Gateway that holds a
> different set of them.**

| The phase row says | What the code says | What was built |
|---|---|---|
| "search box" | `RepoSearchBox` + `ApplyRepoFilter` filter on name OR path, case-insensitive | The same, with a VISIBLE placeholder - a tooltip is not discoverable on the web |
| "Name / Path / Last Used table with sortable headings" | `RepoHeader_Click` / `ApplyRepoSort`, Last Used active and descending by default | The same table and the same heading behaviour - **but the order is never recomputed**, section 3 |
| "per-row remove" | `DELETE /directors/{id}/repos` removes a row from the DIRECTOR'S REGISTRY; this screen reads the GATEWAY CATALOGUE, and `KnownRepositoryStore` has no delete at all | **Not built.** Section 2 |
| "Browse" | `BtnBrowse_Click` opens a folder picker on the machine the Director runs on | **Not built.** A path box and an Add button stand in its place. Section 2 |
| "plus the machine row" | The Director has no machine picker because it IS the machine | A compact row of chips at the top of the repository area |

## 2. The three controls that could not be copied, and why

### Per-row remove - DEFERRED, by the Delivery Lead on the Tech Lead's analysis

`DELETE /directors/{id}/repos` removes the row from the **Director's registry**. This screen reads
the **Gateway catalogue**, and `KnownRepositoryStore` has no delete at all - so the row would not
disappear. A button that visibly does nothing is the Voice-screen failure Critical Rule 7 exists to
stop. Worse, for any path under a registered root folder, `DiscoveredRepositoryObserver` re-adds it
within a three second debounce, so even a catalogue delete would undo itself.

It lands later, on a provenance model another seat is building. The Tech Lead's sentence, for the
record and in their words:

> "I will not ship a remove that undoes itself."

### Browse - REPLACED, by the Delivery Lead's adopted decision

The Director's Browse button opens a native folder picker **on the machine it runs on**. The Cockpit
runs in a browser and is NEVER the machine the path describes, so a Browse button here opens the
VIEWER's file system for a path that has to exist on the Director's disk. That is this mission's
recurring path-comparison defect wearing a button.

In its place: the path box the dialog already had, paired with an **Add to this machine** button
calling `POST /directors/{id}/repos`. A browser genuinely can do that for a disk it is not sitting on.
Screenshot 08 shows the empty state offering Add exactly where the Director offers Browse, and the
test `draws no Browse button, because this screen is never the machine the path is on` holds it down
on both the populated and the empty screen.

### Add - BUILT, and it turned out to be half-connected. Found by reading, not by shipping

**This one was not known when the mandate was written. I found it reading the code and raised it
before drawing anything; the Tech Lead verified it independently against `origin/main` rather than
taking it.**

`POST /directors/{id}/repos` rides the `repo-add` verb into `SessionWriteExecutor.RepoAdd`, which
writes `RepositoryRegistry` **and nothing else**. The writers of `KnownRepositoryStore` are
`SessionHistoryRecorder` (a session ran) and `DiscoveredRepositoryObserver` (a Director's root-folder
scan, from `RepositoryMonitor.Snapshot`). The registry is in neither. So today, for a path that is not
already under a registered root folder, a successful Add leaves this list unchanged - the same shape
the per-row remove was deferred for.

**It was kept, and the reason it was kept matters.** The Add does a real thing: the path is registered
on that machine and appears on that machine's own Director New Session tab at once. And the gap is
being closed right now by another seat, so the screen was built FORWARD-COMPATIBLE rather than around
the gap:

- after a successful Add the screen **RE-READS the one list** and reports what it then actually found;
- the wording reports **two facts and not a third**: what the route did, and what the list held when
  it looked. It predicts nothing. A sentence explaining the gap by its mechanism - *"it joins the list
  once a session has run in it"* - would be true today and **wrong the week the registry feed lands**;
- when that feed lands, the same re-read simply finds the row and the same code says so. There is
  nothing here to tear out.

Screenshot 10 is the honest case: *"Added harbour to this machine. The list above is the one the
Gateway serves, and it does not show this path."* The test
`reports the act and what it then FOUND, without predicting when a missing row will appear` asserts
that sentence and additionally asserts it contains none of `once`, `until`, `will`, `soon`, `yet` -
so a future edit cannot quietly put a forecast back.

## 3. The ordering, which is the whole point of the mission

**THIS SCREEN NEVER READS `lastUsed` TO DECIDE ORDER. Not once, in any file this phase touched.**

The Gateway already ruled - most recently used first, never-opened beneath, then by name and by path -
and Critical Rule 7 says a client renders that ruling rather than re-deriving it. `orderRepositories`
is the whole of it:

| Heading state | What is rendered |
|---|---|
| **Last Used, descending** - how the dialog opens | **The served array itself**, returned index for index. No sort call at all. The heading reads as active because it LABELS the Gateway's ruling, not because anything computed one. |
| Last Used, ascending | **That array reversed.** A pure reversal: no key, no comparator, no policy about a missing time. |
| Name / Path | A string comparison on that field, which is the USER asking for something else - the user's request, not the client deciding what the list MEANS. |

So no date parsing and no nulls-last rule reaches TypeScript. That matters more than it looks: phase 2
proved PostgreSQL and C# disagree about where a null sorts, and a null policy here would be a third
opinion on one list.

**The guard is the fixture, not the assertion.** The test that pins the default asserts
`toBe` - the SAME array, not an equal one - so a sort that happens to agree cannot satisfy it. The
heading round trip runs over a four-row fixture in which a never-opened repository sits ABOVE a used
one and the two used ones are out of recency order, so that **newest-first, oldest-first,
timeless-first, timeless-last and by-name each move at least one row**. Revert C in section 8 shows
why that is not decoration: the realistic fixture PASSES while the screen sorts for itself.

Screenshots 03, 05 and 06 are the same round trip on the page: the Gateway's order, then by name, then
back to the Gateway's exact array.

## 4. The wording

| Thing | What it says | Where it comes from |
|---|---|---|
| The Last Used cell | "just now", "7m ago", "3h ago", "2d ago", "4mo ago", "1y ago" | `RepositoryConfig.FormatTimeAgo` in `src/CcDirector.Core/Configuration/RepositoryConfig.cs`, thresholds copied. **Deliberately NOT client-core's `durationLabel`**, whose sub-minute wording differs and which prints "2d 3h" where this prints "2d ago" - using it would make two screens word one span two ways, in the mission whose point is that they stop disagreeing. |
| A never-opened repository | **"Never opened"** | The Tech Lead's decision. The Director shows an EMPTY cell, and it is empty only because it has no such field yet (phase 6 gives it one). The Gateway now STAMPS the verdict, and an empty cell is the client deciding an absence is not worth saying - the habit this mission exists to end. |
| The status line | "10 repositories on this machine, most recently used first." and, when a heading is clicked, "…, by name." / "…, by path, reversed." | It names the order it is ACTUALLY in. A line claiming recency over a list sorted by name would be the screen telling the owner something he can see is untrue. |
| The create error | "Select a repository above, or enter a path below." | It used to read "Enter a repo path, or click a recent repo above" - a description of a click this phase changed, and an abbreviation besides. |
| Empty state | "No repositories yet" + the Director's own sentence and footnote | `RepoEmptyState` in `NewSessionDialog.axaml`, with "add it" where the Director says "browse to it". |

**THE VERDICT IS READ, NEVER INFERRED.** `lastUsedCell` reads `neverOpened`. It does not write
`lastUsed ? ... : ...` and conclude the same thing for itself. The test that holds this feeds two rows
an inference gets **wrong in opposite directions** - one stamped never-opened yet carrying a time, one
unstamped and carrying none - because a one-row fixture catches one of those two and lets the other
ship. Revert D is that test watched failing.

## 5. What changed

| File | Change |
|---|---|
| `apps/cockpit/src/sessions/NewSessionDialog.tsx` | The repository half redrawn: the machine row, the search box, the Name / Path / Last Used table with sortable headings, the first-run empty state, the path box paired with Add, and **a click that selects instead of starting a session**. |
| `apps/cockpit/src/styles.css` | The `.newsess-*` family extended in its own idiom: the machine picker becomes a compact wrapping row, the list becomes a table on one shared column track, plus the empty state, the headings and the Last Used cell. |
| `packages/client-core/src/api/client.ts` | `addRepo` and `RepoAddResult`, in the idiom of the readers beside them (`encodeURIComponent`, `authHeaders`, `GatewayError.from`) - additive, so phase 5's work in this file conflicts with nothing. Plus the two-line `withRetryHint` fix in section 7a, which is the one existing thing this phase changed there and is a defect this proof found. |
| `apps/cockpit/src/sessions/newSessionDialogOneList.test.tsx` | The screen's test file, **7 tests to 32**. The seven that were there are intact; one fixture row was RENAMED (`"Never opened"` to `"Not yet opened"`) because the product now uses those exact words in a cell and an exact text query could no longer tell the two apart. |
| `apps/cockpit/src/fleet/FleetMapView.test.tsx`, `apps/cockpit/src/sessions/sessionsBadge.test.tsx` | `addRepo` added to the client mock beside `getKnownRepositories`. |

**No Gateway change, no Director change, no phone change, and nothing in
`packages/client-core/src/settings` or `apps/cockpit/src/fleet/DirectorDetailView.tsx`.**

### The style, and the correction this phase is built on

**`docs/VisualStyle.md` DOES NOT GOVERN THE BROWSER SHELLS.** Its own opening block says so, and the
Delivery Lead accepted that correction against the mission document. So "the Director's layout" here
means **STRUCTURE and WORDING parity, never COLOUR parity**. The binding reference is
`apps/cockpit/src/styles.css` and its tokens (`--surface`, `--surface-2`, `--text`, `--text-dim`,
`--border`, `--accent`). **No hex value was copied out of the Avalonia XAML**, and no token was
invented.

## 5a. How the Add note decides whether the path it added is in the list

**The mission's own first defect arrived in this phase's code and a Reviewer caught it.** After a
successful Add the screen re-reads the list and reports whether the added path is in it (section 2).
The first version of that comparison folded CASE and nothing else.

That is wrong in both directions. Both stores this mission joins compare paths by
`KnownRepositoryStore.NormalizePathKey`, which **normalizes separators and trailing slashes** - so a
trailing slash or the other separator could make the note say *"it does not show this path"* with the
row sitting visible on the screen above it, which is the screen telling the owner something the screen
itself disproves. And a plain `toLowerCase()` folds case on a POSIX path, where `/work/Atlas` and
`/work/atlas` are two different folders and calling them one is a claim the machine does not agree
with. **Deciding path identity by a rule that is not the path's own shape is exactly the defect family
this mission exists to end.**

`repositoryPathKey` now mirrors `NormalizePathKey` clause for clause and **no wider**: trim; backslash
to forward slash; drop trailing slashes except a Windows drive root; and fold case **only when the
path's own shape says it is a Windows path** - a drive letter, or a `//` network prefix. Two places
where TypeScript and C# are not identical were checked rather than assumed and are written on the
function: `char.IsLetter` is Unicode-aware where `/[A-Za-z]/` is not, which cannot matter for a drive
letter; and JavaScript's `toUpperCase` is locale-independent, like `ToUpperInvariant`.

Two tests hold it: one asserting each clause, **including that two POSIX paths differing only in case
are NOT the same repository** - the assertion a plain case fold fails - and one driving the whole Add
through a path the Director echoes back with the other separator and a trailing slash.

**Row selection is deliberately still an exact string match, and that is not an oversight.** The path
box is set from the row that was clicked, so the two are the same string by construction; and a
normalizing match would light a row up as "selected" while Create session ran on the different string
in the box. The rule above is for the Add note, which compares two paths that genuinely came from two
places.

## 6. The mission check, section 7, as I ran it

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, `dotnet` at `~/.dotnet/dotnet`.
Run from the worktree root, exactly as section 7 writes them, against the branch tip this proof
describes.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces. |
| `npm test --workspaces --if-present` | **Green. 2,155 passed, 0 failed** - client-core 1,459, cc-assistant 106, cockpit 489, mobile 101. |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **Green. 0 failed, 6,547 passed, 8 skipped.** |
| `dotnet test src/CcDirector.Core.Tests` | **Green. 0 failed, 4,485 passed, 18 skipped.** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **Green. 0 failed, 646 passed, 0 skipped.** |

**Zero failures, and no baseline is quoted.** Against the Tech Lead's own measurement of this
branch's base, the three .NET suites are identical to the digit (6,547 / 4,485 / 646); the Cockpit is
up by the 25 tests this phase adds to that screen, and client-core by the 3 that pin section 7a's fix.
Nothing else moved. **The .NET numbers were measured before section 7a**, whose change is three
TypeScript lines and a test file; no `.cs` file is touched anywhere in this branch.

**One thing that is NOT green, and it is not a test.** `npm run lint` reports three errors, all of the
form *"Definition for rule '…' was not found"* - `@typescript-eslint/no-implied-eval` in
`apps/cockpit/src/push/sw.test.ts`, and `react-hooks/exhaustive-deps` in two `apps/mobile` files. They
are an eslint configuration gap in files this phase does not touch, they are not in the mission check,
and they are reported here rather than left for the next seat to find.

## 7. The behavioural proof - the flow AND the failure cases

`screens/`. Eleven screenshots of the real screen, driven through a Director-owned Chrome profile.

### How they were taken, and what was staged

**THE DATA IN EVERY SCREENSHOT IS STAGED, AND SO IS THE GATEWAY.** No real fleet, no real account, no
real repository, and no path from anyone's disk appears in any of them - this repository is public.
The machines ("North", "Studio", "Laptop") and the repositories ("atlas", "beacon", …) are invented.

- The Cockpit runs on **its own Vite dev server** (`npm run dev`, port 5199), built from this branch.
- The Gateway behind it is a **stand-in HTTP server** (`staging/stub_gateway.py`) serving the four
  routes this screen reads, fronted by the dev server's own `COCKPIT_PROXY_TARGET` proxy exactly as a
  real Gateway would be. The scenario is a JSON file (`staging/set_scenario.py`) swapped between shots.
- **No product code was changed to take these**, and no test-only branch exists in the shipped screen.
  The screen makes its real `fetch` calls, through the real client-core reader, and sees real HTTP
  status codes - which is how the 502 in shot 09 is a genuine 502 rather than a rendered pretence.
- The browser is the Director-owned profile `repo-list-phase-4-proof`, driven by `browser-harness`
  over the debugging protocol, and stopped afterwards.

**This is a deviation from the letter of my mandate and it is flagged rather than buried.** The Tech
Lead named the debugging protocol's *request interception* as the staging mechanism. I staged at the
origin instead, behind the dev server's proxy. It meets both constraints the Tech Lead set for that
choice - no product code change, and failure cases producible on purpose - and it is deterministic,
where interception would have to win a race with the page's own fetches on every shot. The screenshots
are otherwise taken exactly as instructed.

One thing worth knowing for anyone repeating this: **the dev proxy fronts `/sessions`, which is also
the Cockpit's own route for this page.** A hard navigation to `/sessions` is answered by the Gateway
and the browser renders JSON. The shots enter on a route the proxy does not front and reach Sessions
through the left rail, so the router does the navigating.

### The flow

| Shot | What it shows |
|---|---|
| `01-the-new-session-tab.png` | **The whole screen.** The machine row, the search box with its visible placeholder, Name / Path / Last Used with Last Used active and descending, ten rows in the Gateway's order, the path box and Add - and no Browse button anywhere. |
| `02-the-machine-row.png` | **The machine row.** One compact row where a tall column of cards used to be, and no fact lost: the display name, the Control API port, how long that Director has been up, and its version, on every chip. |
| `03-the-gateways-order-never-opened-beneath.png` | The Last Used ladder on the page - "just now", "7m ago", "3h ago", "2d ago", "4mo ago", "1y ago" - and **three never-opened repositories sitting beneath every used one, each reading "Never opened"** rather than an empty cell. |
| `04-a-click-selects-it-does-not-start.png` | **The defect this phase was told to fix.** A row was clicked: it reads as selected, the path box holds its path, the dialog is still open and no session was created. |
| `05-a-heading-click-is-the-user-asking-by-name.png` | The Name heading clicked: a genuinely different order, and the status line now says "by name" instead of claiming recency. |
| `06-back-to-the-gateways-order.png` | **The round trip.** After Name and then Path, clicking Last Used returns the list to the Gateway's exact array - asserted in the browser, not only in the test. |
| `07-the-search-box-filters-on-name-or-path.png` | A search for "beacon" matching one row **by its name** and another **by its path**. |

### The failure cases

| Shot | What it shows |
|---|---|
| `08-a-machine-with-no-repositories-yet.png` | **An empty catalogue.** The Director's own empty state - "No repositories yet", its sentence, its footnote - with **Add where the Director puts Browse**. |
| `09-the-director-is-offline-the-list-survives-the-write-is-refused.png` | **THE MISSION'S CENTRAL CLAIM, PHOTOGRAPHED.** The Director is offline. The list is **still all there** - ten repositories, in the Gateway's order - because `GET /directors/{id}/known-repositories` is synchronous and storage-backed and has no tunnel leg in it at all. In the same frame, Add is refused in the Gateway's own words, *"The Director is not connected right now, so the command was not delivered."*, because `POST /directors/{id}/repos` DOES ride the tunnel. One image, both facts: **the read survives the Director going away and the write does not**. That is the whole reason this screen was moved onto the catalogue route. |
| `09b-the-catalogue-store-is-unavailable.png` | **A real failure of the read route**, from its own code: `knownRepositories is null` answers 503 *"Known repository storage is not available."* The screen says what the Gateway said. It does NOT show the first-run empty state, which would tell the owner his machine holds nothing when all that is known is that the Gateway could not be asked; and it draws no table at all, because a bare heading row offers to sort a list the screen does not have. |
| `10-add-registers-the-path-and-says-what-it-then-found.png` | **Add succeeded and the row is genuinely not in the list.** Section 2. Two facts and no forecast. |
| `11-add-refused-the-path-does-not-exist-on-that-machine.png` | **Add refused, 400.** The Gateway's own sentence inline, the list untouched, and nothing claiming a repository was added. |

### The tests behind the same behaviour

`apps/cockpit/src/sessions/newSessionDialogOneList.test.tsx`, 32 tests. The seven that existed before
this phase are unchanged. The 25 added cover: the machine row's facts; the served array returned by
identity; the pure reversal; the heading transitions; the round trip over the adversarial fixture; the
status line naming the real order; the ladder, rung by rung and on the page; the verdict read rather
than inferred; the filter over name AND path; a filter that matched nothing not being mistaken for an
empty machine; the click selecting; Create session being the only thing that creates; the empty state;
the absence of Browse; the five Add outcomes; and **the path rule in section 5a**.

## 7a. A correction to this proof, and the real defect that came out of it

**This section records that an earlier version of this proof got a Gateway fact wrong, photographed it,
and asserted it in prose.** It is written out rather than quietly re-shot, because a proof that
corrects itself in the open is worth more than one that has never been caught.

### What this proof claimed, and what the route actually does

An earlier shot 09 was captioned *"the Director is not connected, the route is 502"* and showed an
empty list. The staging behind it made `GET /directors/{id}/known-repositories` answer 502 with the
reason "Director not connected".

**That cannot happen.** Read the route on `origin/main` (`GatewayEndpoints.cs`,
`app.MapGet("/directors/{id}/known-repositories", (HttpContext ctx, string id) => ...`): it is
**synchronous**. No `CancellationToken`, no `TrySendAsync`, no `TunnelFailure` - **there is no tunnel
leg in it at all.** It resolves the Director, checks the store and returns
`knownRepositories.ReadForMachine(...)`. Its real failure branches are a 503 when the store is
unavailable, a 403 with no bound tenant, a 409 when the Director has reported no machine name, and
whatever `TryResolveOwnedDirector` returns. `GET /directors/{id}/repos`, two screens up in the same
file, **is** async and **does** ride the tunnel - that is the route that 502s when a Director is gone,
and it is the route this phase moved OFF.

**The harm was worse than a wrong caption**, which is why the picture was replaced rather than
re-labelled. The owner opening this folder would have seen a photograph captioned "the Director is not
connected" over an empty list, and read it as *a disconnected Director empties the Cockpit's list*.
That is the **opposite** of what this mission is for, and it contradicts the header comment on this
screen's own read - *"it survives that Director being disconnected, which is exactly when this screen
still needs something to show."* The proof asserted the design claim in prose while photographing its
opposite.

Found by the Reviewer, verified independently by the Tech Lead and again here against `origin/main`
before anything was changed.

### What replaced it

`09-the-director-is-offline-the-list-survives-the-write-is-refused.png` - the shot the mission actually
wants, and a stronger one: the Director is offline, **the list is still all there**, and in the same
frame the tunnel-backed Add is refused in the Gateway's own words. Both halves of that image are what
the real Gateway sends for an offline Director. Plus
`09b-the-catalogue-store-is-unavailable.png`, a genuine failure of the read route taken from its own
code.

### The defect the exercise found, which is real and is NOT the above

While staging that 502, the error line rendered as two sentences run together:

```
... not connected Try again.
```

**The malformed-text class is real and shipped**, even though the reason this proof staged was not one
this route sends. `withRetryHint` in `packages/client-core/src/api/client.ts` appended `" Try again."`
to whatever it was handed, assuming it already ended in terminal punctuation. The Gateway writes some
reasons as SENTENCES ("That machine is catching up.") and some as PHRASES. A shipped example, cited
from the product rather than invented, since a made-up one is exactly how this was nearly
mis-diagnosed:

> `SessionWsProxyEndpoints.WriteVerbJsonAsync` answers `{ error = "owning director is not connected" }`
> at **503** when the owning Director is not tunnel-connected. 503 is retryable by default in the
> `GatewayError` constructor, so that reason reached the screen as
> *"owning director is not connected Try again."*

**It is fixed in `withRetryHint`, not in the dialog** - `CLAUDE.md` rule 3. Two lines: trim the reason,
terminate it if it is not already terminated, then append. Three tests in
`packages/client-core/src/api/errorReporting.test.ts` pin BOTH endings, because a careless fix trades
one malformed line for another:

| Test | What it holds |
|---|---|
| `terminates a reason that is a PHRASE before adding the hint...` | The defect, on the real shipped phrase above, at its real status, with **no `retryable` flag set by hand** - so it exercises the default rule that makes a 503 retryable, which is the half that turns a merely-unterminated reason into a run-together line. |
| `leaves a reason that already ends in a sentence exactly as it was` | The opposite mistake - an unconditional full stop giving an already-terminated reason two of them. Full stop, question mark and exclamation mark, each asserted on the whole string. |
| `does not leave a gap where the reason had trailing space` | A reason with trailing whitespace does not leave a hole before the full stop. |

Revert F in section 8 watches the fix fail.

**NO SCREENSHOT IN THIS PROOF SHOWS THAT DEFECT, and that is stated rather than implied.** The earlier
picture of it was produced by a reason this route does not send, so it has been deleted rather than
kept with a caption. Of the failures this screen can actually produce, none carries an unterminated
reason today: the 503 is *"Known repository storage is not available."* and the Add refusal is *"The
Director is not connected right now, so the command was not delivered."* - both already sentences. The
fix stands on the code citation and the tests, not on a picture.

### What this whole episode is worth writing down

Thirty passing tests of this screen did not find the run-together line; staging a failure case and
looking at it did. And no test of this screen could have found the wrong-route claim either - it was
in a caption and in prose, where only a reader who went to the route could catch it. **The Reviewer
could not render images, so the pictorial content was the one thing it could not check, and it is
exactly where the error was.** Both facts belong in the record.

## 8. Watched failing

Five reverts. **The predicted symptom of each was committed BEFORE any of them was run** -
`predicted-symptoms.md`, commit `b45df8fd4` - and `watched-it-fail.md` records what actually happened,
including the two places the prediction was wrong.

| Revert | Predicted | Actual |
|---|---|---|
| A - read `getRepos` again | typecheck fails, ~20 tests | **typecheck fails** naming the missing `neverOpened`, **22 tests** |
| B - the row creates a session | exactly 2 | **exactly 2** |
| C - the client re-sorts by `lastUsed` | exactly 5, named | **exactly those 5** |
| D - infer the verdict | exactly 1 | **exactly 1**, both rows wrong in opposite directions |
| E - filter on the name only | exactly 1 | **exactly 1** |
| F - the retry hint stops terminating the reason | exactly 2, and one named test staying green | **exactly 2**, and that test stayed green |
| G - the Add note compares paths by a plain case fold | exactly 2, named | **exactly those 2** |

In every one of the five, the other 55 Cockpit test files stayed green.

**The two lines worth carrying out of it:**

1. **Revert C's no-op happened, as predicted.** The realistic fixture - two used repositories newest
   first, one never-opened at the bottom - **PASSED while the screen was sorting for itself**, because
   a comparator is a no-op over a list that already agrees with it. It reads exactly like the ordering
   guard and is not one. The guard is the adversarial fixture.
2. **Revert B's two tests are all that stand between this screen and launching an agent on a mis-aimed
   click.** 484 tests were green while it did. One of those two assertions was added *while writing
   the predictions*, before any revert ran, because the test as first written would have passed
   straight through the defect.

## 9. What this proof does NOT cover

- **No real Gateway, no real Director, no real session.** Every Gateway response in the screenshots
  came from the stand-in server, and every one in the tests from a mock. What is proved is what the
  screen does with what it is handed - which is the whole of Critical Rule 7's claim about a client -
  and nothing more. That the real route serves this shape is phase 3's proof, not this one.
- **Nobody clicked "Create session" against a real Director.** The create path is unchanged by this
  phase and is asserted with a mock; no agent was launched to take a screenshot.
- **The Add's forward compatibility is reasoned, not demonstrated.** The claim that this screen becomes
  simply correct when the registry-to-catalogue feed lands follows from the re-read and the wording; it
  cannot be shown until that feed exists. When it does, screenshot 10's scenario should be re-run - the
  note under it should change by itself, and if it does not, that is a defect here.
- **Section 7a's fix is proved by TEST AND CODE CITATION ONLY - by no screenshot at all.** `withRetryHint` is
  shared by both browser shells and every screen that shows a Gateway error, and **none of the failures
  this screen can produce carries an unterminated reason**, so nothing here photographs it. **No audit
  was made of which other reasons the Gateway writes without terminal punctuation, or of which other
  screens were showing the same run-together line** - `SessionWsProxyEndpoints` is one citation, not a
  survey. That is worth someone's afternoon and it is not this phase's.
- **Nothing here was run against a really disconnected Director.** Shot 09's two halves are each what
  the real Gateway sends for one - the storage-backed read unaffected, the tunnel-backed write refused
  with `MapDirectorFailure`'s own sentence - and both were read out of `origin/main` before being
  staged. But no Director was actually disconnected, so what is proved is that the screen renders those
  two answers correctly, not that the Gateway produces them together. Phase 6 disconnects a real one.
- **No browser other than Chrome, and one viewport.** 1440 by 1040 at device scale 2. Nothing here
  says what this screen does on a narrow window, and the Cockpit is desktop-first by design.
- **Volume.** Phase 3 recorded that nothing caps this route's result and nothing measures what a
  machine with a thousand repositories does to the screen that renders it. This phase draws that screen
  and **still does not measure it**. The table scrolls within a fixed height, which is a layout answer
  and not a volume answer.
- **Accessibility beyond roles and labels.** The controls carry `aria-pressed`, the search box carries
  a label, and the tests find everything by role and by text. No screen reader was run, and no keyboard
  traversal of the new table was tested.
