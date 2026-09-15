# Phase one, work items one to four: what was built, what is proved, and what is not

Branch `mission/turn-detection-phase-one`, worktree `D:\ReposFred\devthrottle-turn-detection`.
Four commits, one per issue, in order: #2854, #2855, #2856, #2857.

This report is written to be disbelieved. Everything below that says "proved" names the test and
the mutation that was watched turning it red; everything that is not proved is in its own section
rather than left for a reader to notice by its absence.

## What was built

**#2854 - the pure function.** `src/CcDirector.Core/Wingman/TerminalContentNovelty.cs`. Two
candidate rules behind one interface, `ITerminalNoveltyRule`, because the evidence does not settle
which is better. The row rule applies the four conditions in the harness's own order: at least
three letters or digits; none of the caller-supplied markers; a key - the row reduced to lower-case
letters only - absent from the settled screen; and not a near-duplicate of a settled row at eighty
percent. The size rule is a threshold on how much text changed after aligning the two bodies row by
row. Nothing called this at the end of the commit.

**#2855 - the settled screen.** `TerminalStateDetector` captures the settled screen on every settle
rather than only when the shadow evidence producer happens to be wired, and `TryExtractBody` gained
a sibling that returns the rows, with the joined form expressed in terms of it.

**#2856 - the markers.** `IAgentDriver.SelfDescribingRowMarkers`, empty by default, with eleven
strings for Claude Code and six for Codex.

**#2857 - the wiring.** The rule is wired into `OnBytesCore` behind a switch that ships OFF, the
continuous-idle path folds into the same settled case, and every check writes a row to a new local
append-only log, `TurnDetectionShadowLog`, which ships ON.

## The similarity function is Python's, not something equivalent

The Architect ruled before this was written that the eighty-percent test must be Python's
`difflib.SequenceMatcher.ratio` and not a longest-common-subsequence ratio, because the two
disagree on 28 percent of short pairs - worst case 0.15 against 0.67 - which is far wider than the
threshold they feed. So `PythonSequenceMatcher` is a port of that algorithm: the longest matching
block then recursion either side, the orientation kept (settled row as A, candidate as B, because
the ratio is not symmetric), and the popularity heuristic that engages only from two hundred
elements up, reproduced rather than skipped.

It is pinned against CPython 3.11.6 two ways. Six ratios are literals in the test source, which a
reader can check by eye. A generated fixture replays 325 pairs, 42 of them long enough to exercise
the popularity heuristic.

**The literals alone would not have caught the substitution the ruling forbids.** Swapping the
ratio for a longest-common-subsequence ratio and running the suite turned four tests red and left
thirty-five green - and `Ratio_matches_python_exactly`, the six hand-picked literals, was one of
the thirty-five, because difflib and the rival definition agree on all six. The fixture is what
catches it. That is the whole reason it exists, and it is why "three pinned values" was treated as
a floor rather than the requirement.

## What is proved, and the mutation watched failing

Every mutation below was applied, the suite run, the reported symptom read, and the code restored.

| What | Test | Mutation watched | Result |
|---|---|---|---|
| The ratio is difflib's algorithm | `SequenceRatioPinnedToPythonTests` | ratio replaced with a longest-common-subsequence ratio | 4 red, 35 green |
| The settled screen is captured with no producer wired | `TerminalSettledCaptureTests` | the `_activityProducer is not null` condition restored | 1 red, 22 green |
| The markers suppress the rows they were written for | `SelfDescribingRowMarkersTests` | the Claude Code list emptied | 9 chrome rows red, all 4 real-reply controls green |
| Bytes inside the window push the check out | `A_byte_inside_the_window_pushes_the_check_out...` | the timer no longer re-armed while a check was pending | red |
| A later byte asks the rule again | `A_later_byte_re_arms_a_check_that_found_nothing` | a latch making it one check per wake - the reviewer's own defect | red |
| A repaint holds the session red | `A_repaint_that_adds_no_content_holds_the_session_red` | the rule made to open unconditionally | red |
| The switch-off path still flips on the byte | the three `With_the_switch_off_...` tests | the off path made to defer instead of flipping | 3 red |

The switch-off case is proved twice, because it is what every Director will actually run: the
transition sequence is identical with the shadow log on and off, and the flip still happens
synchronously on the byte rather than after the settling window.

The four real-reply controls in the marker tests are deliberately placed next to markers. "I
refactored for clarity" carries the `ed for ` fragment a thinking-line marker would have used, and
"Updated the installer" sits beside `Update installed`. Both must still open the turn, and do.

## What is NOT proved

**Nothing here is measured on live bytes or on the corpus.** The C# rule has never been scored
against the labelled corpus, and no shadow run has happened. Every percentage quoted in the code
comments came from the design and the review, and was produced by the Python harness, not by this
code. The claim "this rule holds 94 percent of repaints" is NOT a claim this phase has earned - it
is a claim about a Python implementation of the same idea. Work item five is where that gets
answered.

**The size candidate's definition is stated, not reproduced.** The reviewer who scored a size
threshold at 97.0 percent did not deposit the script behind it, so "how much text changed" was
never written down anywhere to copy. What is implemented is our definition of it - align the rows,
count the characters of the rows no matching block covers - and its threshold constant is labelled
unvalidated in the code. Scoring it against the reviewer's number would be comparing two different
functions.

**The re-arm guarantee is proved as a property, not traced to a line.** The scheduler re-arms its
timer on every byte, so the guarantee holds by construction. A narrower mutation - leaving the
scheduled flag set - did not break any test, because the timer is re-armed regardless. The test
proves the property holds; it does not prove which line carries it.

**The thinking line is a known remaining source of phantoms for Claude Code.** Its verb rotates
through a large vocabulary, so the only stable fragment is `ed for `, which occurs in ordinary
prose. It is deliberately not a marker, and rows of that shape will still open a turn.

**Codex's marker list rests on forty-three episodes.** That is thin and is said so in the code.

**The published 94.0 percent is now slightly stale in our favour.** `esc to cancel` is a distinct
row from `esc to interrupt` and the measurement's expression carried only the second, so the row
rule's real score is a little better than published. That figure must be re-taken in work item
five, not adjusted by arithmetic.

**Two tests do not run in the default gate.** `TerminalSettledCaptureTests` and
`ContentTurnRuleTests` drive the detector's real timers, so they are wall-clock dependent and
belong in `CcDirector.Core.Tests`, which is parked. They were run explicitly and are green. The
pure halves of the same work - the ratio pins, the row rule, the body rows, the markers - are in
`CcDirector.Core.UnitTests`, which does run at commit time.

**One deviation from the issues.** Issue #2854 said the tests live in
`src/CcDirector.Core.Tests/Wingman/TerminalContentNoveltyTests.cs`. Same file name and same
namespace, but they were put in `CcDirector.Core.UnitTests`, because they are pure, take 46
milliseconds, and `Core.Tests` is parked out of the default run - and the gate script's own comment
says a guard whose value is fast feedback must not live in a parked suite.

## What I got wrong on the way

Two of the item-four tests failed on their first run and both looked like production defects. They
were one test defect: the helper waited for "at least one shadow row" and got the row from the
session's FIRST turn, because a first turn is also a settled session receiving bytes and therefore
also produces a check. It was asserting against the wrong check. The helper now takes a baseline
and waits for a row past it, and says so in its own comment.

The first mutation written for the re-arm test did not turn it red, which is recorded above rather
than quietly replaced with one that did.

## The gate

The default local run is green: 1,719 tests across eight suites, every one reporting
`outcome=Completed`.

`-Parked` was then run because the gate itself flagged a coverage gap. Two of the three parked
suites ran and passed - `CcDirector.Core.Tests` at 4,438 passed (8 minutes 22 seconds) and
`CcDirector.Gateway.UnitTests` at 4,297 passed.

**`CcDirector.Gateway.Tests` DID NOT RUN, and this report does not call it green.** It reported
`outcome=Failed  total=0  executed=0`, which is a run that collected nothing rather than a run that
found a defect. The cause is in its own log: it queued behind another worktree's run of the same
suite - that suite takes a machine-wide lock because concurrent runs corrupt each other - waited
forty-five minutes across two successive holders (`devthrottle-wingman-a`, then
`devthrottle-wingman-b`), and then refused to start alongside the second rather than overlap. The
lock was still held when this was written, so a retry would queue and refuse again.

Two things follow, and neither is "it passed". First, the suite's verdict on this change is simply
unknown. Second, nothing in these four commits touches the Gateway: every changed file is in
`CcDirector.Core` (`Wingman/`, `Drivers/`, `Storage/CcStorage.cs`) plus its two test projects, and
the Core code that changed is covered by the two parked suites that did run. That is a reason to
think the risk is low, not evidence that the suite would pass. It should be run before this lands.

This is the queue issue #1156 describes, observed rather than inferred.

## Where the verdicts land

`%LOCALAPPDATA%/cc-director/turn-detection-shadow/<sessionId>.jsonl`, one line per check. Each line
carries the time, the agent, the mode, which rule was authoritative, what the byte rule would have
done, what each candidate decided, the row the row rule saw, the size magnitude and its threshold,
both screen hashes, the byte count of the burst, and whether a submission explains the wake.

Switches: `CC_DIRECTOR_CONTENT_TURN_RULE` is unset (off) and takes `row` or `size`;
`CC_DIRECTOR_TURN_SHADOW=0` turns the log off.
