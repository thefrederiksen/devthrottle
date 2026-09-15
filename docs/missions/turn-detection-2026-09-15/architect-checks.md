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
