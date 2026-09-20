# Shared-reader review - the known-repository reader stops re-sorting and carries the verdict

**Reviewer seat:** opened by the Delivery Lead for the change on `mission/one-repo-list-shared-reader`
(2 commits, 2 files), run under the mission workflow's Reviewer conduct (DevThrottle Method, section 3).
A different agent family from the seat that wrote the code.

**Scope of this review:** the diff `origin/main...origin/mission/one-repo-list-shared-reader`
(`ee9345d4a`, `b69b8bb57` - `packages/client-core/src/api/client.ts` and
`packages/client-core/src/api/newSession.test.ts`), the mission document, the phase 3 review that
ordered this change, the DevThrottle Method, `docs/CodingStyle.md` (skimmed - it is scoped to
C# / .NET / WPF; this diff is TypeScript and follows the file's existing conventions), and
`CLAUDE.md` Critical Rule 7. To answer the verdict question I also read the Gateway side this
reader consumes: `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` (`OrderOneList`) and
`src/CcDirector.Gateway.Contracts/KnownRepositoryDto.cs` - read only, not changed by this diff.

**What I ran** is in the runs table. **What I could NOT reach:** the dotnet suites from the mission
check - this diff contains no C# (`git diff --stat` is two TypeScript files), so they prove nothing
about this change that phase 3's review has not already proved about the Gateway side; I did not
run them. No phone or Cockpit rendering was executed - no device, and the Cockpit does not call
this reader yet (see finding on the brief's premise below).

---

## Verdict: no blocking findings. The change is what it claims to be.

### Question 1 - is the re-sort actually gone?

**Gone, not weakened, not conditional.** The old line
`list.sort((left, right) => right.lastUsed.localeCompare(left.lastUsed))` is deleted from
`getKnownRepositories`; the function now maps, filters pathless rows, and returns. I searched
rather than assumed, across all of `packages/client-core/src` for `.sort(`, `localeCompare`,
`OrderBy`: the only two sorts left in `client.ts` belong to `getDirectors` (line 1694, the machine
picker) and `getRepos` (line 1723, the Director registry route) - different routes, untouched by
this diff, and both pre-existing and documented. Nothing in the reader re-orders, re-ranks,
reverses or slices. The one behaviour that changes membership rather than order - dropping rows
with no path - was already there, is not a ruling on order, and the test now says so in a comment.

**I broke it myself to prove the guard is real.** I temporarily re-introduced the deleted sort
line, ran `newSession.test.ts`, and the new guard test "serves the Gateway's order untouched and
never re-sorts on lastUsed" failed exactly as designed (1 failed, 5 passed), because the
never-opened row - fed at the TOP, above two used rows - would be pushed to the bottom by a
lastUsed sort. Restored, the file is byte-identical to the branch tip (`git diff` empty) and the
suite is green. The assertion is index-for-index, so it holds down a reorder and nothing else.

**The code path around it - one re-sort survives, and it is not this reader's.** The only
production caller of `getKnownRepositories` is the phone's `apps/mobile/src/pages/NewSession.tsx`,
and its `mergeRepositories` (lines 61-88) still re-sorts by `lastUsed` then label, still
re-deduplicates paths with its own Windows-ness rule, and - newly relevant - **drops the
`neverOpened` flag**, rebuilding each row as bare `RepoInfo` (name, path, lastUsed only). So on
the phone today the Gateway's order is still destroyed and the verdict still does not reach the
render, downstream of a reader that is now correct. This is the phase 5 leftover the phase 3
review ordered deleted ("it must not survive phase 5"), not a defect of this change; but phase 5
now owes TWO things there, not one: delete the re-sort AND carry the verdict. Recorded here so
phase 5's reviewer finds it named.

### Question 2 - is the verdict carried faithfully, and by the right shape?

**The types achieve the stated design.** `KnownRepoInfo extends RepoInfo` with a required
`neverOpened: boolean`; `getKnownRepositories` returns `KnownRepoInfo[]`; `getRepos` is untouched
and still returns plain `RepoInfo[]` carrying no verdict - so the other route's reader is not
forced to invent one, which is exactly what putting the field on `RepoInfo` would have done. I
searched all of client-core for constructions of the flag: the reader's map is the only one. No
default value is supplied anywhere else.

**The one coercion in the reader is not an invented verdict, and I can say why on evidence.**
`neverOpened: repository.neverOpened === true` reads an absent flag as `false` ("not never-opened").
Against this Gateway that case is unreachable: `KnownRepositoryStore.OrderOneList` stamps
`NeverOpened = row.LastUsedUtc is null` on every row it emits (line 444 - "stamped here and nowhere
else"), and the DTO's `NeverOpened` is a plain non-optional `bool`, so every row on the wire carries
it. The coercion is defensive parsing of untrusted JSON, not a ruling - and critically, the reader
never derives the verdict from the absent `lastUsed`, which is the actual Rule 7 trap. The new test
"does not invent the never-opened verdict for a row that arrives without one" pins that: an
unstamped, time-less row reads `false`, where a client ruling from the empty date would have said
`true`. Honest caveat, stated rather than buried: if a future Gateway ever served this route
without the flag, rows would read as opened - the coercion's meaning is "no verdict", its rendering
would be "opened". Today that is a hypothetical the wire cannot produce.

### Question 3 - did anything break for existing callers?

**The only production caller compiles and passes unchanged.** `KnownRepoInfo` extends `RepoInfo`,
so `KnownRepoInfo[]` is assignable everywhere a `RepoInfo[]` was taken - the phone's
`mergeRepositories(...sources: Array<RepoInfo[] | null>)` takes it without an edit, and the
mobile suite is green (101 tests, 0 skipped). Nothing else in the repository calls
`getKnownRepositories` - confirmed by search, not assumed.

**The brief's premise needs correcting, and I correct it here rather than pass it on.** The brief
says this reader is "used by BOTH the phone and the Cockpit". On this branch that is not true:
`apps/cockpit/src` contains zero references to `getKnownRepositories` or `KnownRepoInfo`. The
Cockpit's New Session tab is phase 4 and does not exist yet; this reader is the one both WILL use.
No harm follows - but the blast radius of this change is the phone alone, and the review is scoped
accordingly.

**The two named Cockpit stub files are unaffected, and not for the reason the brief feared.**
`fleet/FleetMapView.test.tsx` and `sessions/sessionsBadge.test.tsx` stub `getRepos` - the Director
registry route this diff does not touch - with `Promise.resolve([])`. They never stubbed
`getKnownRepositories` at all. Both suites pass.

### What I ran (all on this worktree, macOS, Apple silicon)

| Run | Result |
|---|---|
| `npm ci` | clean (esbuild postinstall blocked by policy - warning only, build unaffected) |
| `npm run typecheck` (all four workspaces) | **passed, zero errors** - client-core, cc-assistant, cockpit, mobile |
| `npm test --workspaces --if-present` | **exit 0** - client-core **1,456 passed** (123 files), cc-assistant **106 passed** (8 files), cockpit **457 passed** (55 files), mobile **101 passed** (19 files) |
| Skipped counts, every suite | **zero skipped, zero todo** - grepped the whole run log; a skipped test would read identically to a passing one and there are none |
| Revert experiment (sort re-introduced by me) | guard test **failed exactly as designed** (1 failed, 5 passed), restored byte-identical to the branch tip |

The client-core count is phase 3's 1,454 plus this diff's two new tests - consistent, not a
coincidence to be trusted: both new tests were run and both were named in the output.

**Taken on the mission's own record, not re-derived:** the Gateway-side ordering and stamping that
this reader consumes - phase 3's review verified it against a live Gateway and over real HTTP
tunnels, this diff touches no C#, and I read the stamping code only to answer question 2.

### Minor observations, none blocking

1. **The phone's `mergeRepositories` must lose its re-sort AND carry `neverOpened`** - phase 5's
   scope, ordered by the phase 3 review, restated here because the flag now exists and that
   function silently drops it.
2. **The branch base is 2 commits behind `origin/main`** (both smart-director-restart - docs and
   Avalonia files, no overlap with `packages/client-core`). The mission document (section 8) says
   rebase before the pull request; that rebase will be trivial.
3. **The `=== true` coercion** - documented above; the comment and test say what it means, which
   is the most that can be asked of a defensive parse of a wire that always carries the flag.

---

**Summary for the Delivery Lead.** The re-sort is gone - deleted, not weakened - and I watched the
guard fail by putting it back. The verdict travels on a separate `KnownRepoInfo` so `getRepos`
invents nothing, the Gateway provably stamps the flag on every row, and the reader derives nothing
from an absent date. Nothing broke: the phone compiles and passes unchanged, the two named Cockpit
stub files stub a different route, and every web suite is green with zero skipped. One correction
to the brief: the Cockpit does not call this reader yet - it is phase 4 - so the only existing
caller is the phone, whose own `mergeRepositories` re-sort and verdict-drop remain phase 5's
ordered work. Nothing blocks the pull request.
