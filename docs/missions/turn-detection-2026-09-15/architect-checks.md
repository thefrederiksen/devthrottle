# What the Architect checked independently, and what came back

The build report is self-testimony and the inspection is a different agent's reading of the code.
These are the Architect's own checks against evidence neither of them produced. Each says what it
covers and, where it matters, what it does not.

## The pinned similarity fixture is genuinely Python's, and it is a real guard

The whole phase turns on the near-duplicate threshold being Python's `difflib.SequenceMatcher`
ratio rather than the obvious longest-common-subsequence ratio. A fixture of pinned values is
only worth anything if the values came from Python and not from the code under test fed back
into its own expectations.

All three hundred and twenty-five pairs in
`src/CcDirector.Core.Tests/TestData/turn-detection/difflib-ratios.json` were regenerated from
CPython 3.11.6 on 15 September and compared to twelve decimal places. **Zero mismatches.** So the
expectations are ground truth, not circular.

The fixture also has to be able to catch the substitution it exists to forbid. Scored against a
longest-common-subsequence ratio, **one hundred and one of the three hundred and twenty-five
pairs disagree, and thirty-seven of those land on opposite sides of the eighty percent
threshold.** A wrong similarity function cannot survive this fixture.

What this does NOT cover: it proves the expectations are right, not that the C# meets them. That
is what the test asserts and what the inspection reads.

## The file is linked into the suite that actually runs, not left in the parked one

The fixture is tracked once, under `CcDirector.Core.Tests`, and reaches `CcDirector.Core.UnitTests`
through a pre-existing `TestData\**` link in that project. It is not a stale build artefact sitting
in a `bin` directory, which would have made the default gate pass here and fail on a clean
checkout.

## Python's popularity heuristic cannot fire on real screens

The port reproduces `difflib`'s autojunk heuristic rather than skipping it, which is correct - the
published measurement ran with it on, so skipping it would quietly diverge from the numbers. The
code comment reasons about an eighty-column terminal and a hypothetical "very wide" one. The
Director's own snapshot grid is **two hundred and twenty columns**, so that hypothetical is the
default and deserved a number rather than an assumption.

Measured across the rebuilt corpus - four thousand seven hundred and twenty-seven distinct screens,
one hundred and sixteen thousand eight hundred and forty-two body rows: **no row key reaches two
hundred characters. The longest seen is one hundred and ninety.** The heuristic is inert on real
screens, not merely unlikely to matter.

What this does NOT cover: one machine, and the grid width is a constant that could change. If the
snapshot grid is ever widened, this measurement expires and the heuristic becomes reachable.

## The change really is confined to the Core

The build report claims nothing in the change touches the Gateway. A naive `origin/main..HEAD`
diff appeared to contradict it, showing modified Gateway and ControlApi files and a deleted test
file. Diffed from the true merge base instead, the claim holds: nineteen files, all under
`CcDirector.Core`, its two test projects, and this mission folder. Those Gateway entries were
`origin/main` moving forward while the work was built, not this branch.

This is **not** a reason to call `CcDirector.Gateway.Tests` green. That suite references
`CcDirector.Core` and some of its tests use the changed storage and driver surfaces, so it has
genuine exposure. It has no verdict on this change and must run before anything lands.

## The pre-existing latch, checked against origin/main rather than taken on trust

The fix report says `MarkActiveFromByte` and `MarkContinuousActive` carry the same latch shape that
finding two had fixed elsewhere, and that they were deliberately left alone as today's shipped path.
If that were wrong - if this mission had introduced the hazard there - it would sit in the
switch-off path, which is what every Director actually runs.

Read from `origin/main` directly: the byte activation sets `_active = true`, then logs, then records
evidence, then writes the state. The hazard is pre-existing and unchanged by this mission. Leaving it
is the right call and widening the change to cover it is separate work.

## The build report's reason for skipping the Gateway suites was false

The report claimed both Gateway suites "reference none of the changed types". Another session
reading the report caught it; nobody inside this mission did.

`CcStorage` is a changed type and thirty-three files under `CcDirector.Gateway.Tests` reference it.
The true reason to expect a pass is narrower: the change to `CcStorage` is one new static method with
no existing member touched, so nothing those files already call has moved. The report has been
corrected in place rather than left to be believed.

This is worth recording as a pattern, not just a correction. Both Managers reached for the widest
reassuring sentence available - "nothing touches the Gateway", "references none of the changed types"
- and in both cases the narrower true statement was still good enough to justify the same decision.
The wide version cost nothing to write and would have been believed.

## The Gateway suite cannot be run locally under contention, measured

This mission spent real time waiting for `CcDirector.Gateway.Tests` to become runnable, on the
Architect's instruction that it must have a verdict before landing. That instruction was wrong on
two counts and both are recorded here rather than quietly dropped.

**It cannot be waited out.** The lock's maximum wait is 45 minutes. Measured from the lock's own log
on 15 September, across 487 distinct holder processes, the longest single hold is 6,555 seconds -
one hour and 49 minutes. A queued run therefore cannot acquire under contention: it waits the full
45 minutes and reports `outcome=Failed  total=0  executed=0`, which is a run that collected nothing
and reads in a report exactly like a run that found no defect. The constant is sized by a code
comment claiming the suite takes roughly nine minutes. Filed as issue #2862 with the numbers.

**It was not the merge gate anyway.** The repository's rule is a green local run plus a review from
a different agent family, then merge - never hold a merge for a check. `-Parked`, which is where
this suite lives, is the RELEASE gate. Holding phase one's merge for it inverted that.

So the corrected plan: merge on the local green and the inspection, and read the continuous
integration result afterwards, where the suite runs on a clean runner with no lock at all
(`ci.yml` runs `dotnet test cc-director.sln -c Release`, and the suite is in that solution - both
checked). A red there is chased forward immediately.

**What this does NOT license.** The suite still has no verdict at the moment of merging, and this
change is not released by merging. Before any release carries it, the release gate is one command -
`.\scripts\test-local.ps1 -Parked -Configuration Release` - and that run has to happen on a machine
quiet enough for the lock to be free.
