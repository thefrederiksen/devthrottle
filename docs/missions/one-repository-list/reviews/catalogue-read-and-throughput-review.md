# Review: the catalogue read names from the path, and the throughput test measures the parser

**Reviewed:** branch `mission/one-repo-list-green-dotnet`, the two commits after `fe5ceeeb6` (`58d58caa7`,
`4d7de50ce`), which is the head the previous review approved. Nothing before `fe5ceeeb6` is re-opened here.
**Reviewer seat:** Worker on the mission workflow, a different agent family from the seat that wrote the
code. The reviewer never built product code and never changed a line of the work; the one experiment below
ran in a disposable worktree that was removed afterwards, and this branch's tree was untouched until this
review file was committed to it.
**Date:** 20 September 2026.

---

## Scope

### What I read

The two commits' full diff; `CatalogReadExecutor` around both changed lines; the whole of
`CatalogReadRepositoryNameTests.cs` and the rewritten `Parse10MbInChunks_TypicalChunkStaysWithinUiBudget`;
`RepositoryPaths.cs` (already reviewed and approved at `fe5ceeeb6` — not re-reviewed here, only confirmed
as the thing the fix routes through); the mission document; the author's record
(`proofs/green-check-dotnet/catalog-read-and-the-throughput-test.md`); and the previous review
(`reviews/green-check-dotnet-review.md`), so its four product defects and ten test faults are not repeated.

### What I ran, and its results

1. **The three suites, once each, on this branch, on this Mac:**

   | Suite | Result |
   |---|---|
   | `CcDirector.Gateway.UnitTests` | **Failed: 0**, Passed: 6371, Skipped: 8, Total: 6379 |
   | `CcDirector.Core.Tests` | **Failed: 0**, Passed: 4461, Skipped: 18, Total: 4479 |
   | `CcDirector.Avalonia.Tests` | **Failed: 0**, Passed: 550, Skipped: 0, Total: 550 |

   Identical to the author's table, including every skip count.

2. **The new catalogue tests, against the fix reverted.** In a scratch worktree at the branch head I put
   the two `CatalogReadExecutor` lines back to `Path.GetFileName(...)` exactly as they were, changed nothing
   else, and ran the three new tests. Result: both path cases red with the record's exact symptom
   (`Expected: "devthrottle_internal"`, `Actual: "D:\\ReposFred\\devthrottle_internal"`), the stored-name
   case green — 2 failed, 1 passed. So the fix is not a change the new tests cannot fail; the author's
   "watched fail" claim is reproduced, not taken on trust. The worktree was then removed.

3. **The primary throughput assertion's own number, on this machine:**
   361,483,416 bytes allocated for 10,485,760 parsed = **34.474 bytes per byte** (budget 40), fastest chunk
   9.82 ms, median 14.57 ms, slowest 26.86 ms, 0 of 160 over budget. The 34.474 matches the author's three
   measurements (34.472 / 34.475 / 34.474) to the fifth significant figure, on a different day — the
   determinism claim is witnessed, not just read.

4. **The deferred call sites, spot-checked.** Twelve of the seventeen lines the record defers with file and
   line (both tables and both adjacent defects) were opened at exactly those lines: every one holds the
   `Path.GetFileName`-on-a-repository-path shape the record says it holds, including
   `NewSessionDialog.axaml.cs:631`, which names a repository found under a watched root — phase 2's own
   subject, correctly flagged. The record's arithmetic correction of the previous seat's list (ten remain
   of twelve listed, not thirteen, plus seven newly found) is accurate.

### What I could NOT reach, and what I took on the author's evidence

- **Windows.** Same gap as both previous records; nobody has watched any of this go red on Windows. The
  record says so plainly and I confirm it is not softened (below).
- **The author's load experiment** (old test failing at load average 40–52 while the new numbers barely
  moved). Not reproduced — the instruction for this seat was that reading the assertions is enough, and it
  is: the old assertions were three wall-clock statistics, and a wall-clock median over a 100 ms bar
  measuring a ~14 ms parser is a verdict on the machine by construction. Taken on the author's evidence.
- **The deliberate per-character-allocation regression** (new test failed at 56.076 bytes per byte, old test
  passed with median 24.57 ms). Not reproduced. The quoted failure arithmetic checks out
  (587,998,224 / 10,485,760 = 56.08), and the conclusion follows from the assertions I read: a parser that
  allocates per character must multiply the 34.474 figure far past the 40 budget. Taken on the author's
  evidence, and the direction is certain from the assertions themselves.

---

## 1. The catalogue read — the fix is right, and it is the shared rule

- **It uses the shared helper, not a second copy of the rule.** Both empty-name fallbacks (`repos-list` and
  `repos-overview`) now route through `CcDirector.Core.Utilities.RepositoryPaths.FolderName`, the helper the
  previous review approved for exactly this question. There is no duplicated logic anywhere in the diff.
- **Drive root, UNC path, trailing separator, POSIX path.** These are properties of the helper, and the
  previous review probed the helper itself directly on all of them (`D:\` answers `D:`, `\\server\share`
  answers `share`, trailing separators ignored, POSIX correct, null and empty answer `""`). This change only
  changes what the two call sites route to; I confirmed the routing and did not re-probe the helper.
- **Ambiguous shapes fail closed, and one old crash is gone.** The old inline code dereferenced `r.Path`
  unconditionally (`r.Path.TrimEnd(...)` — a null path in the registry threw
  `NullReferenceException`); the helper answers `""` for null, and an empty or separator-only path answers
  `""`, so the entry shows its path with an empty name rather than a guess or a crash. Nothing ambiguous is
  answered with a fabricated name.
- **The tests prove the fix and cannot pass on the old code** — witnessed red in my own revert experiment
  (item 2 above), with the exact symptom the defect produces. They build the registry file by hand so the
  stored name is empty — the on-disk shape a registry carried from another machine or an older version
  actually has — rather than going through `TryAdd`, which would store a name and hide the fallback. That is
  the right fixture. Placing them in `Gateway.UnitTests`, a suite the mission's check actually runs, rather
  than the parked `Gateway.Tests` where the executor's other tests sit, is right and honestly reasoned in
  the record.
- **The phase 3 hazard the Delivery Lead named is genuinely closed**: the two lines fixed are the ones the
  ordered union will be built from, so phase 3 can no longer pass its own proof over incorrectly named
  repositories coming from this executor. The two adjacent defects that could still corrupt names —
  `RepositoryRegistry.TryAdd` storing a mangled non-empty name the fallback can never rescue, and
  `ControlEndpoints.NormalizeRepoPath` grouping by a host-canonicalised, lower-cased key — are left unfixed
  and named with file and line; I verified both descriptions against the code and they are accurate. Whether
  phase 3 can proceed over an unfixed `NormalizeRepoPath` is a question for the seat that owns the mission,
  not a defect of this change: the change was scoped to the one site the Delivery Lead ruled.

**No finding against the catalogue read.**

## 2. The throughput test — the one question this seat was asked

**Would this still catch a parser that genuinely got slower? Yes, in both of the ways a parser gets
slower, and it catches the common one far more sharply than the old test did.**

A managed parser slows down in essentially two ways, and each has an assertion pointed at it:

1. **More work per byte** — a string per cell, a query in the hot loop, a boxed struct. The allocation
   assertion catches this hard: the measurement is bytes allocated on the parse thread per byte parsed,
   which is deterministic (witnessed: my 34.474 against the author's 34.472/34.475/34.474 on a different
   day), per-thread, and unmoved by machine load. The budget is 40 against a measured 34.474 — 16 percent of
   headroom, so ordinary drift passes and any regression that changes the order of the work does not. The
   old test waved a deliberate regression of this class through (median 24.57 ms, all bars green, while the
   allocation figure was 63 percent over budget); the new one fails it. For this class the change made the
   test STRICTER, not immune.

2. **The same work, slower per chunk** — an algorithmic change that allocates nothing new. The
   fastest-chunk assertion catches this the moment it matters: measured elapsed time for a chunk is the
   parser's own cost plus interference, and interference is never negative, so the fastest of 160 samples can
   never read BELOW the parser's own cost. A genuinely over-budget parser (true cost above 100 ms) therefore
   cannot be certified green — a pass at this assertion is never a false pass. A slowdown that stays under
   100 ms per chunk, say five times today's 10 ms, would pass — but a chunk under its 100 ms
   responsive-interface budget is within the only property this test claims to guard, so nothing it existed
   to protect has broken. And a slowdown of THAT shape with no allocation change is the one case where the
   old test was also blind on a quiet machine (its median bar was 100 ms too).

The test is not a fix that cannot fail. It fails for the two things a parser can do wrong, it failed for
the author's deliberate regression, and it no longer fails for the one thing the parser cannot help —
which was the defect being fixed.

### One finding — the stated invariant is inverted, in the failure message a future red will read

The test's comment and the record both state: *"a figure that is only ever inflated by load cannot produce a
false RED"*, and the failure message the test prints says: *"Scheduler contention can only add to an elapsed
time, so even on a loaded machine this says the parser itself is too slow."*

The logic runs the other way. One-sided interference guarantees that a **green** is never false (fastest ≥
true cost, so fastest ≤ 100 proves the parser is within budget), and it leaves a **red** possible in
principle: a parser whose true cost is close to the budget, on a machine whose minimum interference across
all 160 chunks pushes the fastest sample over it. The comment's companion sentence has the same inversion —
it says a fully-contended run "can produce a false green", when inflated measurements produce false reds, and
a pass under this assertion is always a true pass (what a contended machine produces is a green that
certifies the parser while delivered latency is over budget — the test declining to judge the machine,
which is the design).

**The harm, and why it matters despite being remote:** today the parser costs ~10 ms a chunk and a false red
needs ~90 ms of interference on every one of 160 samples — the author's own load-average-40-plus run
measured fastest at 11.07 ms, so in practice this red will not happen. But the sentence is written as a
property of the measurement, and it is not one — it is an empirical fact about today's ten-times margin. If
a future legitimate change puts the parser's true cost at 70–90 ms per chunk (still within budget), a
heavily loaded machine could turn this test red, and the failure message would tell the reader, wrongly,
that machine load is excluded — sending them hunting for a parser regression that does not exist, exactly
the mis-triage this repair was made to prevent. The true sentence is: *a pass is never false; a red means
the parser is over budget or was within interference of it.* Whether that sentence is corrected is for the
seat that owns the change; the behaviour of the test is sound either way, and this finding asks for no code
change to the assertions themselves.

## 3. The record's honesty

- **The deferred call sites are named with file and line, and they are accurate.** Twelve of the seventeen
  spot-checked, all holding exactly the shape described; the two adjacent defects of the same family are
  named with lines I verified. The correction of the previous seat's count (twelve listed, not thirteen;
  at least seventeen sites in total across eleven files, seven newly found) makes the list longer and the
  outstanding work bigger, not smaller — the direction an honest record errs in.
- **The Windows gap is not softened.** The record states it twice, plainly: for the catalogue read, *"the
  change is proven on macOS; on Windows it is proven by reading... neither that seat, nor its reviewer, nor
  this one has watched any of this work go red on a Windows machine"*; and for the allocation budget, the
  40 figure is *"expected, not observed — nobody has run this on Windows"*, which is why the headroom is 16
  percent. Nothing has been quietly upgraded from reading to observation.
- One caveat carried forward, correctly and unchanged: the helper treats a backslash as a separator in POSIX
  paths, so a repository folder genuinely named `my\repo` answers `repo` on a POSIX machine. Recorded by the
  author, reviewed and accepted in the previous review, seen in no real repository. Not re-opened here.

## Verdict

**Approve.** The catalogue read routes both fallbacks through the shared, already-approved helper, fails
closed on ambiguous shapes, and its tests go red against the reverted fix — witnessed here, not taken on
trust. The throughput test still catches a genuinely slower parser in both ways a parser gets slower, is
strictly sharper than the old test for the common regression class, and no longer renders verdicts on
machine load; the budget was not widened and no other assertion in the file was loosened. The one finding is
a wrong invariant sentence in the test's comment, record and failure message — a documentation defect with
a real (if remote) mis-triage cost, for the owning seat to decide on. The record's deferred list is
accurate at every line I checked, and the Windows gap is stated as plainly as it was before.
