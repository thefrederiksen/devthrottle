# Phase 4 - the five reverts, run, and what actually happened

Mission: One repository list, held on the Gateway. Phase 4, the Cockpit's New Session tab.

Read `predicted-symptoms.md` first. It was committed in `b45df8fd4`, **before** the first revert was
run; every command below was run after that commit and the history shows the order.

Each revert was applied on top of the phase-4 commit, the whole Cockpit suite was run (486 tests
across 56 files), the failures were recorded, the file was restored with `git checkout --`, and the
suite was run again. Nothing outside `apps/cockpit/src/sessions/newSessionDialogOneList.test.tsx`
went red in any of the five - **55 of the 56 test files passed in every run**, which is the other
half of the claim: the guards catch what they are aimed at without catching everything.

| Revert | What it removed | Predicted | Actual | Match |
|---|---|---|---|---|
| A | The route: read `getRepos` again | typecheck fails, ~20 tests in the file | typecheck fails, **22 tests** | Yes, with a correction below |
| B | The click: the row creates a session | exactly 2 | **exactly 2** | Yes |
| C | The ordering: the client re-sorts by `lastUsed` | exactly 5, named | **exactly those 5** | Yes |
| D | The verdict: infer it from a missing time | exactly 1 | **exactly 1** | Yes |
| E | The search: filter on the name only | exactly 1 | **exactly 1** | Yes |

After the fifth restore: `npm run typecheck` clean, `Test Files 56 passed (56)`,
`Tests 486 passed (486)`.

---

## Revert A - the route switch back to `getRepos`

**`npm run typecheck --workspace apps/cockpit` failed before a single test ran**, exactly as
predicted and with the error named in advance:

```
src/sessions/NewSessionDialog.tsx(387,18): error TS2345: Argument of type 'RepoInfo[]' is not
  assignable to parameter of type 'SetStateAction<KnownRepoInfo[] | null>'.
  Type 'RepoInfo[]' is not assignable to type 'KnownRepoInfo[]'.
    Property 'neverOpened' is missing in type 'RepoInfo' but required in type 'KnownRepoInfo'.
```

That is worth more than a test. The registry route cannot supply the Gateway's verdict, and the
compiler says so at the point of the swap rather than letting the screen quietly fall back to
inferring one.

**Tests: 22 failed, 464 passed.** All 22 in this file; the other 55 files passed. The first, with the
message predicted for it:

```
× ... asks for the one list on the selected machine, and never for the Director's registry half
  → expected "spy" to be called 1 times, but got 0 times
```

and the rest reading as an empty machine, for example
`Unable to find an element with the text: 3 repositories on this machine, most recently used first.`

**WHERE THE PREDICTION WAS WRONG, and it is the useful line in this document.** I predicted "most of
the phase-4 block" would fail with the machine-row test as the only exception. Seven tests stayed
green, not one, and **two of them I had not thought about**:

* `says a machine has no repositories rather than showing an empty list in silence`
* `offers the first-run empty state when the catalogue is empty, with Add where the desktop puts Browse`

Both pass under revert A for the same reason: `getReposMock` resolves to an empty array, so the page
renders precisely the empty state those two tests are asking for. **An empty list from the WRONG
ROUTE is indistinguishable, on the page, from an empty list from the right one.** Neither test is
wrong - each proves what its name says - but neither says anything about which route was read, and I
would have been fooled by them if they had been the only tests here. The test that does say it is the
one asserting `getKnownRepositoriesMock` was called and `getReposMock` was not.

The other five that stayed green were predicted: the machine row feeds no repositories, and the four
pure-function tests call the exported functions directly and never reach the route.

## Revert B - the row starts a session again

`onClick={() => selectRepository(r.path)}` put back to `onClick={() => void create(r.path)}`.

**Tests: 2 failed, 484 passed** - exactly the two predicted, with exactly the predicted message:

```
× ... SELECTS the repository that was clicked, and starts no session
  → expected "spy" to not be called at all, but actually been called 1 times
× ... starts the session the user selected only when Create session is pressed
  → expected "spy" to not be called at all, but actually been called 1 times
```

The second of those two assertions was added to that test *while writing the predictions*, before any
revert ran, precisely because the test as first written would have passed straight through this
defect: the row click created the session, and the later assertions about what Create session did
were all still satisfied. One line - "nothing has started yet" - is the difference between a test
that catches this and a test that watches it happen.

**And this is the revert to take seriously.** 484 tests were green while the screen launched an agent
on a single mis-aimed click. Nothing about the page LOOKS different: the rows render, the order
holds, the status line is unchanged. Two assertions stand between this screen and that behaviour.

## Revert C - the client re-sorts by `lastUsed`

The default branch of `orderRepositories` replaced with a comparator - newest first, missing times
last, which is the shape a developer reaches for and is this mission's own defect.

**Tests: 5 failed, 481 passed** - exactly the five named in advance:

```
× renders an order no client rule would produce, because the order is not this screen's to decide
  → expected [ '/repositories/september', …(3) ] to deeply equal [ '/roots/zulu', …(3) ]
× hands back the served array itself for the order it opens in, without sorting anything
  → expected [ { …(4) }, …(3) ] to be [ { …(4) }, { …(4) }, { …(4) }, …(1) ] // Object.is equality
× flips Last Used by reversing that array, with no key and no rule about a missing time
× returns to the Gateway's exact array after a round trip through Name and Path
× reverses the served array when Last Used is clicked again, and comes back on the next click
```

**THE PREDICTED NO-OP HAPPENED, AND IT IS THE MOST IMPORTANT LINE HERE.**
`renders the rows in the Gateway's order, with the never-opened repository beneath the used ones`
**PASSED while the screen was sorting for itself.** Its fixture is a realistic list - two used
repositories newest first, one never-opened at the bottom - and a newest-first comparator is a no-op
over a list that already agrees with it. That test reads exactly like the ordering guard and is not
one. The guard is the adversarial fixture, in which a never-opened repository sits ABOVE a used one
and the two used ones are out of recency order, so that newest-first, oldest-first, timeless-first,
timeless-last and by-name each move at least one row.

The `Object.is` failure is the one that cannot be satisfied by a sort that happens to agree with the
Gateway: the default state is asserted to be **the same array**, not an equal one, so the absence of a
sort is what is pinned rather than the result of one.

## Revert D - infer the verdict from a missing time

`lastUsedCell` reading `repository.neverOpened` replaced with
`repository.lastUsed ? lastUsedAgo(...) : "Never opened"`.

**Tests: 1 failed, 485 passed:**

```
× reads the never-opened verdict instead of deciding for itself what a missing time means
  → expected [ '1d ago', 'Never opened' ] to deeply equal [ 'Never opened', '' ]
```

**Both rows wrong, in opposite directions**, which is why that fixture has two rows and not one. The
row the Gateway STAMPED as never opened, which also carries a time, is displayed as recently used.
The row the Gateway did NOT stamp, which carries no time, is announced as never opened on no
authority whatsoever. A one-row fixture catches one of those two and leaves the other to ship.

As predicted, `shows each rung of that ladder on the page...` stayed green: in that fixture the
stamped row also has an empty `lastUsed`, so the verdict and the inference agree and the test cannot
tell them apart.

## Revert E - the search box filters on the name only

**Tests: 1 failed, 485 passed:**

```
× filters on the name OR the path, the way the desktop tab's search box does
  → expected [ '/work/alpha' ] to deeply equal [ Array(2) ]
```

The row whose PATH holds the needle vanished; the row whose NAME holds it stayed. That fixture is
built so the two fields disagree about which rows match - one row matches by name only, one by path
only, one not at all - because a fixture where name and path agree cannot tell a two-field search
from a one-field one.

---

## What these five reverts do NOT prove

* **Nothing here ran against a real Gateway.** Every Gateway call in this file is replaced by a mock.
  What is proved is what the screen does with what it is handed, which is the whole of Critical Rule
  7's claim about a client - but the shape of what a real `GET /directors/{id}/known-repositories`
  hands it is proved elsewhere, in phase 3's tests, not here.
* **No revert was aimed at the machine row, the empty state's wording, or the stylesheet.** Those are
  covered by tests and by the screenshots in `screens/`, not by a watched failure.
* **A revert proves the guard catches the defect that revert removed.** It does not prove the guard
  catches everything its name suggests - revert C's passing test and revert A's two unpredicted
  passes are both instances of exactly that, recorded above rather than tidied away.
