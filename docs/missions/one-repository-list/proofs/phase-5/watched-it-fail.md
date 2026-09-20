# Phase 5 - watching the proof fail

Every revert below was run **after** `predicted-symptoms.md` was committed on its own, before any
revert ran, and after the code it attacks was committed. The predictions were not edited afterwards;
where reality differed from them, this file says so rather than the prediction being tidied up.

**On the hashes, because phase 3's reviewer caught a stale one and it is worth not repeating.** The
branch has been rebased twice since those commits were written, so the hashes they cite are pre-rebase
and no longer resolve. On the final base `f20bbd33b` the three commits are `cac4fac21` (the code),
`549885154` (the predictions) and `407059ea4` (this proof). **What carries the evidence is the ORDER,
not the hash**: the predictions are a commit of their own that contains nothing else, and it sits
between the code and this file in every version of the history. `predicted-symptoms.md` has not been
edited since it was committed - including its own now-stale hash, which is left exactly as written
rather than quietly corrected afterwards.

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, Node v26.9.0, `dotnet` 10.0.301
at `~/.dotnet`.

**Two of these five reverts attack code this branch no longer carries** - `main` moved four times while
this phase was built, and the Delivery Lead ruled that the shared reader and the folder-name defect each
land as their own pull request ahead of this one. Those runs are kept rather than deleted: they were
made against this branch's own working tree at the time, with the same defect in the same place, and the
evidence belongs to whoever owns the fix now. Each one says plainly whose code it is. **Reverts B, B2
and C are this branch's, and they are the ones that prove the phone.**

---

## Revert A - the sort back in `client-core` `getKnownRepositories` - NOT this branch's any more

**Whose it is now.** The shared reader landed on its own as `828a182b3`, with a guard of its own
(*"serves the Gateway's order untouched and never re-sorts on lastUsed"*), and this branch rebased onto
it and dropped its version. This run was made before that, against the equivalent change built here.

**Applied:** `.sort((left, right) => right.lastUsed.localeCompare(left.lastUsed))` restored at the end
of `getKnownRepositories`, which is what `origin/main` has today.

| | |
|---|---|
| **Predicted red** | `newSession.test.ts` → *"returns the Gateway's order verbatim, even when it is not the order a client would have chosen"* |
| **Observed** | **Exactly that test, and only that test.** `AssertionError: expected [ 'Newest', 'Middle', 'Oldest' ] to deeply equal [ 'Oldest', 'Newest', 'Middle' ]` |
| **Counts** | client-core **1 failed, 1454 passed** (123 files, 1 failed) |
| **Predicted green** | the phone's own suite, because it mocks the client module |
| **Observed** | **`apps/mobile`: 19 files, 107 passed, 0 failed.** The prediction holds, and it is the uncomfortable half: the phone's 107 tests cannot see this defect at all. That is precisely why the client-core test had to be written rather than assumed covered. |
| **Restored** | client-core back to **1455 passed, 0 failed** |

## Revert B - the whole pre-phase-5 screen back, merge and all

**Applied:** `git checkout origin/main -- apps/mobile/src/pages/NewSession.tsx` - the entire
`origin/main` screen, with `mergeRepositories`, `repositoryKey`, the second route and the old wording.

**Observed: 12 failed, 95 passed.** I predicted five by name. All five went red with the symptom
predicted:

| Predicted | Observed symptom |
|---|---|
| *"shows the Gateway's one list with the never-opened repositories at the bottom"* | `expected [ 'Registry only' ] to have a length of 5 but got 1` - **and `Registry only` is what is on screen.** That is the mocked Director registry row, which the Gateway's catalogue does not hold. With no search typed, the phone showed the registry's one row and none of the Gateway's five. |
| *"renders the Gateway's order verbatim…"* | the same, `[ 'Registry only' ]` |
| *"reads one route for repositories and never the Director's own registry"* | `expected "spy" to not be called at all, but actually been called 1 times` |
| *"keeps the Gateway's order through a search"* | the two three-element arrays differ |
| *"says the repository list could not be loaded and still accepts a typed path"* | `expected 'Repository history could not be loade…' to contain 'The repository list could not be load…'` - **it failed for the wording, which is the uncertainty the prediction flagged in advance, not for the rows.** |

**I under-counted, and the reason is mine.** The prediction described revert B as *"restore
`mergeRepositories` and `repositoryKey`"* but I executed it as *"restore the whole file"*, which also
restores the error wording and the loading and empty-state conditions. Seven further tests went red on
those - the loading sentence, the empty-machine sentence, the search cap note, the case-distinct paths,
the in-flight guard, the eight-repository search, and the retry. None of that is evidence about the
ordering claim, so it is noise around the finding rather than part of it.

So the revert was run again, surgically.

### Revert B2 - only the deleted client sort, with every word on the screen left alone

**Applied:** the phase 5 file, unchanged, except that `allRepositories` re-sorts the Gateway's list with
the exact comparator `mergeRepositories` used - last-used descending, then `repositoryLabel` under
`localeCompare`.

| | |
|---|---|
| **Observed** | **2 failed, 105 passed** |
| | *"renders the Gateway's order verbatim, even an order no client sort would produce"* → `expected [ 'Newest', 'Middle', 'Oldest', …(2) ] to deeply equal [ 'Oldest', 'Zulu', 'Newest', …(2) ]` |
| | *"keeps the Gateway's order through a search"* → the filtered rows come back re-ordered |

**And the one that matters most for the next reader: *"shows the Gateway's one list with the
never-opened repositories at the bottom"* PASSED under this revert.** The realistic mission order is
also the order that comparator produces, so the owner-facing test cannot catch a client re-sort on its
own. That is not a weakness to be quiet about - it is the whole reason the adversarial test beside it
exists, and it is the same trap phase 3 recorded when its permutation test needed a final assertion on
the mission's order rather than merely a stable one.

## Revert C - the default five taken from the bottom of the list instead of the top

**Applied:** `allRepositories.slice(0, TOP_REPOSITORY_COUNT)` → `allRepositories.slice(-TOP_REPOSITORY_COUNT)`.

| | |
|---|---|
| **Observed** | **2 failed, 105 passed** |
| **Predicted** | *"searches beyond the top five repositories…"* fails on `Repository 1` not being on screen |
| **Observed** | exactly that: `Unable to find an accessible element with the role "button" and name "Select repository Repository 1"` |
| **Also predicted** | that *"shows the Gateway's one list…"* would **pass**, because its list is exactly five long and slicing five from either end of five is the same five |
| **Observed** | it passed. The prediction was right, and the honest reading is that the mixed-list test proves the ORDER and says nothing about WHICH rows the default view picks. |
| **Not predicted** | *"uses an immediate in-flight guard so rapid confirmation taps create one session"* also went red, for the same reason - it clicks `Repository 1`. One mechanism, two tests. |

## Revert D - the folder-name fix undone - NOT this branch's any more

**Whose it is now.** A dedicated seat owned this defect; it landed as `f20bbd33b` and reached this branch
through `main`. This run was made before the hand-over, against the equivalent fix built here. It is
kept because the symptom it captured is evidence about a real product defect on this platform, and the
seat that owns the fix should have it.

**Applied:** `RepositoryPaths.FolderName(session.RepoPath)` → `Path.GetFileName(session.RepoPath.TrimEnd('\\', '/'))`.

| | |
|---|---|
| **Observed** | `CcDirector.Avalonia.Tests`, the reader's own class: **2 failed, 5 passed** |
| | `Read_NamedAndUnnamedSessions_UseTheNameTheRailShows` - `Expected: "devthrottle" / Actual: "D:\\ReposFred\\devthrottle"` |
| | `Read_UnnamedSessions_ReadTheFolderNameFromThePathWhicheverMachineWroteIt` - the same |
| **Predicted** | both, with that exact symptom |
| **Restored** | the class back to **7 passed, 0 failed** |

The first of those two is the test that was **already red on `origin/main`** when this phase started;
the second is new here and pins both separators, so the fix cannot quietly rot back on a machine where
`Path.GetFileName` happens to agree.

---

## The state after every revert was restored

`git diff HEAD` is empty - the working tree is the committed code, not a repaired copy of it - and the
whole mission check was run again on it, and then a THIRD time on the final base `f20bbd33b` after the
second rebase, because counts from a base that no longer exists are not counts for this branch. They are
in `README.md`, section 6: **zero failures in all five commands.**
