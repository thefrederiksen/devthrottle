# Review - Smart Director Restart - phase 2, task 2 second round: the rebuilt shutdown progress screen

Reviewer seat for the trial merge `8a2d5d6ea` (branch `smart-restart-p2-progress` at `8ee649166` into
`origin/main` at `642482c46`), opened by the phase 2 Tech Lead. This file is the review of the second
round: a fresh Developer rebuilt the screen directly on the real engine interface after the first
review (`review-phase-2-2.md`, two findings, both accepted).

## Scope

What I read:

- The whole diff `origin/main...HEAD`: the view model (`src/CcDirector.Avalonia/SmartRestart/ShutdownProgressViewModel.cs`),
  the view (`ShutdownProgressView.axaml`, `ShutdownProgressView.axaml.cs`), the fake
  (`src/CcDirector.Avalonia.Tests/SmartRestart/ShutdownProgressFakes.cs`), the twenty-one tests
  (`ShutdownProgressViewTests.cs`, `ShutdownProgressScreenshotTests.cs`), the eight pictures, the
  Developer's answers (`review-phase-2-2-answers.md`) and proof (`attachments/phase-2/progress-screen-proof.md`).
  Both were read as claims, not as evidence: every point below was verified in the code.
- The real interface on `origin/main`: `src/CcDirector.ControlApi/SmartRestart/ISmartShutdownRun.cs`,
  and the settled interface document `phase-1-interface.md`.
- The first review `review-phase-2-2.md` and the Developer mandate
  `mandate-phase-2-developer-progress-findings.md` (read in the mission worktree).
- `docs/CodingStyle.md` (error handling, logging, naming) and `docs/VisualStyle.md` section 13.
  Rule 7 of the repository instructions (the client is dumb).

What I ran, all in the foreground, nothing in the background, no sub-agents, no tracked file changed:

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  46 passed, 0 failed. Matches the Tech Lead's run (25 are the dialog, already merged; 21 are this
  screen).
- `dotnet test src/CcDirector.Avalonia.Tests` (the whole project): 645 passed, 0 failed, 1 minute
  9 seconds. Matches the Tech Lead's run.
- `git diff origin/main...HEAD` for `HeadlessTestApp.cs` and the test project file: EMPTY. The branch
  adds nothing there that main does not already have, which is what the mandate requires now that the
  dialog (pull request 3189) has merged first. The `Avalonia.Skia` package reference is gone with it.
- Searches for what must be gone: `IShutdownProgressSource`, `ShutdownProgressKind` and any use of
  `ISmartShutdown` (the engine, rather than the run) appear nowhere in the screen or its tests. No
  test sleeps; the clock is driven by a fake time provider and direct calls.

What I could not reach:

- The eight pictures. The model running this seat cannot view images in this session, so the pictures
  were checked only by the test's distinct-colour presence check (a blank frame fails it, over 100
  distinct colours demanded) and by the Developer's own statement that he looked at each. That a
  picture is not blank is proved; that it looks right is not, by me.
- The real engine. It has not landed; everything is against the real types and a hand-stepped fake
  that implements the real interface. The honesty of the words, the row order and the two permission
  booleans is the engine's to keep.
- The two revert proofs in the Developer's proof file. Re-running them needs a tracked file changed,
  which this mandate forbids. I verified the letting-go one by reading instead, below.
- The one-second clock firing by itself. No test sleeps; the tests call the same method the clock
  calls. Consistent with the code.

## Findings

None. Within the scope above I found nothing that breaks, for anyone, that must change. Each point
the mandate told me to look hardest at was followed into the code, and the results are written down
below so the check can be argued with rather than taken on trust.

## The two findings of the first review, answered in the code

**Finding 1 (the view model never let go).** Answered in the code, not only in the answers file.
`LetGo` (`ShutdownProgressViewModel.cs` lines 240 to 246) unsubscribes from `Changed`, stops the
one-second clock, unsubscribes the clock's tick and cancels the token that holds the continuation on
the run's `Completion` task. `Dispose` (lines 229 to 238) runs it once, guarded against a second
call. The view calls `Dispose` in `OnDetachedFromVisualTree` (`ShutdownProgressView.axaml.cs` line
52), and the screen also lets go by itself when the run completes (`CompletePosted` calls `LetGo`,
line 288), so a screen whose view is never detached still stops when the run does.

The letting-go test does not pass by an absence. It first pins three presences while attached: one
subscriber on the run, a running clock, and a snapshot that did reach the screen
(`ShutdownProgressViewTests.cs` lines 564 to 569). After the view is taken out of its window it
asserts zero subscribers (line 575), and only then pushes a snapshot and a completion from another
thread - something IS pushed - and asserts the count of arrivals is still one, so nothing was posted,
and nothing shown moved (lines 577 to 592). If the unsubscribe at line 242 were taken out, the
subscriber count would stay one and the arrival count would become two; the test goes red on a
presence, not on nothing happening. The Developer's revert proof (2 failed, 19 passed without it) is
consistent with exactly that.

**Finding 2, part a (the buttons obey the engine).** Answered. `Apply` (line 316) sets each button
live only from the snapshot's own permission and a pressed flag (line 322 and 223 in the view model;
`CanShutDownNow = snapshot.CanShutDownNow && !_shutDownNowPressed`, and the same for cancel). A press
marks the button dead BEFORE the run's method is called (lines 194 to 196 and 210 to 212), and the
pressed flag keeps it dead against any later snapshot. The review's named case - the limit reached,
a cancel meaningless - is tested with both buttons drawn and dead and a real mouse click calling
nothing (test 7), and the one-press-one-call proof uses a fake that stays silent when pressed, so
only the screen itself can have killed the button (tests 9 and 10).

**Finding 2, parts b and c (the engine words, and the three missing states).** Answered. `CountText`,
`PhaseText`, `NoteText`, `StateText` and `DetailText` are the snapshot's own strings, shown as given;
`Describe`, `DescribeStatus` and `IsGone` are gone, and the screen computes no count anywhere (the
only counting in the file is the row replacement bounds). Test 2 sends a count label that disagrees
with its rows ("7 of 3" over one row) and asserts the label is what shows, so a screen that counted
would fail it. All ten states have a colour and an unknown state or phase throws (the `BrushFor`
switch and `IsCountingDown`, lines 331 and 418 onward); test 4 draws all ten and test 5 refuses
values 99. Rows are drawn in snapshot order, indented by `OwnerSessionId` (test 6 measures the indent
in the window at 20 pixels or more). A second snapshot replaces everything (test 3 asserts no word
of the first survives anywhere in the window), and a row whose record changed in any way is replaced
whole, so nothing from an older snapshot can survive inside a row.

**Finding 2, part d (phase, note and completion have no home).** Answered. `PhaseLabel` and `Note`
each have a line bound to the engine's own string. `Completion` is awaited without blocking: a
continuation posts to the interface thread, and `CompletePosted` (lines 274 to 302) shows the
result's `Detail`, applies the result's final snapshot, hides both buttons and the time left, and
raises ONE event, `Finished`, exactly once, on the interface thread, after the screen is already
showing the end state. All six outcomes are tested one by one, each showing its own detail and
raising `Finished` once with that result (test 15); a failed run leaves no live button and lets go of
the run (test 16); a run that was already over before the screen was built still reaches a caller
that subscribes at once (test 18). The screen closes nothing.

**Finding 2, part e (the ignore-all kind).** Answered, by removal. No trace of
`ShutdownProgressKind`, its picture or its tests remains anywhere in the screen or its tests.

## The race the mandate names: completion, a late change, and detach

Every change to anything bound happens in `Apply` or `CompletePosted`, and both run only on the
interface thread: a posted continuation of the run's `Completion` task and every posted snapshot.
The constructor verifies interface-thread access and subscribes before its first read, so a change
raised between the two is queued behind the first read and the newest snapshot is the one left
showing. Posts are first in, first out, so a snapshot raised before the completion is applied before
the completion is, and the completion then applies the result's own final snapshot. All the guards
(`_disposed`, `_isCompleted`) are written and read on the interface thread only, so no torn state is
possible. A snapshot posted before a detach but landing after it is dropped by the `_disposed` guard
in `ApplyPosted` (line 259), which the answers file states plainly.

`Finished` cannot be raised twice: it is raised only in `CompletePosted`, which sets `_isCompleted`
before it does anything else and returns on a second entry; both entries would run on the interface
thread, one after the other. It is raised never in exactly one case - the view was detached before
the run completed - and that is the designed letting-go: the continuation is taken off the task by
the cancelled token, and the test asserts no subscriber, a stopped clock and no `Finished` after a
detach (test 20). A caller that has taken the screen out of its window has said it no longer wants
to hear from it; the answers file records this as a decision, and I find nothing wrong with it.

## Points I checked and cleared

- **A press: one call, dead at once, no words of the screen's own.** The pressed flag and the dead
  button are set before the call (lines 194 to 196, 210 to 212), a second click sends nothing, and a
  later snapshot that re-offers the permission does not bring a used button back (tests 9 and 10).
  After a press the words on screen do not move until the engine pushes: the tests hold the fake
  silent and assert the phase text is unchanged. The screen's own sentences are down to two, both
  sanctioned: "Time left: ..." (the screen's own clock, which the settled interface gives to the
  screen) and "No reason was given." (cleared by the first review, kept, drawn and logged).
- **The time left.** The screen's clock against the snapshot's `LimitUtc`, clamped at zero, rounded
  up so it opens on the full time and reaches "0:00" only at the limit; a snapshot that moves the
  limit moves the time (test 13). Shown only in `Asking`, `Collecting` and `Interrupting`, and the
  test walks all eight phases, asserting there are eight, and that the time shows in those three
  and no others (test 14). No test sleeps: a fake time provider is advanced by hand.
- **The thread proof.** Kept and widened: a snapshot and a completion are raised from a real
  background thread (asserted to be one), and the thread of every row change, every property change
  and the `Finished` event is recorded and asserted to be the interface thread, with six named
  properties asserted to have changed at all - a presence check, not an absence (test 19).
- **Tests that hand-build their input.** Every test drives the real view in a real window and
  asserts on what the controls hold; the words in the snapshots are deliberately not words a screen
  would choose ("the engine says: ..."), so a label seen on screen can only have come from the
  snapshot. Button tests click with the mouse, because a raised event reaches a disabled button and
  a real click does not.
- **The catch in the two click handlers** (`ShutdownProgressView.axaml.cs` lines 63 and 76). They
  log and swallow. I weighed this against the no-fallback rule and cleared it: the handlers are
  entry points, where the coding style puts try-catch; the only thing inside that could throw is
  the run's own method, which the settled contract says returns at once and never throws, so the
  catch stands guard over a contract violation only, and throwing out of a click on a shutdown
  screen would kill the application mid-shutdown. The clock's tick handler is the same shape, and
  nothing inside it can throw. `ApplyPosted` and `CompletePosted` log and rethrow, which is
  fail-loud, and the throw for an unknown enumeration value is the Tech Lead's own decision.
- **The second-attach guard** (`ShutdownProgressView.axaml.cs` lines 37 to 46). A view that has
  left its window throws rather than being shown again as a dead screen that looks alive. Recorded
  as a decision in the answers file with the line to revisit if the integration finds the main
  window re-attaches content in normal life. Not tested, and stated as not tested. Nothing wrong
  with it as it stands.
- **The two read-only members for the proof** (`SnapshotsReceived`, `IsClockRunning`). They turn
  "nothing was posted" and "the clock stopped" into asserted facts rather than inferences. Harmless.
- **`HeadlessTestApp.cs` and the test project file.** Identical to `origin/main` - proven by the
  empty diff, not by the answers file's word. The `Avalonia.Skia` reference is gone and the pictures
  still draw, as the dialog branch found.
- **The visual style.** The disabled and hover colours in the markup match `docs/VisualStyle.md`
  section 13 exactly, including the disabled cursor change to an arrow. The same markup was cleared
  by the first review.

One claim in the proof is broader than its test: test 16's comment says a straggling snapshot cannot
put anything back, but no snapshot is pushed after a completion in any test. The claim still holds -
by the time the completion is shown the run has no subscriber, so a later push has no handler to
reach, and a snapshot posted before the completion is applied before it because posts are first in,
first out - but only the subscriber count is directly proved. Not a finding: nothing breaks either
way, and the unreachable path is unreachable.

Nothing found. The two findings of the first review are answered in the code itself, the screen is
dumb in the way rule 7 demands (it lays out the engine's words and decides nothing but layout,
colour, indent and its own clock), the letting-go is proved by presences that fail without the
unsubscribe, and the race the mandate names has one designed outcome and no path to a torn state, a
change off the interface thread, or a finished event raised twice.

END OF REVIEW
