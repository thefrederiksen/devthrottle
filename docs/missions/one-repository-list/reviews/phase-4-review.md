# Review: phase 4 - the Cockpit's New Session tab

**Reviewed:** branch `mission/one-repo-list-phase-4`, tip `c2ef464a4`. The branch carries EIGHT commits
of its own on its merge base with main (`f20bd33b`): two code commits (`812655b59`, `91490b75b`) and six
proof commits. The brief that opened this seat said six; the tip it named is the tip reviewed here, and
all six proof commits it named by hash are present. Nothing else on the branch is from another hand.
**Reviewer seat:** Worker on the mission workflow, a different agent family from the seat that wrote the
code. I read; I never built, and I never changed a line of the work under review. The three revert
experiments below were run in this review's own worktree (`/tmp/p4-review`, cut from the branch tip
detached, never the shared checkout), each restored afterwards - the tree is clean at this commit.
**Date:** 20 September 2026.

**Verdict: the work is sound. Two findings follow - one on the proof's account of a failure case, one
minor on the screen's Add note - and neither is a defect in the ordering, which is the mission's whole
reason for existing and which I could not break.** Everything else I went at held up.

---

## Scope

### What I read

The full branch diff against its merge base (26 files, 2,337 insertions). In full:
`apps/cockpit/src/sessions/NewSessionDialog.tsx`, `newSessionDialogOneList.test.tsx`,
`packages/client-core/src/api/client.ts` (the `addRepo` reader, `withRetryHint`, and the
`getKnownRepositories` reader it sits beside), the `errorReporting.test.ts` additions, the whole
`styles.css` diff, the two test files whose mocks gained `addRepo`, the phase proof
(`proofs/phase-4/README.md`, `predicted-symptoms.md`, `watched-it-fail.md`, and the three staging
files), the mission document, the repository `CLAUDE.md` (Critical Rule 7 above all),
`docs/CodingStyle.md`, and the opening block of `docs/VisualStyle.md`.

Against `origin/main`, not the working tree, I read the ground the proof's claims rest on:
`GatewayEndpoints.cs` (the `known-repositories` route, the `POST /directors/{id}/repos` route,
`TunnelFailure` and `MapDirectorFailure`, `TryResolveOwnedDirector`),
`SessionWriteOrphanContracts.cs` and `SessionWriteExecutor.cs` (the `RepoAddResponse` shape and the
repo-add verb), `ControlEndpoints.NormalizeRepoPath`, and `KnownRepositoryStore.cs` (the ordering, the
`NormalizePathKey`, and what the route serves).

### What I ran, and its results

1. `npm run typecheck` - **clean**, all four workspaces.
2. `npm test --workspaces --if-present` - **2,153 passed, 0 failed**: client-core 1,459, cc-assistant
   106, cockpit 487, mobile 101. Identical, to the test, to the Tech Lead's and the author's
   independent measurements, on a machine at load average 3.6 to 5.9 at the time of the runs.
3. **Revert C, re-run by me, exactly as the author describes it** (the Last Used branch replaced with a
   newest-first, missing-times-last comparator): **exactly the five named tests fail, 25 pass** in the
   screen's file. Matches the author's record.
4. **Revert C, run a SECOND way, which the author did not run**: the same branch replaced with
   nulls-FIRST-then-newest - the PostgreSQL null-ordering behaviour phase 3 documented as this
   mission's trap. **Nine tests fail**, including all five the author's own revert fails, and including
   the realistic-fixture test that the author's comparator left green. The guard catches the class, not
   only the comparator the author happened to remove.
5. **Revert F, re-run by me** (the two terminating lines removed from `withRetryHint`): **exactly the
   two named tests fail**, with the predicted messages, in `errorReporting.test.ts`; the third, guarding
   the opposite mistake, stays green. Matches the author's record.

Reverts A, B, D and E I did NOT re-run. I take them on the author's evidence, having read their
transcripts against the code and the fixtures: the assertions they credit are present in the test file
(including the "nothing has started yet" line revert B says was added while writing the predictions,
which is there, in the test, before the Create session click).

### What I could not reach

- **The three .NET suites. Not run, deliberately.** The branch's own commits contain zero C-sharp files
  (verified: `git diff --name-only` from the merge base to the tip contains none), so the .NET suites
  test nothing this branch touched; the Tech Lead and the author measured them green independently on
  this branch's base (6,547 / 4,485 / 646, all zero failed), before the TypeScript-only commits landed.
- **The committed screenshots. I could not view the images** - this session cannot render them. I
  verified they exist as real non-empty pictures of the claimed viewport (2664 by 1260, which is
  1440 by 1040 at device scale 2, as the report states), and that the staging that produced them is
  committed and coherent with every scenario the report describes. **What the pictures show I take on
  the author's evidence** - except where the report quotes their text, and those quotations are all of
  screen behaviour I verified independently through the tests I ran.
- **A live run of the staging.** I did not re-drive the browser or take fresh shots.
- **The three lint errors the brief excluded.** Not examined, per the brief.

---

## The five things I was asked to go at

### 1. The ordering, and whether the client rules

**Greped, not believed.** `lastUsed` appears in `NewSessionDialog.tsx` in exactly two places outside
comments: `lastUsedAgo`, which words the cell, and `lastUsedCell`, which reads the served verdict.
Neither is on any path that decides order. `orderRepositories` is the whole of the ordering: the
Last Used descending branch returns the served array itself, the ascending branch is that array
reversed with no key and no null policy, and Name and Path are case-insensitive string comparisons
with a path tie-break on Name. The one sort call in the file is the Name/Path branch. No date parsing
reaches the ordering. Critical Rule 7 is satisfied: this screen renders the Gateway's ruling and never
re-derives it.

**The pin, and whether a wrong implementation could satisfy it.** The `toBe` assertion - the same
array, not an equal one - is satisfied by one wrong implementation: an in-place `served.sort(...)` with
a comparator that happens to be a no-op returns the same reference and passes. But the report never
claims the pin alone is the guard; it says "the guard is the fixture, not the assertion", and the two
together close the hole: the rendered test over the adversarial fixture fails under any order-changing
rule (I confirmed this myself twice, below), and the pin fails under any array-returning
implementation. A no-op in-place sort that passes both is, behaviourally, "render exactly what
arrived" - which is the requirement.

**The third blind spot. I looked for one and could not construct it.** The four-row fixture is
timeless, used, timeless, used. Every partition-based comparator - nulls first, nulls last, newest
first, oldest first - must place the two timeless rows adjacent to each other; in the served array
they are not adjacent, so no such rule can reproduce it. By-name and by-path rules disagree with it
outright. I additionally ran the one rule the author did not - nulls-first-then-newest, the PostgreSQL
behaviour - and nine tests fail, more than the author's own five. **Every wrong rule I could reach for
has to move at least one row, and does.**

### 2. The general lesson, applied: which reverts I re-ran

**Re-ran myself: revert C, a second variant of revert C of my own construction, and revert F.** These
are the two that carry the weight - revert C because the ordering is the mission's point, and revert F
because its guard's name claims a class ("terminates the reason") that its revert cannot fully
exercise, the exact trap this lesson names. All three reproduced the recorded results exactly.

**Took on the author's evidence: reverts A, B, D and E**, as stated in the scope. The parts of their
transcripts that are checkable without running them check out: the assertions exist, the predicted
counts are consistent with the fixtures, and the honest note in revert A (two tests staying green that
the author had not predicted - an empty list from the wrong route being indistinguishable on the page
from an empty list from the right one) is the sound of a record telling the truth rather than
decorating itself.

**Commit ordering, verified rather than taken:** the predictions for reverts A to E are in `b45df8fd4`
at 06:37:14, their results in `305f0bbc7` at 06:40:48; the prediction for revert F is in `c610f5c75` at
07:08:22, its result in `c2ef464a4` at 07:11:11. Both prediction commits precede the commits recording
their runs.

**One thing the record does not spell out, and I confirmed it is benign:** the "draws no table when the
read failed" behaviour and its test landed in `d113f5d07`, AFTER the five reverts ran - so the author's
revert runs were against a file one test short of the final one (486 tests then, 487 now; the numbers
in the two documents are both correct for their moments). My own revert C run was against the final
tip and still produced exactly the author's five failures, so the ordering guard holds for the code
actually being reviewed.

### 3. The style rule most likely broken without anyone noticing

**No hex value, no rgb, no hsl anywhere in the added stylesheet** - verified by grep over the whole
added-line diff, not by reading. Every colour the new rules set is an existing Cockpit token
(`--surface-2`, `--text`, `--text-dim`, `--border`, `--accent`) or `transparent`. The Avalonia palette
was not ported.

**`--newsess-repo-cols`, and the Tech Lead's reasoning about it: the reasoning holds.** It is declared
once, on `.newsess-repotable`, and consumed only by rules inside that component - the heading row and
the repository rows, which must share one column track. It is a grid-template value local to one
component subtree, not a named design token; a token would live in `:root` and be shared across
components, and this is neither. No token was invented. (One nit, not a finding: `.newsess-pick` reads
it with a `1fr` fallback, which would silently collapse the row to one column if the row were ever
rendered outside the table. Today it never is.)

**Nothing else hard-codes a colour.** The scrollbar, the headings, the empty state, the machine chips,
the Last Used cell: all tokens.

### 4. The proof itself

The staging is committed and coherent; the scenarios in `set_scenario.py` produce every state the
report claims to photograph, including the 502 and both Add outcomes, and `stub_gateway.py` serves
exactly the four routes the screen reads. The report's honesty habits are real: the staging is declared
loudly, the two wrong predictions are recorded rather than tidied, and section 9 says what the run does
not cover - including the forward-compatibility claim being reasoned rather than demonstrated, which
is correctly flagged as undemonstrable until the other seat's feed lands.

**But one claim in the proof overreaches, and it is finding 1 below.** Section 9's discipline is
genuine everywhere else; this is the one place a claim is asserted as a product fact that the product
contradicts.

### 5. Ordinary defects

None found beyond finding 2, which is minor. Specifically checked and sound:

- **The click selects.** `selectRepository` writes the path into the one place a chosen path lives;
nothing in the row calls `create`. The footer's Create session button is the only thing that creates,
and the double-click guard is present.
- **The search covers name AND path**, with a fixture in which the two fields disagree about which
  rows match - the only kind of fixture that can tell a two-field search from a one-field one.
- **The empty state, the failed read, and a filter that matched nothing are three different states**,
  each drawn differently, and a filter that matched nothing does not borrow the first-run empty state.
- **The Add wording is forecast-free**, and a test forbids the words that would smuggle a forecast
  back. I also verified against `origin/main` that the 201-versus-200 distinction the wording rests on
  is real: the route answers 201 for newly registered and 200 for already present, both carrying
  `{ added, repo }`, which the reader parses exactly.
- **Null and undefined handling:** the reader coerces every field explicitly and treats a missing
  `neverOpened` as false rather than guessing; `lastUsedCell` renders an unstamped, timeless row as an
  empty cell rather than inventing a verdict; there is not one non-null assertion in the screen file;
  try-catch sits only at boundaries (event callbacks and lifecycle effects), per the coding style
  guide.
- **The error path draws no table** - a failed read draws neither the heading row nor the first-run
  empty state, only the status line saying what the Gateway said.

---

## Findings

### Finding 1 - the proof states, as a fact about the Gateway, a failure cause the real route cannot have

**Where.** `proofs/phase-4/README.md` section 7a, `predicted-symptoms.md` (revert F addendum), and
`watched-it-fail.md` (revert F addendum) all say some version of: *"the Gateway's 502 reason for a
disconnected Director is the PHRASE 'Director not connected'."* Screenshot 09 is captioned *"The
Director is gone and the route answers 502."*

**What the code on `origin/main` says.** Both halves of that sentence are wrong about the product:

1. `GET /directors/{id}/known-repositories` is served **from Gateway storage, not through the Director
   tunnel**. A disconnected Director does not make this route answer 502 - it serves the durable
   catalogue anyway, which is the mission's central design and the reason the catalogue was built
   (mission document, section 5: a pull "returns nothing the moment the Director is unreachable,
   which is exactly when the other two screens still need the list"). The route's real failure
   answers are 403, 404, 409 and 503, each with a full-sentence reason.
2. The string "Director not connected" appears **nowhere** in the product's C-sharp source on
   `origin/main` (verified by grep). The tunnel routes' disconnected-Director reasons are terminated
   sentences: *"The Director on {machine} is not connected right now, so the command was not
   delivered."*

So the malformed line the proof photographed - *"Could not load repositories: Director not connected
Try again."* - was produced by the stand-in's invented reason string, not by anything the real
Gateway writes on this route. The screenshot's caption attributes to the product a state it cannot be
in.

**The harm.** The proof is half the deliverable, and this is the one place it misleads rather than
discloses. A reader - the Delivery Lead, the owner - would take from shot 09 that a disconnected
Director empties the Cockpit's repository list with an error line, which is the exact opposite of the
mission's central claim about this screen: the list SURVIVES the Director going away. And the next
seat who goes auditing "which reasons the Gateway writes without terminal punctuation" (a job section
9 correctly says was never done) will start from an incident that cannot occur and a reason string
that does not exist.

**What is NOT wrong.** The fix itself is sound and worth keeping. The defect class is real in the
product: `TunnelFailure`'s default branch relays Director-sent failure reasons verbatim at status 502
- and the Director writes reasons as phrases (`"session not found"`, `"invalid session id format"`),
which arrive retryable at the client and produce exactly the run-together line. The three tests are
correctly aimed, the revert I re-ran reproduces, and `withRetryHint` should not assume termination
regardless of what any one route writes. Only the PROOF's account of where the incident came from is
false.

**What the finding does not decide:** whether the remedy is to correct the three documents and
re-caption shot 09 (stating the reason was the stand-in's), or to re-stage the failure case with a
status and reason this route can actually produce (503 with "Known repository storage is not
available.", or a 404 for an unknown Director), or both. That is for the seat that built the work.

### Finding 2 (minor) - the Add note can state a falsehood about the list, on paths that differ only in spelling

**Where.** `addRepository`, matching the added path against the re-read catalogue:

```ts
const wanted = (result.path.trim() || path).toLowerCase();
setAddNote(addOutcome(result, list.some((r) => r.path.trim().toLowerCase() === wanted)));
```

The comparison folds case and nothing else. Both stores this mission joins compare repository paths
with more than that, because in this domain raw comparison is unreliable: the Director's own
`ControlEndpoints.NormalizeRepoPath` applies `Path.GetFullPath`, trims trailing separators, and folds
case before comparing; the Gateway's `KnownRepositoryStore.NormalizePathKey` replaces backslashes with
forward slashes and trims trailing separators before keying. The served row carries the path as it was
first observed, not the normalized key.

**The harm.** A path typed as `D:/Repos/my-project` or `D:\Repos\my-project\` - the same repository the
catalogue holds as `D:\Repos\my-project` - registers successfully, then makes the note read *"The list
above is the one the Gateway serves, and it does not show this path"* while a row for that repository
is visible in the table above. It is a wrong sentence, not a wrong action: the Add works, the re-read
happens, and the wording's two-facts shape is untouched. The failure needs the user's spelling to
differ from the catalogue's, which on Windows paths is ordinary.

**Severity: low.** It is one sentence in one outcome, in the case the note exists to be honest about -
which is the very case this screen was redesigned around. The seat that built the work may accept it,
or fold the same normalisation the two stores already use (separator folding and trailing-separator
trim, on both sides of the comparison) into the match. A test for it would type the same path in a
different spelling and expect "It is in the list above."

---

## For the record, not findings

- **The branch is behind `origin/main`.** `origin/main` has moved past the branch's merge base since it
  was cut. The mission's own merge plan requires a rebase before the pull request, so nothing is owed
  here beyond doing that when the pull request opens.
- **The brief that opened this seat said six commits; the branch carries eight** (two code, six
  proof), all accounted for by hash in the brief. The tip is the tip the brief named.
- **The `.newsess-list` scrollbar rules are webkit-only.** The proof says one browser was used, and
  section 9 says so. Not a finding.

**Where the verdict stands.** The screen never re-derives the Gateway's order; the guard is the
adversarial fixture and it holds against a wrong rule the author never tried; the reverts that carry
the weight reproduce when re-run by a different hand; the stylesheet invents nothing; the proof is
honest everywhere except the one failure case named in finding 1; and the screen's own behaviour I
could not break in any way that matters.
