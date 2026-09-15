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

---

# The six inspection findings: what was changed, and the mutation that proved each fix

A second Manager, seated after the inspection returned DISAGREE. Every finding was accepted; none
was argued away. This section is written the same way as the one above - to be disbelieved - so
every fix names the test that pins it AND the mutation that was watched turning that test red with
the reported symptom. Where a test was watched red BEFORE the fix existed, that is said; where the
API did not exist until the fix did, the proof is the mutation and that is said too.

The mutations were applied with a script rather than by hand, so each one is an exact,
reversible edit and the code was verified back to its fixed form afterwards.

## Finding 1 - a missing settled baseline could lose a turn

**What changed.** Two places, because the finding has two halves.

`RunContentCheck` (the renamed body of the check) now calls a frame ambiguous when EITHER side is
missing - no current rows, or no settled rows - and the two are told apart in the log, which now
carries `(screen could not be read)` or `(no settled screen to compare against)` instead of a
silent zero.

`OnQuietCore` now CLEARS the baseline when the settle cannot read the screen, instead of leaving
the previous turn's rows in place. That was the other half of the defect and the inspection named
it: a stale baseline is not a missing one, it is a wrong answer, and it makes the next check score
the difference between two unrelated turns.

The unit-test comment claiming the detector never asks the rule with no settled side is gone,
replaced by what is now true.

**The tests, and the mutations watched failing.**

| Test | Mutation | Watched |
|---|---|---|
| `A_settle_that_captured_no_baseline_does_not_lose_a_small_reply` | the ambiguous test drops `settled.Length == 0` | red: the reply never opened the turn - the lost turn itself |
| `A_settle_that_cannot_read_the_screen_drops_the_previous_turns_baseline` | the settle keeps the old rows | red: "a settle that could not read the screen kept the previous turn's baseline" |

Both were also watched red before the fix was written, which is the stronger of the two proofs.
The second mutation additionally reddens `A_later_byte_re_arms_a_check_that_found_nothing`, because
a stale baseline corrupts that scenario too.

The first test builds the exact sequence the inspection constructed: a settle with the cursor at
row zero, so no baseline is captured, then ONE short reply, under the size rule's threshold, with
no later byte to ask again.

## Finding 2 - the two exception windows

**What changed.**

The check no longer consumes its burst on a fault. `OnContentCheckCore` still clears the scheduled
flag, the pending flag and the byte count up front - that is what makes the re-arm guarantee work -
but the whole of the rest now runs inside a try, and a fault calls `RestorePendingCheck`, which puts
the bytes back and arms the timer again. A one-burst reply is therefore asked about again without
needing a later byte.

That retry is BOUNDED, and the bound is new work rather than something the finding asked for. A
naive restore retries forever on a permanent fault, and worse, the original deadline is already in
the past, so every retry would fire with zero delay and spin a core. `RestorePendingCheck` takes a
FRESH deadline and gives up after `MaxCheckFailures` (three) consecutive faults, saying so in the
log. One lost turn is a smaller failure than a session pinning a processor.

`MarkActiveFromContent` now releases the active latch when anything after it throws. Setting
`_active` and then faulting left the session red AND latched active, after which every later byte
took the already-active branch and scheduled nothing - permanently red, with no way back. The false
comment claiming the loose latch's worst outcome was a duplicate Working write is deleted, with a
sentence in its place saying what is actually true.

**The tests, and the mutations watched failing.**

| Test | Mutation | Watched |
|---|---|---|
| `A_check_that_throws_does_not_consume_the_burst` | the restore removed | red: one burst, a fault, and the turn never opened |
| `A_throw_during_the_state_write_does_not_leave_the_session_stuck_working` | the latch release removed | red: the session never came back to red |

Both were watched red before the fix. The fault is injected two different ways, on purpose: the
first through a rule that throws once (the injectable seam finding 3 created), the second through a
state-change subscriber that throws once - which is a real hazard, not a contrived one, since the
Gateway and the user interface both subscribe to that event. Removing the restore also reddens the
second test, because that one needs both windows closed; removing only the latch release reddens
only the second, which is what isolates it.

**Not changed, and deliberately:** `MarkActiveFromByte` and `MarkContinuousActive` have the same
shape. They are today's shipped path, unchanged by this mission, and the switch-off case is proved
byte-for-byte against them - so widening the change to cover them is a separate piece of work, not
this one. It is named here rather than left for a reader to notice.

## Finding 3 - production bypassed the interface

**What changed.** The detector now HOLDS two rules and invokes them: `ITerminalNoveltyRule` for the
row candidate and a new `ITerminalSizeRule` for the size candidate. It no longer calls
`GainedContent` or `ChangedCharacters` as functions, and the threshold comparison lives inside
`ChangedSizeRule` where it always should have.

`ITerminalSizeRule` exists because the log carries the magnitude on every row, whatever the verdict,
so work item five can re-choose the threshold from the log. It adds `Threshold` and `Measure` to the
rule interface, so the number written down comes from the same object that produced the verdict and
the two cannot describe different functions. The row rule does not get a magnitude, because it does
not have one - it answers with a row.

Both rules are injectable through the internal test constructor, which is what makes the seam
provable rather than merely present.

**The tests, and the mutation watched failing.**

| Test | Mutation | Watched |
|---|---|---|
| `The_detector_obeys_the_row_rule_it_was_handed` | the direct function calls restored | red: a rule that opens everything left the session red |
| `The_detector_holds_red_when_the_row_rule_it_was_handed_says_nothing_was_gained` | as above | red: a rule that holds was overruled by real prose |
| `The_size_verdict_and_the_numbers_beside_it_come_from_the_same_rule` | as above | red: the log carried the shipped threshold, not the rule's |

All three were watched red before the wiring was written, and again under the mutation afterwards.
The size test hands in a rule with a threshold of 7 and a magnitude of 42 - numbers nothing else in
the product can produce - so a log row carrying them proves where they came from.

## Finding 4 - the shadow log ran before the state decision

**What changed, per the Architect's ruling.** Compute the verdict, apply the state, then append. The
check body is split out as `RunContentCheck` and the append is the last thing it does. The single
process-wide lock stays, as ruled.

A consequence worth stating: a check whose state write FAULTS now writes no row at all. That is
correct rather than a gap - the burst is restored and the retry writes one - and it is written into
the method's own comment rather than left for a reader to discover.

**The test, and the mutation watched failing.**

`The_shadow_row_is_written_after_the_state_decision_not_before` counts the rows from INSIDE the
state write, through a subscriber, and requires the count to be unchanged at that moment. Mutation:
move the append back above the state decision. Watched red, before the fix and again under the
mutation, with the count reading one higher than the baseline.

One thing was got wrong here and is recorded rather than quietly repaired. The first version of this
test waited for the state TRANSITION and then read the observation, which races: the transition
handler and the test thread run on different threads, and it reported "never observed" rather than
the ordering defect. It now waits for the OBSERVATION itself.

**The handoff note is corrected.** The sentence saying the log can never affect the session it
observes is replaced by the claim the code supports: the log is appended after the state write and
therefore cannot delay it.

## Finding 5 - the continuous-idle driver's unbounded shadow cost

**What changed, per the Architect's two rulings.**

*The check rides on a body change.* `OnBytesContinuousIdle` is now `ObserveBodyChange`, which answers
the question instead of acting on it, and all three call sites use the answer: a footer repaint on a
continuous-idle agent no longer schedules a check, no longer pushes a pending check out, and no
longer costs a screen read beyond the one the body comparison already did. The state rule for that
driver is unchanged - a body change still marks it active, an unreadable frame is still treated as
activity.

The honest cost is in the code comment: a reply on such an agent can now be sampled up to one
throttle interval into its drawing, because later footer bytes no longer push the check out. That is
the trade the ruling makes and it is written down rather than glossed.

*The log is bounded on disk.* Two numbers, in one named place in `TurnDetectionShadowLog`: four
megabytes per session file, after which it is rolled over with exactly one predecessor kept, and
fourteen days, after which a file nothing has written to is deleted. The sweep runs at most hourly
per process. It names what to DELETE - this directory, this extension, last write time read live -
and a file it cannot delete is reported with its path and tried again rather than passed over in
silence.

**The tests, and the mutations watched failing.**

| Test | Mutation | Watched |
|---|---|---|
| `A_footer_that_repaints_for_ever_costs_no_check_and_no_row` | schedule on raw bytes again | red: three footer frames wrote three rows |
| `A_session_file_that_reaches_the_size_bound_is_rolled_over` | the rollover removed | red: "the file passed the size bound and was never rolled over" |
| `A_session_file_inside_the_size_bound_is_left_whole` | (the control) | - |
| `A_file_older_than_the_age_bound_is_deleted_and_a_recent_one_is_not` | the sweep removed | red: "a file past the age bound was kept, so the log grows for ever" |
| `The_bounds_are_the_numbers_this_log_ships_with` | either constant moved | red |

The first was watched red before the fix, on a real Grok-declaring session driven through a settle
and three footer repaints. The retention tests could not be watched red before the fix, because the
code they call did not exist; their proof is the mutation, and each has a control in the opposite
direction so that a rollover on every append, or a sweep that deleted everything, would fail.

**The deferral-cap comment is corrected.** The claim that a burst longer than the window is never
sampled half drawn was false of this code. It now says what the cap actually buys: a possibly
half-drawn sample in place of an agent that is never judged at all.

## Finding 6 - the constants nothing pinned

Each one now has a test that fails when it moves, and - where the finding asked for it - a test
whose behaviour turns on the value rather than restating it. In the two threshold tests the literal
assertion is deliberately LAST, so that a mutation fires the behavioural assertion first; put first,
a restated literal would fire on every mutation and hide whether anything depended on the number.

| Value | Test | Mutation | Watched |
|---|---|---|---|
| 0.80 near-duplicate | `The_near_duplicate_threshold_is_eighty_percent_and_a_verdict_turns_on_it` | 0.80 -> 0.90 | red: "a row this close to a settled row is the same row redrawn" |
| | | 0.80 -> 0.70 | red: "a row this far from a settled row is new content" |
| 200 size threshold | `The_size_rule_starts_at_two_hundred_changed_characters` | 200 -> 201 | red: "200 changed characters is at the starting threshold" |
| | | 200 -> 199 | red: "199 changed characters is under the starting threshold" |
| 400 ms window, 3 s cap | `The_settling_window_and_its_cap_are_the_numbers_that_ship` | either moved | red |
| the cap, behaviourally | `An_agent_that_never_stops_writing_is_judged_at_the_deferral_cap` | `Math.Min` removed | red: no row was ever written while the chatter continued |
| every marker | `Each_declared_marker_is_the_only_reason_its_row_is_suppressed` | any single entry deleted | red: exactly that entry's case, and no other |
| the marker table itself | `No_declared_marker_is_left_without_a_row` | a marker added and not covered | red |
| the rule switch | `The_environment_switch_reaches_the_rule_the_detector_runs` | (was red before the fix) | red: the detector ignored the variable |
| the shadow default | `The_shadow_log_ships_ON` | `ResolveEnabled` inverted | red |

Three of these needed a production change to be provable at all:

- **The similarity bracket.** The examples in the file sat at about 0.98 and 0.30, so the threshold
  was free to move anywhere between them. Two new rows bracket it - one at 0.8667, one at 0.7925 -
  and their ratios are asserted from the ratio function first, so the test pins the threshold rather
  than repeating it.
- **The environment switch.** `ContentRuleDefault` was a static property initialised at type load,
  which no test could change, so "the switch reaches the detector" was unprovable and a detector
  that ignored the variable would have passed everything. It is now
  `ContentRuleFromEnvironment()`, called by the constructor. A detector is built once or twice in a
  process; reading one environment variable there costs nothing.
- **The shadow log default.** `Enabled` is mutable and tests move it, so reading it proves nothing.
  The resolution is now a named `ResolveEnabled`, and the value this process started with is kept
  separately as `InitialEnabled`.

**The two markers nothing exercised.** `auto-accept` and `IDE disconnected` are covered rather than
deleted, and the record now says what they rest on. They are NOT in the corpus evidence file: the
shipped list is the counted rows plus the rows the scoring harness's expression carried, and those
two came in with the second group. Their test rows are written from the agent's own interface
strings, the test says so, and `chrome-markers-evidence.md` now says so. They are the weakest two
lines in the list and are the first to delete if it is ever cut back.

**Every marker is now independently load-bearing.** The new theory requires each row to carry
EXACTLY ONE declared marker and requires the row to come back as gained content when that entry is
removed - so an entry can no longer be deleted while a neighbour on the same physical row keeps the
test green, which is how these two came to be unexercised. Where a real corpus row carries three
markers at once (Codex's background-terminal line) the row is trimmed to the fragment under test,
and that is marked in the table.

## What is NOT closed, and what this run does not cover

**`CcDirector.Gateway.Tests` still has no verdict on this change.** It was not run, on the
Architect's instruction: the machine-wide lock is contended and that suite is run immediately before
landing, after these fixes, so that the run describes the tree that actually merges. Nothing in this
change is outside `CcDirector.Core` and its two test projects, and every change to a type the
Gateway suites DO use is additive - see the correction below. That is a reason to think the risk is
low, not evidence the suite passes.

**`CcDirector.Gateway.UnitTests` was not run either**, for the same reason.

**CORRECTED BY THE ARCHITECT, 15 September.** The sentence above originally said both Gateway
suites "reference none of the changed types". That is FALSE and was caught by another session
reading this report rather than by anyone in this mission. `CcStorage` IS a changed type, and
thirty-three files under `CcDirector.Gateway.Tests` reference it. The true reason the risk is low
is different and narrower: **the change to `CcStorage` is purely additive** - one new static method,
`TurnDetectionShadow()`, with no existing member touched - so nothing those thirty-three files
already call has moved. That is a reason to expect the suite to pass. It is still not a verdict,
and the suite is run before this lands.

**Still nothing here is measured on live bytes or on the corpus.** Every caveat in the section above
stands unchanged. These six fixes make the rule correct and the interface real; they do not make it
scored.

**The retry bound is a judgement, not a measurement.** Three consecutive faults before a burst is
dropped is a number chosen to stop a spin, not one taken from any observation of how often a check
faults - because it has never been observed faulting at all.

> **SUPERSEDED, round two.** Two things above are wrong. The drop happened on the FOURTH consecutive
> fault, not the third - the comparison was greater-than and the prose never matched it. And the
> burst is no longer dropped at all: past the bound the turn OPENS. See finding 1 in the round-two
> section at the end of this report.

**The continuous-idle sampling trade is unmeasured.** Riding the check on a body change means a
reply on such an agent can be sampled up to one throttle interval (500 ms) into its drawing. That is
the ruling's intent and it is cheaper, but no measurement says how often a half-drawn sample changes
a verdict on that driver - there are two such episodes in the whole corpus.

## The gate

The default local run is green: 1,741 tests across eight projects, every one reporting
`outcome=Completed`. The focused parked tests for this change were run explicitly and are green:
53 tests across `ContentTurnRuleTests`, `TerminalSettledCaptureTests`,
`TurnDetectionShadowRetentionTests`, `ContinuousIdleStateTests`, `TerminalStateDetectorTests` and
`StateChangeLogTests`. `CcDirector.Core.UnitTests` is in the default run and carries the new pure
pins: 381 tests, green.

**One flake was found and chased rather than reported as green.** Running the three shadow-log test
classes together, about once in fifteen runs, `The_size_verdict_and_the_numbers_beside_it_come_from_
the_same_rule` reported that no shadow row had been written within five seconds - and both times it
happened on the first run after a rebuild, with a compile still running beside the suite. These
tests drive the detector's REAL timers, which is why they are parked, and a check that is merely
late under load is not the same fact as a row that was never written. The row wait is now fifteen
seconds instead of five, and its failure message lists what the directory actually held, so the two
cannot be confused again. What the test asserts is unchanged. Twenty-six consecutive runs since,
including eight with a build running alongside, are green. The cause is stated as scheduling latency
rather than proven to be: no run has been caught failing with the new diagnostic in place, so if it
returns, that message is what will tell the next reader which of the two it is.

> **CORRECTED, round two.** "So the two cannot be confused again" was false, and the inspection
> demonstrated it: a directory holding exactly the baseline rows is the same observation whether the
> row is late or will never be written, so a listing of files and sizes can never separate them. The
> longer wait is a tolerance change and diagnoses nothing on its own. A positive signal that DOES
> separate them - whether a check is still armed at the deadline - has since been added, and both
> sides of it were demonstrated. See finding 7 in the round-two section at the end of this report.

---

# Round two - the four findings the second inspection left open

The second inspection returned DISAGREE. Round-one findings 1 and 4 were closed; findings 2, 3, 5
and 6 were not, and a seventh finding named a false claim in the section above. Every one was
accepted, and the Architect settled the parts where the inspection had identified a problem without
choosing between the ways out.

Every fix below was watched failing first, with the mutation named and the message it printed. The
mutations were applied to the committed tree, so `git checkout --` restored the FIX rather than the
defect; the working tree was checked clean of every mutation afterwards and the focused suites
re-run.

## Finding 1 - the fourth consecutive fault no longer loses the turn

Past the retry bound the burst was DROPPED. A one-burst reply whose check faulted four times then
had no pending check and no later byte to make one, so the session sat red for ever - the exact
failure this whole design exists to prevent.

Past the bound the turn now OPENS. That is today's behaviour, it is always available, and it is the
conservative direction: a wrong open costs a blue session that settles again a quiet window later, a
wrong drop costs work that sits red until somebody happens to look at it. With the switch OFF
nothing opens, because the byte already flipped the session on the way in and the check only
observes - so the switch-off path stays byte for byte.

The failure count is deliberately not reset. While the fault persists every later burst takes the
same path and opens immediately, which IS today's byte rule; the first check that completes resets
it.

The off-by-one is gone in all three places the inspection named. The constant is renamed
`MaxCheckRetries` because it counts RESTORES, not failures: faults one, two and three put the burst
back and fault four stops retrying. The log line now prints the count it actually observed rather
than the constant, and the paragraph above claiming three has been corrected here.

- Test: `A_check_that_faults_past_the_retry_bound_opens_the_turn_rather_than_losing_it`. It
  reproduces the inspection's probe - one real burst, a rule that faults on every call, nothing
  afterwards - and also asserts the rule was asked more than once, so the fallback stays the last
  resort rather than becoming the first.
- Mutation watched red: the `MarkActiveFromContent` call removed, leaving the bare `return`.
  Reported `a check that faulted past the retry bound lost the burst, so the turn never opened`.

## Finding 2 - the faulted latch on both shipped activation paths

Not a regression from this mission. The same shape is on `origin/main`, and these are the paths
every Director runs while the switch ships off. `Session.SetActivityState` assigns `Working` and
only then calls its subscribers, so a subscriber that throws escapes from the middle of the write:
the session was left in `Working`, the latch set, and no idle countdown armed, after which every
later byte took the already-active branch and armed nothing.

**The remedy is not the one the ruling named, and the difference is load-bearing.** The ruling said
to apply the same latch release already used next door. Applied literally it closes nothing here:
`OnQuietCore` returns on its first line when the latch is clear, so a cleared latch plus an armed
countdown is a timer firing into nothing and the session stays blue exactly as before. That was
established by reading the code that consumes the latch, not assumed.

What closes it is arming the countdown WHATEVER happened above - the arm now sits in a `finally`,
which is what "restart the idle countdown on every byte" always meant. The latch release is kept but
made CONDITIONAL, in a helper with the reasoning written on it: it goes back only when the session
is not sitting in `Working`, because a fault before the state write leaves a latch that is a lie,
while a fault after it leaves a session that still owes a settle and only the latch can deliver one.
Reading the session's state is the only signal that separates the two, since the throw itself cannot
say how far the write got.

`MarkActiveFromContent` was given the same treatment rather than left alone. Its unconditional
release was survivable only because the check retry re-ran it, and finding 1's fallback now calls it
from a place where no retry follows.

- Tests: `A_faulted_state_write_on_the_byte_path_does_not_leave_the_session_stuck_working` and
  `A_faulted_state_write_on_the_body_path_does_not_leave_the_session_stuck_working`. Both reproduce
  the inspection's probes: a settled session, ONE burst, a state-change subscriber that throws once,
  and nothing afterwards to rescue it. The body-path test runs as Grok, whose idle terminal never
  goes byte-silent, so it goes through the other method.
- Mutation watched red, separately for each: the `finally` emptied so a throw skips the arm. Both
  reported `the session never came back to red, so the faulted write left it latched Working`.

## Finding 3 - the seam's guarantees

Two defects, two different answers, as ruled.

**The size verdict is now one answer.** `ITerminalSizeRule.Evaluate` returns a `SizeVerdict`
carrying the verdict, the magnitude, the threshold and the log line together, and `Measure` is gone
from the interface. The detector asks once. The three numbers cannot describe different
measurements, rather than being three calls trusted to agree. `Threshold` stays on the interface for
exactly one caller - the row written for an ambiguous frame, where no verdict exists to read one
from - and that is said on the property.

**The ambiguous-frame comment is corrected and the behaviour is not.** An unreadable screen, or one
with no settled side, short-circuits to the conservative open without consulting either rule. That
is right: it is not a question a content rule can answer, and asking one to compare an empty list
against an empty list would produce a verdict shaped like a measurement and meaning nothing. The
comment claiming both candidates are asked on EVERY check is replaced by one that says what happens
and why.

- Test: `The_size_verdict_and_its_numbers_come_from_one_call`, driven by a rule whose magnitude
  changes on every call, alternating above and below its own threshold - so a detector that asks
  twice writes a row that contradicts itself. It asserts the logged triple is self-consistent and
  that the rule was asked exactly once. Two pure tests in `TerminalContentNoveltyTests` cover the
  verdict in both directions, including that the magnitude is recorded when the rule HOLDS.
- Mutation watched red: the verdict taken from one `Evaluate` call and the magnitude from a second.
  The row said the size rule opened while carrying a magnitude of 4 against a threshold of 100.

## Finding 4 - the sweep deletes only what it can prove it wrote

The sweep enumerated every `*.jsonl` and deleted on age alone. An extension rules some things out;
it is not proof of ownership, and the inspection demonstrated the consequence by dropping an aged
`owner-notes.jsonl` into the directory and watching the next append delete it.

It now matches the two shapes the writer composes - a thirty-two character lower-case hexadecimal
session id, optionally followed by the predecessor's `.1`, then `.jsonl` - anchored at both ends. It
enumerates what to DELETE and never what to skip. Anything it does not recognise is left alone and
said so, once per sweep, because a directory quietly filling with files this log will never touch
should not look identical to one doing its job.

- Tests: `The_sweep_leaves_alone_an_aged_file_this_log_did_not_write` reproduces the probe and, in
  the same test, puts an aged owned file and an aged rolled predecessor beside it that must still
  go - a sweep that deleted nothing would otherwise pass. Nine table cases pin the name shapes,
  including the thirty-one and thirty-three character near-misses, upper case, `.2.jsonl`, a
  prefixed name and a `.jsonl.bak` suffix.
- Mutation watched red: the ownership test removed and the `*.jsonl` filter restored. Reported
  `the sweep deleted a file this log never wrote; an extension is a deny boundary, not ownership`.

## Finding 5 - the rollover

**A failed rollover no longer costs the row.** Rotation shared the append's one broad try, so a
`File.Move` that threw skipped the append entirely and the observation survived only in `FileLog`,
never in the shadow file the comparison is actually read from. Rotation now has its own catch and
the append goes ahead regardless. An oversized file is a far smaller harm than a missing
observation.

**The cross-process reasoning is written down, and it is stronger than the ruling's.** The ruling
said the file is per session and a session belongs to one Director. True, but the real reason comes
earlier: every path here resolves through `CcStorage`, whose root is `CC_DIRECTOR_ROOT` when that
variable is set, and it is set per Director INSTANCE. Verified rather than taken on trust -
`CcStorage.Root()` is `Base()`, `Base()` returns `CC_DIRECTOR_ROOT` when non-empty, five instance
roots exist side by side on this machine (default, devthrottledemo, slot-1, slot-5, slot-7), and
this session's own variable reads the first of them. So two Directors never share the shadow
DIRECTORY, let alone a file, and the per-session filename is a second independent reason rather than
the only one. Neither is a claim about the lock: the lock serialises threads inside one process,
which is all it is now claimed to do.

**The size bound is stated honestly rather than the algorithm tightened.** The size is tested before
the next record is appended, so a live file may reach the bound plus at most one record, and a file
rolled at that moment preserves the same overshoot. The cost per session is twice the bound plus at
most two records, not twice the bound exactly.

- Tests: `A_rollover_that_cannot_happen_still_writes_the_row` makes the failure real rather than
  mocked - a directory sits where the rolled predecessor would go, so `File.Move` cannot succeed
  while the live file stays writable. `A_live_file_can_pass_the_size_bound_by_at_most_one_record`
  measures the overshoot as it happens and asserts both that it exists and that it is within one
  record.
- Mutations watched red: rotation put back inside the outer try, which lost the row (2 lines
  expected, 1 found); and rolling over 500 bytes early, after which the overshoot test reported
  `the live file never passed the bound (581 bytes against 1000), so this test is not measuring the
  overshoot it claims to`.

## Finding 6 - the shipped defaults, not just the named constants

The inspection left every named constant alone, substituted the production defaults only, and all
102 focused tests stayed green. Fixed for all five values it named.

- The settling window and its cap: the detector now exposes what it is actually running, and
  `A_detector_built_the_way_the_product_builds_one_runs_the_shipped_timings` builds one through the
  public constructor with no timings and compares. Mutation watched red: the constructor's fallback
  changed to 401 milliseconds, leaving both constants untouched.
- The retention size, age and sweep interval: the live fields are asserted against the shipped
  constants where nothing has moved them - this class's constructor does not touch them and every
  test in the collection restores what it found. Mutation watched red: the live size initialised to
  five megabytes with `DefaultMaxFileBytes` left alone.
- The shadow log's enabled default: compared against the value read before this class turns the log
  on. Mutation watched red: `Enabled` initialised to a bare `false` with `ResolveEnabled` and
  `InitialEnabled` untouched.

## Finding 7 - the false claim about late and never

The claim was false and is withdrawn. Lengthening the wait from five seconds to fifteen changes no
production behaviour and establishes no cause; it makes the flake less likely, which is a tolerance
change. A target file holding exactly the baseline rows is the same observation whether the row is
late or will never be written, so the directory listing could never separate them.

A positive signal was cheap, so it was added rather than the limitation merely admitted. A check
that is still ARMED at the deadline is unique to "late": the callback is scheduled and has not
fired. No check armed and no row means nothing is going to write one. The detector exposes that
fact, the wait helper reports it, and every call site now passes the detector so the answer is
available to all of them. A caller that does not gets the honest sentence saying the two cannot be
told apart, never a guess.

Both sides were demonstrated on the SAME directory state, which is the whole point:

- Appends suppressed entirely: `the directory holds (the directory does not exist); NO check is
  armed for this session, so no row will ever be written for this burst`.
- The check timer armed sixty seconds out: `the directory holds (the directory does not exist); a
  check IS still armed for this session, so the row is LATE rather than absent`.

While doing this it turned out every transition wait in these tests used `Task.WaitAsync`, which
throws before `Assert.True` can look at the result - so each one reported `The operation has timed
out` and discarded the sentence its author wrote. The first mutation proof of finding 1 printed
exactly that and nothing else. A waiter that answers instead of throwing replaced it, which is why
the messages quoted above exist at all.

## The gate, round two

- `.\scripts\test-local.ps1`: green. 1,742 tests across eight projects, every one reporting
  `outcome=Completed`.
- `CcDirector.Core.Tests` in full, the parked suite this change lives in: 4,471 passed, 8 skipped,
  0 failed, 8 minutes 10 seconds.
- `CcDirector.Gateway.UnitTests`, the other parked suite the coverage gap named: 4,296 passed, 0
  failed, with `HostedSchemaRefusesAnUnownedRowTests` excluded.
- `CcDirector.Gateway.Tests` has NO verdict on this change and is not called green. Its machine-wide
  lock is unwinnable here today (issue #2862) and the mandate excluded it.

**What is NOT covered, said plainly.** The eight excluded Gateway tests are PostgreSQL-backed and
fail with `Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:55432` - the throwaway database
only a `-Parked` run provisions, and a `-Parked` run would also take the lock this mandate forbids.
They are environmental, not a verdict on this change: the whole branch touches no Gateway file, and
every product change is under `CcDirector.Core`, checked against the true merge base rather than
assumed.

Still uncovered, unchanged from round one: live terminal bytes, the labelled corpus, a real
two-process rollover - which the reasoning above argues is unreachable rather than merely untested -
and the parked Gateway suite.
