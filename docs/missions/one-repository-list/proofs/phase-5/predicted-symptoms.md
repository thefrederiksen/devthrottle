# Phase 5 - what I predict each revert will do, written before any of them runs

Mission: One repository list, held on the Gateway. Phase 5: *the phone's repository step takes the one
ordered list. No other change to that screen.*

This file exists so the reverts below cannot be rationalised after the fact. It is committed on its own,
**before** a single revert is applied, so its position in the history is the evidence that the prediction
came first. Phases 2 and 3 did the same and it is what separates a proof from a story told afterwards.

The code at the time of writing is commit `700d4df5a` ("The phone's repository step takes the one ordered
list").

Each revert names one load-bearing thing, says what it would look like to the owner if it were wrong, says
exactly which tests I expect to go red and with what symptom, and says what I expect to stay green. **A
revert whose tests stay green is a proof that does not cover what I changed, and I will say so rather than
quietly move on.**

---

## Revert A - put the sort back in `client-core` `getKnownRepositories`

Restore `list.sort((left, right) => right.lastUsed.localeCompare(left.lastUsed));` at the end of
`getKnownRepositories`, exactly as `origin/main` has it today.

**What it would mean for the owner.** The Gateway decides one order for his machine and the phone quietly
decides another on the way in. Today those two orders agree, which is exactly what makes this dangerous:
the defect ships invisible and surfaces the first time the Gateway's order changes for a reason the client
does not know about - a new kind of row, a second sort key, a tie broken differently.

**I predict red:**

- `packages/client-core/src/api/newSession.test.ts` →
  *"returns the Gateway's order verbatim, even when it is not the order a client would have chosen"*.
  It serves `Oldest, Newest, Middle` - an order no descending sort produces - and expects them back in
  that order. With the sort restored the reader answers `Newest, Middle, Oldest`.
- `apps/mobile/src/pages/NewSession.test.tsx` → nothing. The phone's tests mock the client module, so
  they never run this function. **I expect the phone's suite to stay entirely green on this revert**, and
  that is worth recording rather than hiding: the phone's own tests cannot see this defect, which is why
  the client-core test above had to exist.

**I predict green:** everything else, including all four .NET suites - no .NET code is involved.

## Revert B - put the merge and the re-sort back on the phone

Restore `mergeRepositories` and `repositoryKey` in `apps/mobile/src/pages/NewSession.tsx`, feed the step
from `getRepos` and `getKnownRepositories` again, and take the default five from
`mergeRepositories(recentRepositories)` as `origin/main` does.

**What it would mean for the owner.** The phone's default list is the Director's registry, not the
Gateway's list: a repository he worked in from the Cockpit is missing from the top of the phone's list,
and a repository nobody has ever opened never appears at all. This is the state the before screenshot
shows.

**I predict red:**

- `NewSession.test.tsx` → *"shows the Gateway's one list with the never-opened repositories at the bottom"*.
  The mocked `getRepos` returns one repository, `Registry only`, so with the merge back the default five
  become the merged list and the assertion on the five names fails. I expect `Registry only` to appear in
  the rendered order.
- `NewSession.test.tsx` → *"renders the Gateway's order verbatim, even an order no client sort would
  produce"*. The served order is deliberately not a sorted one, so the merge's sort reorders it to
  `Newest, Oldest, Middle, Kilo, Zulu`.
- `NewSession.test.tsx` → *"reads one route for repositories and never the Director's own registry"*.
  `expect(api.getRepos).not.toHaveBeenCalled()` fails, and `Registry only` is found on screen.
- `NewSession.test.tsx` → *"keeps the Gateway's order through a search"* - the search runs over the merged
  and re-sorted list, so `Match newest` comes out above `Match oldest`.
- `NewSession.test.tsx` → *"says the repository list could not be loaded and still accepts a typed path"*.
  With `getRepos` succeeding, rows are on screen although the one list failed, so the assertion that no
  repository row is offered fails. I am less certain of this one than of the four above, because it also
  depends on the error wording I changed; if it goes red for the wording rather than for the rows I will
  say so.

**I predict green:** `packages/client-core`, the Cockpit, cc-assistant, and all four .NET suites.

## Revert C - take the default five from the bottom of the list instead of the top

Change `allRepositories.slice(0, TOP_REPOSITORY_COUNT)` to `allRepositories.slice(-TOP_REPOSITORY_COUNT)`.

**Why this one.** Reverts A and B both attack the ORDER. This one leaves the order alone and attacks the
CHOICE OF ROWS, which is the other half of what the owner sees, and it is the failure a test that only
compared sets rather than sequences would sail straight past.

**What it would mean for the owner.** He opens the phone and the five repositories offered are the five he
is least likely to want - the never-opened tail - while the one he was working in ten minutes ago is not
on screen at all.

**I predict red:**

- `NewSession.test.tsx` → *"shows the Gateway's one list with the never-opened repositories at the bottom"*
  fails only if the list is longer than five. My mixed list is exactly five, so **this test would pass**,
  and I am saying so before running it because that is the interesting part of this revert.
- `NewSession.test.tsx` → *"searches beyond the top five repositories and creates only after explicit
  review"*. That one serves eight repositories and asserts `Repository 1` is on screen and `Repository 8`
  is not. With the slice taken from the end, `Repository 8` is on screen and `Repository 1` is not, so it
  fails on the first assertion.
- `NewSession.test.tsx` → *"renders the Gateway's order verbatim…"* passes, because that list is five long.

**I predict green:** everything else.

## Revert D - undo the folder-name fix in `SmartShutdownSessionReader`

Put `Path.GetFileName(session.RepoPath.TrimEnd('\\', '/'))` back.

**Why this one is in the list.** It is not the phone, but it is a real product change made in this pull
request, and a change nobody watched fail is decoration however small it is.

**What it would mean for the owner.** On a Mac or a Linux Director, the Smart shutdown dialog names a
session by its whole path instead of by its repository, in the list of what is about to be shut down.

**I predict red:** `CcDirector.Avalonia.Tests` →
`SmartShutdownSessionReaderTests.Read_NamedAndUnnamedSessions_UseTheNameTheRailShows` (the one that is
already red on `origin/main`) and the new
`Read_UnnamedSessions_ReadTheFolderNameFromThePathWhicheverMachineWroteIt`. Both with
`Expected: "devthrottle" / Actual: "D:\ReposFred\devthrottle"` on this machine.

**I predict green:** every web suite, and the other three .NET suites.

---

## What no revert here can prove

- **Nothing on a real phone.** The screenshots are the real phone application in a real browser at a real
  phone viewport, driven against a stand-in Gateway. No iOS device and no real Gateway were involved.
- **Nothing about the Gateway's order itself.** Phase 3 owns that and proved it. Everything here assumes
  the served array is right and tests only that the phone does not touch it.
- **Nothing about a repository the Gateway's catalogue does not hold.** That gap is real, it is named in
  the README, and no test here would notice it, because no test here can tell an empty catalogue from a
  complete one.
