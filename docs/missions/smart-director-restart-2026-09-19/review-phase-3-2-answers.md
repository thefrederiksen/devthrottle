# Answers - phase 3 review 2 on the way up ENGINE

Written by the Developer seat opened by the phase 3 Tech Lead (session 38f41a97), 20 September 2026.

- Branch: `smart-restart/p3-way-up-engine`, pull request **#3202** (already open, not merged, no second one).
- Worktree: `D:/ReposFred/devthrottle-smart-restart-p3-engine`.
- Reviewed commit: `238d815f6`. My commit: `9d34b67e5`.
- All four findings were accepted by the Tech Lead and all four are answered here.

---

## READ THIS FIRST - what changed on the published surface

**Nothing.** `IDirectorWayUp`, `WayUpWords`, `IWayUpGateway` and every record they carry are unchanged
in name and in shape. The window Developer building against this branch head has nothing to carry.

What moved is inside the engine and inside the tests:

- `DirectorWayUp`'s reopen claim is now STATIC. The type's public surface is the same; the field was
  private either way.
- `DirectorWayUp` gained one `internal static` method, `ForgetReopenClaims()`, visible only to the test
  assemblies through the existing `InternalsVisibleTo`. It is for tests and says so.
- Documentation gained sentences on `IDirectorWayUp.ReopenAsync` and on `ControlApiHost.CreateDirectorWayUp`.

**One thing a window author should now read and act on:** the factory may be called as often as a caller
likes. A fresh engine per window, per screen or per action is safe, because the once-only reopen claim is
held by the process. That is written on the factory itself, so nobody has to find this file.

---

## The counts

Every run below is a FULL build. There is no `--no-build` run anywhere in this document, including the
restore runs of the two revert proofs.

| Check | On `238d815f6` (the Tech Lead's and the Reviewer's figure) | On my commit `9d34b67e5` |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 580 total, 580 passed, 0 failed | **580 total, 580 passed, 0 failed** |
| the same check, run a SECOND time in a row | - | **580 total, 580 passed, 0 failed** |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin\|FullyQualifiedName~Agent"` | 247 total, 247 passed, 0 failed | **247 total, 247 passed, 0 failed** |
| `dotnet build cc-director.sln` | 0 warnings, 0 errors | **0 warnings, 0 errors** |

Both Gateway runs reached their end and each printed its own final count line; neither was aborted. **The
two runs are identical, which is what the Tech Lead asked the second run for**: the new state is static,
and static state that leaked between tests would show as a second run that differed from the first.

**No test was added or removed.** The mission's check stays at 580 and the agent plugin check at 247. Every
change here is an assertion added to a test that already existed, a test made to use two engines instead of
one, a collection attribute, or a comment. That is deliberate: findings 2, 3 and 4 asked for the rules
already held to be held HONESTLY, not for more of them.

**Isolation, measured rather than assumed.** The way up tests pass alone as well as in the whole suite:
`--filter "FullyQualifiedName~DirectorWayUp"` alone gives 57 total, 57 passed, 0 failed, and the twice-test
alone gives 1 total, 1 passed, 0 failed. The whole-suite figure is the 580 above, twice.

---

## Finding 1 - the once-only reopen guard was instance state, and the factory hands out a new engine every call

**Fixed the way the product already fixes this exact problem, and not by asking the caller to hold one engine.**

`DirectorRestore` holds its one-at-a-time rule in `private static readonly object Gate` and a static field,
because the rule belongs to the DIRECTOR and there is one Director per process. The reopen claim now has the
same shape: `private static readonly HashSet<string> Reopened` under `private static readonly object
ReopenGate`, and `ClaimReopen` is a static method. The comment says in the first sentence of its own
paragraph that the claim belongs to the process and not to the object, and says why: held on the instance,
the guarantee would depend on the next caller keeping one engine for the Director's lifetime, which is a
tripwire that caller cannot see.

The comment also now records the second half of the Reviewer's finding, which was nowhere in the code
before: **the still-running check does not catch what the claim misses**, because a reopened session comes
back under a NEW session id, so moments after a reopen the roster says nothing at all about the seat's
captured id.

`ControlApiHost.CreateDirectorWayUp` carries the matching sentence at the place a caller is actually
standing when the question arises: a fresh engine per call is safe, the start-up window and the history
window may each build their own, and they will still reopen a seat once between them.

The cross-restart gap is unchanged and still disclosed in the same three places: the field comment, the
`ReopenAsync` documentation, and this file.

### The test rig no longer hides it

`WayUpTestRig.WayUp()` still builds a fresh engine per call - that was never the problem, it is what the real
factory does. What changed is the test that proves the rule:

**`Reopening_the_same_seat_twice_starts_only_one_session` now uses TWO SEPARATE ENGINES.** It calls
`rig.WayUp()` twice, which is what the real windows will do. The previous version captured one engine in a
local, so it would have passed just as happily with the claim on the instance - where it guarded nothing.
The test's own summary now says that, addressed to whoever reads it next.

### Static state between tests: a test that passes alone passes in the suite

Two things, and both were needed.

1. **`DirectorWayUp.ForgetReopenClaims()`**, `internal static`, and `WayUpTestRig`'s constructor calls it.
   Several way up tests use the same record slug (`restart-1`) and the same seat id (`ended`), so without
   this the second of them would be refused for a reason about the test runner and not about the product.
2. **The three way up test classes joined `DirectorGatesCollection`.** That collection already exists for
   exactly this - the classes that take one of the Director's static one-at-a-time gates - and its
   documentation now names the way up as the third. Without it, one way up class could clear the claims
   another had just taken, mid-run. Classes in one collection run one after another; nothing else changed,
   and the suite's `MaxParallelThreads = 4` cap is untouched.

I did not reach for a unique-key-per-test scheme instead. Keying round the collision would have left the
static state shared and the isolation depending on every future test author picking an unused slug, which
is the same shape of tripwire this finding is about.

### Revert proof, and it is two parts because one part would have proved the wrong thing

**Part one - the fix can fail.** I put the claim back on the instance: the field back to
`private readonly`, `ClaimReopen` back to an instance method, and `ForgetReopenClaims` emptied. Full build:

    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpHistoryTests.Reopening_the_same_seat_twice_starts_only_one_session
    Failed!  - Failed:     1, Passed:   579, Skipped:     0, Total:   580

**1 failed, 579 passed, 580 total.** Exactly the twice-test, and nothing else moved.

**Part two - the TEST change is the load-bearing half, and this is the part that proves it.** With that
same defect still in the source, I changed the twice-test back to holding ONE engine in a local, the way it
was written before today. Full build:

    Passed!  - Failed:     0, Passed:   580, Skipped:     0, Total:   580

**0 failed, 580 passed, 580 total, with the defect present.** So the test as it stood yesterday could not
see this defect at all. Part one alone would have been an honest-looking proof of the wrong thing.

Both mutations were put back with `git checkout --` (the commit was already pushed, so the restore could not
eat the work), and `git status --short` and `git diff HEAD --stat` then both printed nothing.

## Finding 2 - the "never asks what is running" rule was held by one assertion in one test, whose own comment said the opposite

**The stale sentence is gone.** `A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_about_running_sessions`
opened by telling the reader the seam "carries no question about sessions at all", which stopped being true
the moment the ruling added the roster - and it told the very reader who would notice the rule eroding that
there was nothing to notice. Its summary now opens with "READ THIS BEFORE YOU BELIEVE THE SEAM CANNOT ASK:
it can", says why it can (the reopen needs it), says that what holds the rule is now four counts rather than
the seam's shape, names where those four counts are, and tells whoever is adding a roster call to another
path that those assertions are what they are about to break and that breaking them is the point of them.

**The other three read paths now assert it too**, one each, which is what the ruling asked for:

| Path | Test that holds it now |
|---|---|
| the start-up offer (was the only one) | `DirectorWayUpOfferTests.A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_about_running_sessions` |
| nothing waiting | `DirectorWayUpOfferTests.A_record_already_brought_back_is_not_offered` |
| the history | `DirectorWayUpHistoryTests.The_history_holds_every_record_of_this_director_newest_first` |
| the bring back | `DirectorWayUpBringBackTests.Bring_back_names_every_seat_under_every_ticked_row` |

Each carries a sentence saying why the count is there rather than just the number, so a reader does not have
to come back to this file to find out.

No product code changed for this finding. Only `ReopenAsync` calls `GetRosterAsync`, which was already true.

### Revert proof

I added `await _gateway.GetRosterAsync(ct)` to the three other entry points - the first line of
`FindOfferAsync`'s try, the first line of `ReadHistoryAsync`'s try, and before the bring back reads its
record. Full build:

    Failed ...DirectorWayUpBringBackTests.Bring_back_names_every_seat_under_every_ticked_row
    Failed ...DirectorWayUpHistoryTests.The_history_holds_every_record_of_this_director_newest_first
    Failed ...DirectorWayUpOfferTests.A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_about_running_sessions
    Failed ...DirectorWayUpOfferTests.A_record_already_brought_back_is_not_offered
    Failed!  - Failed:     4, Passed:   576, Skipped:     0, Total:   580

**4 failed, 576 passed, 580 total.** One failure per path, all four, and nothing else moved - so those four
assertions, and only those four, hold the rule. Put back with `git checkout --`, `git status --short` and
`git diff HEAD --stat` both printed nothing, and the restored file was touched to force a recompile. The
green run printed `CcDirector.ControlApi -> ...dll` and `CcDirector.Gateway.UnitTests -> ...dll`, so the
green is the restored SOURCE and not a leftover binary:

    Passed!  - Failed:     0, Passed:   580, Skipped:     0, Total:   580

**0 failed, 580 passed, 580 total**, full build.

## Finding 3 - the interface did not say that a failed start keeps its claim

Documentation only, as ruled. `IDirectorWayUp.ReopenAsync` now says it in its own paragraph, in plain words
and addressed to the person who will otherwise file it as a bug:

> A REOPEN WHOSE START FAILS KEEPS ITS CLAIM, and that is deliberate rather than a bug to report: a start
> whose answer never came back may have happened anyway, so the seat is refused from then until this
> Director restarts. A button that goes dead after one failure is doing what it was built to do, and the
> refusal says to look in the session list.

The same paragraph of that documentation also now says the once-only claim is held by the process, so the
window author learns from the interface alone that building engines freely is safe.

The trade itself is unchanged, and the code comment that already explained it is unchanged.

## Finding 4 - a future agent could be worded as resuming for the wrong reason

A comment only, as ruled, and it is on the walk test itself where whoever meets it failing will be standing.
`Every_registered_agent_resume_flag_matches_what_its_launch_spec_really_builds` now opens with:

> IF YOU ARE READING THIS BECAUSE THIS TEST IS FAILING ON AN AGENT YOU JUST ADDED, DO NOT FLIP THE FLAG TO
> MATCH THE ARGUMENTS. Widen what the walk looks at instead.

It then says what the proxy is, that it is exact for all eight agents today, which single direction it can
lie in (an agent carrying the conversation id for a log file name, a working folder or a report is not
resuming anything), why that is the direction the safe-side rule exists to prevent, and what the real fix
would be - make the walk read what the driver MEANS rather than what it spells, and leave the flag saying
what the agent really does.

Nothing was built for it. The eight values and the walk are untouched.

---

## What I did NOT undo from section 6 of the review

I read section 6 before changing anything and checked each item afterwards. The eight flags and the walk,
the still-running guard and its parity with the restore's own roster read, the record guard
`NotThisDirectors` and its three tests, the history offer, the vacuous-pass guards of the walk, the seed
file, the bring-back order rules, `IsOfferable`, and the refusal-versus-emptiness wording are all
untouched by my diff. `git diff 238d815f6..9d34b67e5 --stat` is nine files: two engine files, one factory
documentation, one Core test comment, and five test files that gained assertions, a collection attribute,
or a rig constructor.

The review's own mutation of `NotThisDirectors` remains the proof that the record guard's three tests bite;
I did not repeat it and I changed nothing it covered.

---

## What is still open

1. **Across a Director restart the same seat can still be reopened twice.** Unchanged, and unchanged
   deliberately: closing it needs a mark written onto the record, which needs the restore lease, a change
   to `CcDirector.Gateway.Contracts` and a Gateway deploy. That is the Delivery Lead's decision and not
   this engine's. It is stated in the field comment, on `IDirectorWayUp.ReopenAsync`, and here.
2. **A reopen whose start fails keeps its claim until this Director restarts.** The trade stands, and both
   reviews judged it the right way round. What changed today is that the interface now says so, so it is
   a disclosed cost rather than a surprise.
3. **No real Gateway, Director or session was run.** Everything here is against fakes of the two seams, by
   design, and that is unchanged from the first review's position.
4. **The claim is per PROCESS, which is per Director - and that is the whole of what it promises.** Two
   Directors running on one machine against one Gateway could each reopen the same seat once. That is not
   new and is not a regression: the previous guard was narrower still. It is the same gap as item 1, since
   closing either one needs the mark on the record.
5. **The parked suites, the web tests and the Python tests were not run.** Nothing in this diff touches the
   browser shells or the Python toolbelt. The whole solution builds with 0 warnings and 0 errors, which I
   ran myself on the final tree.
