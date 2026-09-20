# Phase 4 - what I predict each revert will do, written before any revert was run

Mission: One repository list, held on the Gateway. Phase 4, the Cockpit's New Session tab.

**This file is committed BEFORE a single revert is run, and the commit order in the history is the
evidence of that.** Phases 2 and 3 did the same and their reviewers checked it. The reason is not
ceremony: a prediction written after watching the failure is not a prediction, it is a transcript,
and it proves nothing about whether the guard was aimed at the defect or fitted to it afterwards.

Five reverts. Each removes ONE load-bearing part of this phase. For each I say, in advance, which
tests I expect to go red, with what symptom, and - just as importantly - **which tests I expect to
stay GREEN while the defect is present**, because that is where a proof lies to you.

The general warning I am writing these under, which cost this phase real time twice already:

> A REVERT PROVES YOUR GUARD CATCHES THE DEFECT YOU JUST REMOVED. IT DOES NOT PROVE IT CATCHES THE
> CLASS YOUR TEST'S NAME CLAIMS.

The clearest instance is revert C below, and I am predicting it deliberately: the realistic
repository fixture cannot catch a screen that sorts by recency, because it is already in recency
order, and a comparator is a no-op over a list that already agrees with it. If only the adversarial
fixture goes red, the guard is where it needs to be and the realistic one is decoration for this
purpose. I expect exactly that and I will say so if it turns out otherwise.

---

## Revert A - the route switch: read `getRepos` again instead of `getKnownRepositories`

**The change being removed.** `apps/cockpit/src/sessions/NewSessionDialog.tsx` reads
`getKnownRepositories(selectedId)` - `GET /directors/{id}/known-repositories`, the Gateway's one
ordered catalogue. The revert points it back at `getRepos(selectedId)` -
`GET /directors/{id}/repos`, the Director's own registry, which is the "half a list" this mission
opens with.

**I predict `npm run typecheck` FAILS, before any test runs.** `getRepos` returns `RepoInfo[]` and
the screen's state is `KnownRepoInfo[]`. `RepoInfo` has no `neverOpened`, so `setRepos(list)` is a
type error naming that missing property. I expect that to be the first thing that breaks, and it is
a guard in its own right: the half-list route cannot supply the Gateway's verdict, and the compiler
says so rather than letting the screen fall back to guessing.

**I predict these tests fail** in `apps/cockpit/src/sessions/newSessionDialogOneList.test.tsx`:

| Test | Predicted symptom |
|---|---|
| `asks for the one list on the selected machine, and never for the Director's registry half` | `waitFor` times out on `expect(getKnownRepositoriesMock).toHaveBeenCalledTimes(1)` - it is called zero times. |
| `renders the rows in the Gateway's order, with the never-opened repository beneath the used ones` | Times out waiting for "Not yet opened"; the page shows the first-run empty state, because `getReposMock` resolves to an empty array. |
| `renders an order no client rule would produce...` | Same: times out waiting for "Zulu never opened". |
| `describes the list in the status line...` | The status line reads "No repositories known on this machine. Enter a path below." instead of the count. |
| `counts one repository as one repository...` | Same. |
| `says the read failed, and says what the Gateway said...` | The rejection is set on `getKnownRepositoriesMock`, which nothing calls, so the page renders an empty machine instead of the error. |
| Most of the phase-4 block - the machine row is the exception | Every test that feeds rows through `getKnownRepositoriesMock` sees an empty table. I expect roughly twenty failures in this file. |

**I predict these stay green:** `says which machine the repositories are on...` (it feeds no
repositories), and the four pure-function tests (`hands back the served array itself`, `flips Last
Used by reversing that array`, `starts a newly clicked heading ascending`, `words a last-used time on
the desktop tab's own ladder`) - they call the exported functions directly and never touch the route.

## Revert B - the click: make the row start a session again

**The change being removed.** The repository row's `onClick` calls `selectRepository(r.path)`. The
revert puts back `() => void create(r.path)`, which is what the screen did before this phase and is
the defect it was told to fix.

**I predict exactly two tests fail:**

| Test | Predicted symptom |
|---|---|
| `SELECTS the repository that was clicked, and starts no session` | `expected "spy" to not be called at all, but actually been called 1 times` on `createSessionMock`, with `("north-1", "/repositories/yesterday", ...)`. |
| `starts the session the user selected only when Create session is pressed` | The same assertion, one line earlier than the Create session click: the row click has already created. |

**I predict the rest of the file stays green, and that is the point worth writing down.** A session
being started by a click is invisible to every test that only reads the page: the rows still render,
the order is untouched, the status line is unchanged. Two tests stand between this screen and
launching an agent in the wrong repository, and after this revert I will know they really do.

## Revert C - the ordering: let the client re-sort by `lastUsed`

**The change being removed.** `orderRepositories` returns the served array itself for the order the
dialog opens in. The revert replaces that branch with a comparator over `lastUsed` - newest first,
missing times last - which is this mission's own defect wearing a feature's clothes: a second opinion
about an order the Gateway already ruled on.

**I predict these tests fail:**

| Test | Predicted symptom |
|---|---|
| `hands back the served array itself for the order it opens in, without sorting anything` | `toBe` fails: a new array, not the served one. This is the assertion that cannot be satisfied by a sort that happens to agree. |
| `renders an order no client rule would produce, because the order is not this screen's to decide` | Rendered `["/repositories/september", "/repositories/august", "/roots/zulu", "/roots/alpha"]` against an expected `["/roots/zulu", "/repositories/september", "/roots/alpha", "/repositories/august"]`. |
| `returns to the Gateway's exact array after a round trip through Name and Path` | Times out on the opening `waitFor`, before any heading is clicked. |
| `reverses the served array when Last Used is clicked again, and comes back on the next click` | Same opening timeout. |
| `flips Last Used by reversing that array, with no key and no rule about a missing time` | The reversal of a re-sorted list is not the reversal of the served one. |

**I predict `renders the rows in the Gateway's order, with the never-opened repository beneath the
used ones` STAYS GREEN, and I am saying so in advance.** Its fixture is a realistic list - two used
repositories newest first, one never-opened at the bottom - and a newest-first comparator is a no-op
over it. That test is worth keeping for what it reads like, but **it is not the ordering guard**, and
if I had written only that one this phase would have shipped a re-sorting client with a green suite.
The guard is the adversarial fixture, in which a never-opened repository sits ABOVE a used one and the
two used ones are not in recency order, so newest-first, oldest-first, timeless-first, timeless-last
and by-name each move at least one row.

## Revert D - the verdict: infer "never opened" from a missing time

**The change being removed.** `lastUsedCell` reads `repository.neverOpened`, the Gateway's stamp. The
revert replaces it with `repository.lastUsed ? lastUsedAgo(...) : "Never opened"` - the client
deciding for itself what an absent date means, which is the habit Critical Rule 7 exists to end.

**I predict exactly one test fails:**

| Test | Predicted symptom |
|---|---|
| `reads the never-opened verdict instead of deciding for itself what a missing time means` | Rendered `["<some ... ago>", "Never opened"]` against an expected `["Never opened", ""]`. **Both rows wrong, in opposite directions** - the stamped row carrying a time is shown as used, and the unstamped row with no time is announced as never opened on no authority at all. |

**I predict `shows each rung of that ladder on the page, and Never opened for the verdict the Gateway
stamped` STAYS GREEN.** In that fixture the stamped row also has an empty `lastUsed`, so the verdict
and the inference agree, and the test cannot tell them apart. Same lesson as revert C: the guard is
the fixture where the two rules DISAGREE, and it needs both rows - one stamped with a time, one
unstamped without one - because either alone leaves the opposite mistake uncovered.

## Revert E - the search box: filter on the name only

**The change being removed.** The filter matches the needle against the name OR the path, which is
the desktop tab's `ApplyRepoFilter`. The revert drops the path half.

**I predict exactly one test fails:**

| Test | Predicted symptom |
|---|---|
| `filters on the name OR the path, the way the desktop tab's search box does` | Rendered `["/work/alpha"]` against an expected `["/work/alpha", "/work/gateway-notes"]` - the row whose PATH holds the needle has vanished. |

**I predict `says a filter matched nothing...` stays green:** its needle matches neither field, so
dropping one of the two fields changes nothing about it.

---

## What I will do with each

Run the revert, run the cockpit suite (and `npm run typecheck` for revert A), record the ACTUAL
failures and their real messages against the predictions above, confirm nothing outside this file's
tests went red, restore, and confirm green again. The results go in `watched-it-fail.md` beside this,
including any place my prediction was wrong - a prediction that was wrong is the most useful line in
that document, not something to quietly correct.
