# Phase 2 proof - the way down screens

Written by the phase 2 Tech Lead, second seat (session c6c50eeb), on 20 September 2026. The first seat
(session number 120) opened the first two Developers and wrote the baseline below; it was ended while
waiting, because on this Director a waiting session is never woken (product issue 3186).

DRAFT until task 3 is merged; the word DRAFT leaves this line only then. Task 3 is built, reviewed and
HELD: see "The hold on task 3" below.

## The check

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests

A filter that matches nothing exits green, so the COUNT is what is read, never the colour. Every number
below is from a run made by a Tech Lead seat in the foreground with a full build, not taken from a
Developer's report.

## Counts

| When | Commit | The `SmartRestart` filter | The whole project |
|---|---|---|---|
| Before the phase (first seat) | `origin/main` = `0f084d60b` | 0 tests ran | 554 passed, 0 failed |
| The dialog branch as first pushed | `277498f16` | 25 passed | 579 passed, 0 failed |
| The progress branch as first pushed | `e3e66e110` | 14 passed | 568 passed, 0 failed |
| The dialog after its review finding, rebased on `912340ed8` | `70b56e819` | 25 passed | 624 passed, 0 failed (main alone: 599) |
| The rebuilt progress screen, trial merge into `642482c46` | tree `cc44f3307` | 46 passed (25 + 21) | 645 passed, 0 failed |
| `origin/main` after both merges | `8b296fe48`, the SAME tree `cc44f3307` | 46 | 645 |
| Task 3, the swap, as first pushed (engine branch merged in) | `52c5c2b54` | 87 passed | 686 passed, 0 failed |
| Task 3 after its review finding, main merged in | `43ac8c3e8` | 95 passed | 694 passed, 0 failed |

The last two rows are one run: the squash merge of pull request 3191 produced exactly the tree the check
had been run on, compared by tree identifier.

That the check can fail was shown four times, each with a full build: a hand-written
`InitializeComponent` (the defect of the old window) turns 18 of the dialog's 25 tests red, repeated
independently by the Reviewer with the same numbers; the same mutation turns 18 of the rebuilt screen's
21 red; removing the screen's unsubscribe turns 2 of 21 red; and in the first build of the screen,
removing the move to the interface thread turned 1 of 14 red after the test was rewritten to be able to
see it.

## Pull requests

| Pull request | What | Merged as |
|---|---|---|
| 3189 | The Smart shutdown dialog, a window nobody calls yet | `642482c46` |
| 3191 | The shutdown progress screen, a view nobody calls yet | `8b296fe48` |
| none yet | Task 3: the swap of the two doors, the operating system shutting down, the two old windows removed. Branch `smart-restart-p2-swap` at `43ac8c3e8`, pushed | HELD, see below |

## The hold on task 3

The swap is built, checked by the Tech Lead and reviewed, and it is NOT merged, on purpose. The fourth
review found, and the Tech Lead confirmed by reading `origin/main`, that the engine on main still answers
"not built yet" for three things the two doors need: the purpose Restart (`DirectorSmartShutdown.cs`
line 132), `ShutDownIgnoringAllAsync` (line 173) and `RecordAndLetEndAsync` (line 182). The headless
tests are green because they fake the engine, so the gate in mission section 8 would let the swap merge
with the File menu door and the ignore-all choice as dead ends, in place of a close dialog that works
today. The swap merges when phase 1 has landed all three: then `git merge origin/main` into the branch,
the check again, a pull request, a squash merge. What the swap's tests prove, its revert proofs, its four
pictures and its decisions are in `attachments/phase-2/swap-proof.md` on that branch; its change to the
main window is 26 lines added and 39 removed over the two files.

## What each new test proves, in plain words

The dialog (25 tests, `SmartShutdownDialogTests` and `SmartShutdownSessionReaderTests`):

- The window opens and every named control is connected. This is the test the old Drain window never
  had; with the old window's defect put in, it fails.
- What the owner is shown: "9 sessions are running", "1 session is running", the count working against
  waiting, and the sessions with a question box open, by name, under "Answer these first?" - a section
  that is absent when there are none.
- The explanation says all five things the owner asked it to say, and the number in it follows the
  dropdown. The dropdown holds exactly 5, 10, 15, 30 and 60 minutes; ten is the default.
- Smart shutdown is the default button and the one Enter takes. Each of the three answers comes back from
  a real click or key on the opened window: smart shutdown with the time picked (ten, and thirty after
  changing the real dropdown), ignore all sessions, cancelled. Escape and the window's own close are
  cancelled.
- The two doors differ in the title and the confirm button's words and in nothing else drawn.
- A dialog for zero sessions refuses to be built rather than show "0 sessions".
- The reader of real `Session` objects: working and starting are working; waiting, idle and waiting for
  permission are waiting; an open question box is seen; an exited session is left out; and the counts the
  dialog shows come from that reading.
- The four pictures are drawn, not blank: each frame must hold the dialog's own colours and more than
  fifty colours.

The progress screen (21 tests, `ShutdownProgressViewTests` and `ShutdownProgressScreenshotTests`):

- The view opens inside a window and every named control is connected.
- The engine's words appear word for word - the count, the phase, the note, each row's state and detail -
  and a count label that disagrees with its rows is still shown as given, so a screen that counted for
  itself would fail. A second snapshot replaces everything the first showed.
- All ten states draw, each with a colour; an unknown state or phase throws rather than being drawn as
  something plausible. A session under a lead is drawn beneath it and indented.
- Each button is live only while the engine says it may be used; at the limit "Cancel and keep working"
  is drawn dead and a real click calls nothing. One press sends one request and the button is dead
  before the next snapshot, even if a later snapshot offers it again.
- The time left is the screen's own clock against the engine's limit, never below zero, re-read when the
  limit moves, and shown only while sessions are being given time. No test sleeps.
- The end of the run: each of the six outcomes shows the engine's own reason and tells the caller exactly
  once; a failed run leaves no live button; a run already over still reaches a caller that subscribes at
  once; the screen ends on the result's own final snapshot.
- A change raised on a background thread changes everything bound on the interface thread: the thread of
  every change is recorded and asserted, and there must be changes to assert.
- Taken out of its window, the screen lets go: the run has zero subscribers, the clock is stopped, and a
  snapshot and a completion pushed afterwards change nothing.
- The eight pictures are drawn, not blank.

## Screenshots

Under `attachments/phase-2/`, rendered by real Skia from the headless tests, merged with the code:

- the dialog: from the window close with no question boxes; from the File menu; with two question boxes
  open; with the time allowed changed to 30 minutes;
- the progress screen: just started; midway with every state; after two thirds with interrupted rows; a
  could-not-be-asked row with its reason; ending at the limit with cancel dead; cancelling; finished and
  emptied; failed with the engine's reason.

Neither Reviewer could view images. The Tech Lead looked at the pictures: they are drawn, dark, styled as
`docs/VisualStyle.md` asks, and say what the list above says.

## Reviews

All by GLM 5.3 in Pi, a different agent from the one that built the work. Codex, the default Reviewer,
had hit its usage limit until 22 September; both Codex seats were ended before they read anything.

| Review | Of | Findings | Answered in |
|---|---|---|---|
| `review-phase-2-1.md` | the dialog at `277498f16` | one: its result type had the same name as the engine's. Accepted; renamed `SmartShutdownChoice` | `review-phase-2-1-answers.md` |
| `review-phase-2-2.md` | the progress screen at `e3e66e110` | two: it never let go of the run; its invented interface could not carry the engine's words, permissions, three of its states or the end of a run. Both accepted; the screen was rebuilt on the real `ISmartShutdownRun` | `review-phase-2-2-answers.md` |
| `review-phase-2-3.md` | the rebuilt screen, as a trial merge into main | none | - |
| `review-phase-2-4.md` | the swap at `52c5c2b54` | two: with no engine the window closed with live sessions and no question (accepted; the same dialog now opens with the smart choice dead and the reason, and ignore-all carries the close on); and the engine on main is not finished (accepted as the hold above) | `review-phase-2-4-answers.md` on the swap branch |

One correction to `review-phase-2-2.md`: it says the phase 1 types had not landed. They had (pull
request 3182); the engine behind them had not.

## Decisions the Tech Lead made

- The owner's "the progress screen that comes up in both cases" (mission 4.4) is read as both DOORS, the
  window close and the File menu, not both kinds of shutdown. Shutting down and ignoring all sessions
  "ends everything at once" and the engine gives it no run, so the progress screen has no ignore-all
  kind. This also closes the first Developer's two open points about that kind.
- The reviewer's note about the null-forgiving operator in test code was declined: the same pattern is
  already on main in that project and no harm was shown.

## What could not be reached

- `Session.PendingInteraction` is documented in its own file as never populated by the product today:
  its only source, the Claude Code hook path, was removed. The dialog reads it and the reading is tested
  on a real `Session` with the value set, but on a real Director the "Answer these first?" section will
  not appear until something populates that property again.
- No test opens the real main window, and nothing here ran against a real Director or the real engine.
  That is phase 5, on the isolated rig.
- The window's own title bar and its X: the headless platform has no window frame. The two titles are
  asserted; the X is driven as `Close()`.
- The one-second clock firing by itself: the tests call what the timer calls.
