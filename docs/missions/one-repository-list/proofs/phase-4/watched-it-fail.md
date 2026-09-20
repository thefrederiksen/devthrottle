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

---

# Addendum: revert F - the retry hint's sentence termination

Prediction committed in `c610f5c75`, before this revert was run. Same discipline as the five above.

> **CORRECTED AFTER THE REVIEW.** This section first said the defect was found on *"screen 09 of this
> proof - the Director disconnected, the route answering 502"*. **That route cannot answer 502** -
> `GET /directors/{id}/known-repositories` is synchronous and storage-backed with no tunnel leg - so
> the 502 was a fiction in this proof's own staging. A Reviewer caught it; see section 7a of the README.
> **The defect below is real and shipped and the fix is unchanged**; the test now quotes a reason the
> product really sends, and revert F was RE-RUN against it. The numbers below are that re-run.

**Why there is a sixth revert at all.** There is one because **staging a failure case and looking at
it found a real defect in shared code**, which is the QA report doing its job rather than decorating
it. The error line rendered as two sentences run together with nothing between them.

The root cause is `withRetryHint` in `packages/client-core/src/api/client.ts`, which appended
`" Try again."` to whatever it was handed and assumed that thing already ended in terminal punctuation.
The Gateway writes some reasons as SENTENCES ("That machine is catching up.") and some as PHRASES, and
every phrase produced this, **on every screen in both shells that shows a Gateway error**. The shipped
example the test now uses, quoted from the product: `SessionWsProxyEndpoints.WriteVerbJsonAsync`
answers `{ error = "owning director is not connected" }` at 503, and 503 is retryable by default. It
survived because every reason anyone had looked at happened to end in a full stop - including every
reason in `errorReporting.test.ts`.

It is fixed at the one place that joins the two parts, not in the dialog that happened to be pointed at
it (`CLAUDE.md` rule 3: fix the root cause, do not add a fallback that hides it).

**Result: 2 failed, 1,457 passed** in client-core; cc-assistant, cockpit and mobile untouched at 106,
489 and 101. Exactly the two predicted, with exactly the predicted messages:

```
× terminates a reason that is a PHRASE before adding the hint, so the two do not run together
  → expected 'owning director is not connected Try again.' to be
             'owning director is not connected. Try again.'
× does not leave a gap where the reason had trailing space
  → expected 'owning director is not connected    Try again.' to be
             'owning director is not connected. Try again.'
```

**`leaves a reason that already ends in a sentence exactly as it was` stayed green, as predicted, and
it is the half of the pin that matters most.** The revert cannot fail it, because the old code was
correct for that case. It exists to catch the OPPOSITE mistake - a careless fix that appends a full
stop unconditionally and gives every already-terminated reason two of them - which no revert of this
change can produce and which would therefore go unguarded if that test were not written separately. It
asserts `toBe` on the whole string rather than `toContain`, for the same reason.

**No screenshot of this defect is committed, and that is deliberate.** The picture this proof first
took of it was produced by a reason the route does not send, so it was deleted rather than kept with a
caption. Of the failures this screen can actually produce, none carries an unterminated reason today.
**The fix stands on the code citation above and on the tests, not on a picture** - see the README's
section 7a.


---

# Addendum: revert G - the Add note's path comparison

Prediction committed in `aaf906726`, before this revert was run.

**Why there is a seventh.** A Reviewer found the Add note's "is it in the list?" check folding CASE
ONLY, where both stores this mission joins compare paths by `KnownRepositoryStore.NormalizePathKey`.
This is the mission's own first defect - deciding path identity by a rule that is not the path's own
shape - arriving in this phase's code, and neither the Tech Lead nor I saw it.

**Result: 2 failed, 487 passed** in the Cockpit; the other 55 test files green. Exactly the two
predicted, with the predicted symptoms:

```
× decides path identity by the Gateway's own rule, and takes case from the path's own shape
  → expected false to be true          (same("/work/atlas", "/work/atlas/"), the first clause it reaches)
× matches the added path against the list by that rule, not by how it happens to be spelled
  → Unable to find an element with the text:
    Added atlas to this machine. It is in the list above.
```

**The second failure's own page dump is the defect, in one frame.** The rendered note read:

```
Added atlas to this machine. The list above is the one the Gateway serves, and it does not show this path.
```

while `c:/repos/atlas` was sitting in the rendered table 120 lines above it in the same dump. **The
screen telling the owner something the screen itself disproves** - which is the failure mode Critical
Rule 7 exists to stop, reached here not by a client re-deriving a verdict but by a client comparing two
paths with a rule of its own invention.

**Every other Add test stayed green, exactly as predicted**, including
`says a repository the machine already had was already there...` which exercises the very same
comparison. All of them use a path spelled identically on both sides, so a case-only fold is a no-op
over them. **The two tests above are the only thing between this screen and the defect, and both had to
be built with paths spelled DIFFERENTLY on the two sides.** That is the ordering fixture's lesson in a
different costume, and it is now the third time this phase has met it:

| Where | The fixture that could not catch it | The fixture that could |
|---|---|---|
| The order (revert C) | A realistic list already in recency order | Never-opened rows interleaved ABOVE used ones, out of recency order |
| The verdict (revert D) | A row where the stamp and the inference agree | Two rows where they disagree, in opposite directions |
| The path rule (revert G) | A path spelled the same on both sides | A path with the other separator, a trailing slash and different case |

In every one of the three, the comforting fixture passes straight through the defect.