# Phase 5 review - the phone reads the same list

**Reviewer seat:** opened by the Delivery Lead for phase 5, run under the mission workflow's Reviewer
conduct (DevThrottle Method, section 3). A different agent family from the seat that wrote the code.

**Scope of this review:** the diff `origin/main...origin/mission/one-repo-list-phase-5` (3 commits,
11 files), the phase's four proof documents and both committed proof scripts, the mission document,
the DevThrottle Method, the phase 3 review, `docs/CodingStyle.md` and `docs/VisualStyle.md`
(skimmed for the rules this diff touches - testing standards, and the mobile shell's style sheet
locations), and the repository `CLAUDE.md`. What I ran is listed below. What I could NOT reach:
**the four screenshots as images** - my model cannot read pictures. I compensated by re-running the
committed screenshot driver myself and reading its printed evidence (the on-screen rows, the stub
Gateway's route log, and the repository step's rendered text extracted from the page), so every
factual claim the pictures make is verified from the running application; what I cannot certify is
that the pixels are laid out identically, which I instead verified from the diff: no style sheet
was touched and every element keeps its existing class names.

---

## Verdict: no blocking findings. The phase is what it claims to be.

## 1. Is every client ruling gone? Yes - all three, deleted rather than weakened.

I read the whole of `apps/mobile/src/pages/NewSession.tsx` on the branch, not just the diff hunks.

- **`mergeRepositories` is deleted**, not refactored: the comparator (last-used descending, then
  label under `localeCompare`) is gone, and the merge state, its error, its route and its banner are
  gone with it. `allRepositories` is `knownRepositories ?? []` - the served array, nothing else.
- **`repositoryKey` is deleted**, not moved. The React row key is the path itself. No de-duplication
  of any kind remains on the screen.
- **The `neverOpened` verdict is no longer dropped.** The old merge rebuilt each row as a bare
  `{ name, path, lastUsed }`; the rows now travel through untouched, typed `RepoInfo`, which the
  shared reader's `KnownRepoInfo` satisfies.
- **The second route is gone from that screen.** `getRepos` is no longer imported by the phone's New
  Session page at all (it remains in `client-core` for the Cockpit, which is phase 4's scope, and in
  the Cockpit's own pages - verified by grepping every caller).

Nothing else on the screen re-orders, re-ranks or re-deduplicates: the default view is
`slice(0, 5)` - a length, not a ruling - the search is a `filter` (and searching was always this
step's own job; the filter preserves order), `repositoryLabel` is display-only, and the Director and
agent steps render their arrays as served. The reader beneath the screen,
`client-core getKnownRepositories`, does not sort - I read it on the branch.

**The guard, and the lesson from this mission, applied rather than assumed.** The brief warned that a
revert proves the guard catches THAT defect, not the class its name claims. The author's own revert
(B2) restored the descending comparator. I attacked the guard with **two different wrong rules it
was never reverted with**:

- An **ascending** sort on `lastUsed` in the screen: **5 of 17 tests fail**, among them the
  adversarial order test, with the never-opened rows jumping to the top - the visible symptom.
- A **plausible half-correction** - never-opened rows moved to the bottom, sorted by name, the kind
  of "fix" a later developer might add in good faith: **exactly 1 of 17 fails, and it is the
  adversarial test** (*"renders the Gateway's order verbatim, even an order no client sort would
  produce"*), and nothing else. Sixteen tests pass under that rule, including the realistic
  mission-order test. The guard is not decoration; it is the *only* thing holding that class down,
  and its deliberately interleaved fixture (a never-opened row above used ones, and a second below)
  is what makes it so. Both rules restored, the suite is green again (17 of 17).

## 2. Did the screen's shape survive? Yes.

From the diff: **no style sheet is touched** (`apps/mobile/src/styles.css`, where the visual guide
says the phone's styles live, is not in the diff), and every element keeps its existing class names
(`newsession-recent-note`, `banner banner-error newsession-source-error`, `newsession-retry
standalone`, `roster newsession-choice-list newsession-repository-list`, `picker-link`, `row-body`,
`row-name`, `row-context newsession-path`, and the manual-path and review sections unchanged). Same
four steps, same search box with the same label and placeholder, same row layout, same manual path
panel, same review section, same footer. Two visible sentences changed and the author discloses both
in the proof's own table; both had stopped being true ("up to five most recently used" over a list
whose bottom rows were never used; two failure banners for what is now one list), and the wording
changes are inside the existing note and banner elements. I also corrected for the empty-state
behaviour added while photographing the failure (suppressing the note and the empty-machine sentence
while the list is in error): that is a sentence being withheld when the screen does not know it to
be true, which is Critical Rule 7 in the right direction - not a redesign. What I could not do is
look at the pictures; see scope above for how the content claims were verified instead.

## 3. The route decision and its cost. Right, and not patched around.

The reasoning is sound and matches what the code now says: a merge of two sources cannot avoid
ruling, so the second route had to leave, not be fixed. I checked for a fallback and there is none:

- `getRepos` is not imported by the screen at all, so there is no code path to fall back *to*.
- The failure test asserts the opposite of a rescue: when the one list fails, **zero** repository
  rows are offered, alongside a retry and the manual path that was always there.
- The failure screenshot run confirms it in the running application: with the route answering 503,
  the repository step renders exactly one honest sentence, a retry, and the manual path entry - no
  rows, no second source.

**The known cost is stated plainly, in capital letters, in the proof's own "what this does NOT
cover" section**, with the exact case named (a hand-registered repository, never opened since the
Gateway started recording, not under a registered root folder), with the reason (the Director pushes
its root-folder scan and never pushes its registry), and with the hand-over to the seat closing it.
The proof nowhere implies the phone now shows everything; it says the catalogue's completeness is
now the phone's completeness. That is the honest sentence.

## 4. The proof. Verified in the history, and re-run myself.

**The order of the evidence holds in the branch's history.** The code commit is
`7d50e108c` (03:01:38), the predictions commit `9ac60f634` (03:02:20) contains **only**
`predicted-symptoms.md` (128 lines, nothing else), and the proof commit `fb689a51a` (03:18:33)
carries the observations. `predicted-symptoms.md` has not been edited since it was committed - I
checked the file's whole history on the branch. The predictions genuinely precede the observations.

**The failure case is caused on purpose, not photographed by luck.** `FAIL_KNOWN=1` in the committed
stub script deterministically answers 503 on the one route; I re-ran it and extracted the rendered
text of the repository step myself.

**I re-ran the proof driver end to end, with the committed scripts, in a real browser at a phone
viewport** (I could not read the committed pictures, so I made the application tell me what they
show):

| Run | Evidence |
|---|---|
| Default view, stand-in Gateway up | Rows on screen, top to bottom: `devthrottle, devthrottle-internal, mindzie-studio, atlas-reporting, zephyr-tools` - the Gateway's order, never-opened last. **The stub Gateway's log shows `known-repositories` was called and `/repos` never was.** |
| Search `zephyr` | One row: `zephyr-tools` - a never-opened repository is reachable by search (goal 2 on the phone). |
| `FAIL_KNOWN=1` | The repository step's rendered text is exactly: the search box, "The repository list could not be loaded: …", "Retry the repository list", "Enter a path manually". No rows, no top-five note, no empty-machine sentence. |

I did not re-run the "before" screenshot; I verified its claim by reading `origin/main`'s own merge
logic against the stub's registry fixture, which yields exactly the three rows the picture claims
(`devthrottle`, `mindzie-studio`, `old-experiment`, in that order, with the second most recently
used repository on the machine absent and both never-opened repositories absent).

## What I ran (this worktree, macOS, Apple silicon, Node v26.9.0)

| Run | Result |
|---|---|
| `npm run typecheck` | **Green**, all four workspaces |
| `npm test --workspaces --if-present` | **Green: 2,126 passed, 0 failed, 0 SKIPPED** - client-core 1,456, cc-assistant 106, cockpit 457, mobile 107. Matches the author's counts exactly. There are no skipped tests to report loudly because there are none. |
| Ascending re-sort applied by me to the screen's list | **5 failed, 12 passed** - the guard catches a rule the author never reverted |
| Never-opened-to-bottom-by-name rule applied by me | **1 failed, 16 passed** - only the adversarial guard catches it; the guard is load-bearing |
| Both restored, phone suite re-run | **17 passed, 0 failed** |
| The committed screenshot driver, three runs (default, search, forced failure) | Evidence above |
| `git diff HEAD` after all of it | **Empty** - the working tree is the committed code |

**Not run, and said here plainly:** the three .NET suites from the mission check. This diff contains
no .NET code whatsoever (its file list is two TypeScript files and documents), and the author's .NET
counts are self-testimony I did not re-derive - the brief scoped my run to the two Node commands
and told me not to re-derive the author's experiments. Reverts B, C and D as the author ran them I
also take on the author's record; my own revert work attacked the guard from directions the author
did not, which is the stronger question.

## Minor observations, none blocking

1. **The branch is one commit behind `origin/main`.** Main has `ee2b8666e` (the shared reader's
   guard given a fixture no rule over `lastUsed` leaves alone, pull request #3196), which this branch
   predates. The two touch different files, so no conflict is expected; the rebase the merge plan
   requires will give the phone's *reader* the strengthened fixture for free. No action for this
   phase beyond the ordinary rebase before the pull request.
2. **The hashes cited in `watched-it-fail.md` are stale relative to the branch it sits on** (it
   names `cac4fac21`/`549885154`/`407059ea4`; the branch's third rebase left
   `7d50e108c`/`9ac60f634`/`fb689a51a`). The document says in advance that the order is the
   evidence, not the hash, and I verified the order independently. The same nit phase 3's reviewer
   recorded; the same "not worth a round trip" verdict.
3. **The phone's suite cannot see a re-sort inside `client-core`**, because it mocks the client
   module - the author proved that himself in revert A and said so. The protection for the reader
   is the client-core guard test, which the rebase (observation 1) strengthens. Nothing to fix here;
   it is written down so nobody mistakes the phone's 17 tests for covering the reader.

---

**Summary for the Delivery Lead.** All three client rulings are gone - deleted, not weakened - and
the screen keeps its shape: no style sheet touched, same elements, same classes, two sentences
changed and disclosed. The second route left rather than being fixed, there is no fallback anywhere
in the screen or its tests, and the known cost is stated in the proof in capital letters. The
evidence order in the history is genuine, and I attacked the order guard with two wrong rules the
author never tried - both caught, one of them by the adversarial test alone, which is the strongest
evidence a guard can have. My one scope limit is that I cannot read the committed images; their
content is verified from the running application instead. Nothing blocks the pull request.
