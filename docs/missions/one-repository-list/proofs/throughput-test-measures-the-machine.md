# `Parse10MbInChunks_TypicalChunkStaysWithinUiBudget` measures the machine, not the product

Found by the Delivery Lead on 19 September 2026 while verifying phase 1, and recorded here because it
is a defect in the mission's own check that neither Developer saw.

## What happened

Verifying phase 1 independently, `dotnet test src/CcDirector.Core.Tests` came back **1 failed**, 4456
passed, 4475 total. The phase 1 Developer had reported the same suite on the same commit as **0
failed**, 4457 passed. Both runs were honest. The difference was the machine.

The one failure was
`CcDirector.Core.Tests.TerminalThroughputTests.Parse10MbInChunks_TypicalChunkStaysWithinUiBudget`.

## Which it is: the TEST

Settled by measurement rather than argument.

| Load average when run | Result |
|---|---|
| 11.93 (three test suites plus fifteen agent sessions on this Mac) | **failed** |
| 2.99 (same commit, same binary, minutes later) | passed - 4 of 4 in 9s |

Nothing about the product changed between those two runs. The test asserts a wall-clock budget, so on a
shared machine it reports how busy the machine is. That is not a property of the terminal parser.

## Why it matters more than one red line

This is a fleet machine that routinely runs a dozen or more agent sessions at once, so the test will go
red at random. The damage is not the red itself - it is that a throughput assertion which cries wolf gets
dismissed as noise, and the day it fails because the parser genuinely got slower, it will be waved
through with everything else. A performance check that is ignored is worse than no performance check,
because it is mistaken for cover.

It also came within one step of being misattributed: the failure appeared for the first time on a
verification run of phase 1, and phase 1 touches session recording. It has nothing to do with the
terminal parser, and a less careful reading would have sent a Developer hunting through phase 1's diff
for a defect that was never there.

## Not fixed here

Handed to the seat making the .NET suites green on macOS, which owns exactly this job. The mission does
not claim it is fixed. What the mission claims is that it was found, measured, named as the test's fault
rather than the product's, and not quietly re-run until it passed.

The wrong fix would be to widen the budget until the machine stops failing it, which throws away the
only thing the test was for. What it needs is to stop measuring elapsed wall-clock on a contended
machine - or to be stated as a property that does not depend on how busy the box is.
