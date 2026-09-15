DISAGREE

## Scope and positive evidence

I inspected HEAD `df1c5aea7` against `origin/main` at `0330dbce5`, including all nine
branch commits and every changed product and test file.

The similarity port is supported. I independently replayed all 325 fixture rows with the
reference runtime: all 325 ratios matched, and 42 rows exercised sequence B at or above the
200-element popularity boundary. The focused pure-rule run executed 77 tests and passed 77.
The focused detector run executed 23 tests and passed 23. The normal local gate executed
1,719 tests across eight suites, with every suite reporting `outcome=Completed` and every
test passing.

That evidence does not cover the missing-baseline path, exceptions during a content check,
the production timing constants, a burst that reaches the maximum deferral, or the live
continuous-idle detector path.

## Findings

### 1. A missing settled body is not treated as ambiguous, so the size rule can lose a turn

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:565-587`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:733-749`
- `src/CcDirector.Core.UnitTests/Wingman/TerminalContentNoveltyTests.cs:241-248`

The settle path updates `_settledBodyRows` only when extraction succeeds. If the grid is
empty or the cursor is at the top, extraction returns false and the field remains empty, or
retains the previous turn's rows after a later failed capture. The content check calls a
frame ambiguous only when `current.Length == 0`; it never checks whether the settled side
exists.

Constructed sequence: a session settles while its cursor is at row zero, so no baseline is
captured; the size rule is enabled; one later burst produces a valid current body containing
fewer than 200 changed characters; the check computes fewer than 200 changed characters
against the empty baseline and holds the session red. If that was the complete reply, no
later byte exists to re-arm the check. This is a lost turn, not a delayed one.

The unit-test comment saying the detector never asks the rule with no settled side is
contradicted by the detector's own failed-extraction path.

What would have to be true for this finding to be wrong: every settle would have to produce
a non-empty body before any enabled content-rule check. The extraction contract explicitly
allows empty-grid and cursor-at-top failures, so that premise is not established.

### 2. A thrown check consumes the burst, and a later throw can latch active without writing Working

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:532-566`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:625-644`

`OnContentCheckCore` clears `_checkScheduled`, `_checkPending`, and `_pendingBytes` before it
reads the screen or evaluates either candidate. Any exception after that point reaches the
outer catch, which only logs it. A one-burst reply then has no pending check and no later byte
to create another one.

There is a second, worse window after a positive verdict. `MarkActiveFromContent` sets
`_active = true` before the continuous-idle snapshot, unexplained-wake evidence, state write,
and quiet-timer arm. If one of those operations throws, the outer catch again only logs it.
The session can remain red with `_active` true; later bytes take the already-active branch
and do not create a fresh check. This directly contradicts the nearby claim that the loose
latch's worst outcome is only a duplicate Working write.

The explicit false-return path is conservative, but the exception path is not. The focused
tests exercise neither one.

What would have to be true for this finding to be wrong: every screen read, hash/diff step,
evidence call, state write, and timer arm after the flags are changed would have to be
non-throwing for every backend and lifecycle race. The surrounding catch blocks explicitly
acknowledge that this premise is false.

### 3. Production bypasses the candidate interface that the work says is the single seam

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalContentNovelty.cs:304-322`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:573-587`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:611-617`

`ITerminalNoveltyRule` says every consumer takes a verdict through that interface, and the
mission record says both candidates sit behind it so the live choice and later scoring are
the same functions. The detector does not hold or invoke an `ITerminalNoveltyRule`. It calls
`GainedContent` and `ChangedCharacters` directly, repeats the size threshold comparison, and
then switches between two local booleans.

The current direct calls happen to agree with the two interface implementations. Nothing
keeps them agreeing. Work item five can score an interface implementation while production
uses a separately wired rule, and every current test can remain green.

What would have to be true for this finding to be wrong: the interface would have to be
intended only for test or offline use. That contradicts its product comment, the manager
mandate, and the build report's stated architecture.

### 4. The observation log is synchronous and precedes an enabled rule's state transition

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:593-617`
- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:90-105`

Every check enters one process-wide lock and calls `File.AppendAllText`. With a content rule
enabled, that happens before `MarkActiveFromContent` and before the Working state write. The
append catches exceptions, but it has no latency bound and cannot catch a blocked filesystem
operation. One slow path also holds the global lock and stalls checks for every other
session.

Therefore the mission-record claim that the observation log can never affect the session is
too strong. A slow or blocked append delays the very state decision it is meant only to
observe.

What would have to be true for this finding to be wrong: every configured storage path and
filesystem write would have to complete promptly with a known upper bound. No such bound
exists in this code.

### 5. The switch-off proof covers one transition sequence, not unchanged behavior or cost

Files and lines:

- `src/CcDirector.Core.Tests/Wingman/ContentTurnRuleTests.cs:62-81`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:394-418`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:505-529`
- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:43-48`

The test runs the changed detector twice and compares its observed state transitions with
the shadow log disabled and enabled. Its specific presence is the sequence `Working`,
`WaitingForInput`, `Working`. That proves the shadow toggle does not change that one ordinary
session's transition sequence. It does not compare the switch-off branch with shipped code,
count calls that write the same state, or observe screen locks, timers, and filesystem work.

With the content rule off and the shadow default on, an ordinary settled wake now schedules
a timer, reads the screen, computes both candidates, hashes bodies, and appends JSON after
the immediate byte-rule transition. The continuous-idle state logic itself is preserved:
both its active and settled switch-off branches still call `OnBytesContinuousIdle`. Its cost
is not preserved. Because that driver emits footer bytes forever while settled, the maximum
deferral forces a check and append about every three seconds during continuous chatter, and
the next byte starts another check. There is no rotation or retention path for those files.

What would have to be true for this finding to be wrong: "unchanged" would have to mean only
the values in one observed state-transition sequence, excluding added locks, timers, CPU,
and unbounded disk writes; or continuous idle output would have to stop while settled,
contrary to the trait that selects this path.

### 6. Several load-bearing values can change while the relevant suites stay green

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalContentNovelty.cs:25-49`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:77-106`
- `src/CcDirector.Core.Tests/Wingman/ContentTurnRuleTests.cs:26-28`
- `src/CcDirector.Core.Tests/Wingman/ContentTurnRuleTests.cs:193-220`
- `src/CcDirector.Core.UnitTests/Wingman/SelfDescribingRowMarkersTests.cs:66-100`
- `src/CcDirector.Core.UnitTests/Wingman/TerminalContentNoveltyTests.cs:163-211`
- `src/CcDirector.Core.UnitTests/Wingman/TerminalContentNoveltyTests.cs:294-307`

The exact eighty-percent near-duplicate threshold is not pinned. The negative and positive
examples are about 0.98 and 0.30, so replacing 0.80 with 0.90 preserves them; the length-band
assertion also becomes easier to satisfy. The ratio fixture pins the algorithm, not the
threshold that consumes it.

The starting size threshold is self-referenced rather than independently asserted. Replacing
200 with 201 preserves the interface test because its positive sample contains 201 changed
characters, while the threshold-boundary test constructs its own unrelated local threshold.

The production 400 ms delay is bypassed by tests that inject 120 ms. The maximum-deferral
test never reaches its three-second cap: its three writes finish within a few hundred
milliseconds. Removing the `Math.Min` cap would preserve that test, so there is no executable
guard for the requirement that continuous chatter is eventually judged.

The marker tests do not make each declared marker independently responsible for one case.
Two declared entries are not exercised at all, and several test rows carry two or three
markers, so an individual entry can be replaced while another entry still makes the row
pass. The test proves selected rows are suppressed, not that the declared inventory is the
one supported by the evidence.

Finally, behavior tests inject the rule enum directly. They do not prove that the production
environment switch reaches that enum, and no assertion pins the shadow log's claimed default
of on.

What would have to be true for this finding to be wrong: another independently derived test
would have to pin each value and exercise the maximum-deferral and environment-wiring paths.
The repository-wide reference search found no such test.

## Sharp-question answers not already covered by findings

- The similarity implementation is the required greedy longest-matching-block recursion,
  not a longest-common-subsequence ratio. Exact reference ratios are present and independently
  reproduced.
- After a normal no-content result, the scheduled flag is cleared and a later byte schedules
  another check. Bytes inside the settling window push the timer out. The maximum deadline
  judges continuous chatter, but a stream lasting beyond that deadline is necessarily sampled
  while it is still drawing; the code comment claiming a longer burst is never sampled half
  drawn is false.
- Empty current rows and a cursor at the top take the conservative open path. Missing settled
  rows do not, as finding 1 describes.
- The current switch-off state path for the continuous-idle driver still uses ordinal equality
  of the joined body. The adjacent body hash remains evidence only. The state rule is preserved;
  the default shadow cost is new.
- The three-character substance floor is pinned in both directions. The exact ratio function
  is pinned. The other replaceable values are listed in finding 6.

## Verification boundary

I did not run the full parked projects. I ran the 23 focused tests from the parked core
project that exercise this change. The normal eight-project gate and the 77 focused pure-rule
tests were green. Those runs show the current happy paths compile and execute; they do not
answer any of the six findings above.
