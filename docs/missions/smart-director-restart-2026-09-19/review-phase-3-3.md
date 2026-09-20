# Review - phase 3, Reviewer 3: the two windows of the way up

Written by the Reviewer seat opened by the phase 3 Tech Lead (session 38f41a97), 20 September 2026.
I did not write this code, I run a different agent from both Developers that did, and I changed no
product code. My worktree was `D:/ReposFred/devthrottle-smart-restart-p3-review3`, detached at
`2a1a210cf`, the exact commit of pull request #3208 this review is of.

## The verdict in one paragraph

I found no defect in the windows themselves. Every rule the mandate told me to attack - the client is
dumb, the start-up ask asks once off the interface thread and stays quiet when it must, Not now writes
nothing, a press reaches the engine once, the reopen button names its own seat - is held in the code and
I failed to break any of it by reading or by experiment. I have two findings, both LOW and both gaps in
the tests rather than defects in the product: the history's Bring back button is never pressed by any
test, and the offer window's reopen button has no two-button test of its own. I agree with both decisions
the Developers flagged and the Tech Lead accepted, and I judge the flaky test fix sound.

## Scope

Reviewed: pull request #3208, branch `smart-restart/p3-way-up-windows`, at `2a1a210cf`, by reading
`git diff origin/main...HEAD` (4,114 insertions, all under `src/CcDirector.Avalonia`,
`src/CcDirector.Avalonia.Tests` and the mission's documents), every changed file in full, the mission
document sections 5.3 items 10 and 11 and rulings 10.2 to 10.4, both Developer mandates, both proof
files, and `docs/VisualStyle.md`.

NOT in my scope, and not re-reviewed:

- the way up engine (`IDirectorWayUp`, `WayUpWords`, `ControlApiHost.CreateDirectorWayUp`). It is on
  main and was reviewed twice. I read only the two functions the windows call into - `GatewayRefusal`
  and `ReopenOffer` - to judge the two flagged decisions, and I found nothing wrong in them. Anything
  else in the engine is outside this review.
- the phase 1 and phase 2 screens, the drain, the restore, the command line tools, the Gateway.
- whether a reopened conversation really restores a session's context: that is the engine's own open
  question, measured on the rig in phase 5, and nothing in these windows can answer it.

## What I ran, and every count

| Check | Result |
|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | Passed: 145, Failed: 0, Total: 145 |
| `dotnet test src/CcDirector.Avalonia.Tests`, run 1 | Passed: 744, Failed: 0, Total: 744 |
| `dotnet test src/CcDirector.Avalonia.Tests`, run 2 | Passed: 744, Failed: 0, Total: 744 |
| `dotnet test src/CcDirector.Avalonia.Tests`, run 3 | Passed: 744, Failed: 0, Total: 744 |
| `dotnet test src/CcDirector.Avalonia.Tests`, restore run after the mutation (full build) | Passed: 744, Failed: 0, Total: 744 |
| `dotnet build cc-director.sln` after the restore | 0 warnings, 0 errors |

Every run reached its end and printed its total, so none is an aborted run reporting a partial count.
My numbers match the Tech Lead's own on this exact commit: 145 on the filter, 744 on the whole project,
zero failed, and a clean build.

## My revert proof

I broke the once-only latch of the start-up ask: in `WayUpStartUpAsk.OnConnectionChanged` I made the
guard reset itself so every Connected starts an ask.

    Failed CcDirector.Avalonia.Tests.SmartRestart.WayUpStartUpAskTests.OnConnectionChanged_ConnectedTenTimes_AsksOnceAndShowsOnce
    Failed CcDirector.Avalonia.Tests.SmartRestart.WayUpStartUpAskTests.OnConnectionChanged_DisconnectedThenConnectedAgain_StillAsksOnlyOnce
    Failed!  - Failed: 2, Passed: 143, Skipped: 0, Total: 145

Exactly the two once-only tests went red, and nothing else noticed - so those two, and only those two,
hold the rule. I restored with `git checkout --` (the tree was verified clean afterwards), ran a full
build (0 warnings, 0 errors) and the whole project on the rebuilt binaries: 744 passed, 0 failed. That is
the fourth whole-project run in the table above.

ONE MUTATION OF MINE CHANGED NOTHING, and I say so loudly. My first attempt replaced
`Interlocked.Exchange(ref _asked, 1)` with a check-then-set pair - semantically identical in the absence
of concurrency - and the filtered run stayed green at 145. That is expected rather than a gap in the
suite: the tests drive the latch sequentially, so its thread safety is untested. The guard still holds
every sequential call, which is what the tests can reach, and the real caller (`WatchForRestartOffer`)
subscribes once to one monitor, so the concurrency is bounded. Benign, and worth knowing.

## One question I settled by experiment rather than by guessing

The offer window's Not now button is the cancel button, and every answer is disabled while the engine is
working. Whether Escape can close the window in the middle of a bring back was unknown: no test covers
it, and I could not answer it from the Avalonia documentation I have. So I wrote a throwaway test in the
test project (not product code): a bring back held on a gate, the real button pressed, Escape sent while
the call was in flight. The window SURVIVED - Avalonia does not fire a cancel button that is disabled -
so there is no path where Escape closes the window mid-restore and the owner loses the result. The probe
is deleted and the tree was verified clean afterwards. It is not committed anywhere.

## What I attacked and failed to break

1. THE DUMB CLIENT RULE. Every sentence on both windows is the engine's, carried on the records and
   shown as given: the headline, the when, the reason, the count, each row's title and detail, each
   seat's sentence, each reopen offer and every result. I searched both XAML files and every view model
   for a conditional that decides what a state means, for any count re-derived from rows, and for any
   invented sentence, and I found none. The disagreeing-label test is genuine: it feeds the window a
   record whose every label contradicts its own numbers and asserts the window draws the labels anyway,
   and the first Developer's mutation 2 proved that test is the one that holds the rule. An empty string
   where a sentence belongs: `WayUpReopenViewModel.OfferText` falls back to an empty string only when the
   engine says a seat can be reopened but supplies no button words, and the engine's own
   `WayUpWords.ReopenOffer` never does that - I read it. Not a defect.

2. THE START-UP ASK. It asks once: the latch is held, `WatchForRestartOffer` is called from
   `TryAttachGatewayMonitor`, that method is guarded by `_gatewayMonitor is not null`, and `_gatewayMonitor`
   is assigned exactly once and never reset, so the ask is built exactly once per process. It asks off
   the interface thread (`Task.Run`, and the fake engine records whether any call ever arrived on the
   interface thread). It shows nothing on nothing-waiting, on a refusal, on a throw, and on an answer
   that claims to be offered with no record - each with its own test. One boundary worth naming, which is
   the Developer mandate's own wording rather than a defect: the ask fires on the FIRST Connected,
   whenever it comes. A Director that starts up disconnected and connects hours later shows the offer
   then, in front of a working owner. The mandate told the Developer exactly that ("the first time that
   monitor reports Connected"), and the ten-Connected test shows it is still once, so I do not call it a
   finding.

3. NOT NOW WRITES NOTHING. Not now, Escape and the window's own close all call `Close()` and nothing
   else; two tests assert the engine was asked for nothing, and my Escape probe above adds that Escape
   cannot even close the window while a call is in flight.

4. PRESSING TWICE. A bring back is double-guarded: the buttons go dead the moment the call starts, and
   `BringBackAsync` itself returns early when busy or answered. A reopen is guarded by its own
   in-flight flag and the whole screen's busy flag. The tests hold both.

5. THE REOPEN BUTTON NAMES ITS OWN SEAT. Both windows find their row from the sender's own data context,
   never by position, and the seat id travels on the shared `WayUpReopenViewModel`, which builds the
   engine request itself. It is genuinely one class and not two copies: whether there is a button, what
   it says, the sentence beside it and the call are all decided in `WayUpReopenViewModel`; what is
   duplicated is only the event handler glue each window needs, which is five lines each and holds no
   rule. The history has the strong test (two reopenable seats, pressing the second names the second);
   see finding 2 for the offer window's half of that.

## My judgement of the flaky test fix

`WorkTheLastPressStarted` is sound. The press handler starts the work, assigns that task to the property
BEFORE awaiting it, and then awaits it - so a test reading the property after the press always holds the
press's own task, and there is no window in which it can assert before the engine has been called. It is
the same task the handler awaits, so a failure is raised exactly once, on the dispatcher, and is never
left unobserved; in tests the same task is awaited a second time, which observes rather than consumes
and is correct. No sleep, no retry: the fix waits for the thing it cares about.

I checked every other test that presses a button. `BtnBringBack_Clicked_ReachesTheEngineWithThisRecord`
and `BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat` on the offer window, and
`BtnReopen_Clicked_ReachesTheEngineNamingThatSeatAndShowsItsAnswer` and
`BtnReopen_OnTheSecondSeat_NamesTheSecondSeatAndNeverTheFirst` on the history window, all await
`WorkTheLastPressStarted`. The tests that press Not now and Close press buttons that start no engine
call and need no wait. The tests that call the view models directly await the returned tasks. I found no
other test racing the pool. I could not reproduce the failure before the fix either - my four whole
project runs were all clean - but the second Developer observed the cause directly under a starved
thread pool, and the reasoning above holds without it.

## The two flagged decisions: I agree with both

THE BUTTON WORDS AND WINDOW TITLES ARE CONSTANTS IN THE WINDOW ("Bring back", "Not now", "Close", "Bring
back...", "Restart history", "Reading..."). I agree they may stay there. They are the mission document's
own words and the File menu item's own words; they never change with any state, so they are labels and
not verdicts, and critical rule 7 forbids verdicts, not labels. They are held as named constants and
bound, so there is one spelling of each. The "Reading..." line is chrome with a lifetime of one engine
read, replaced by the engine's sentence the moment it arrives and never shown beside it, which is what
the responsive interface rule demands.

`NoHostWayUp` SUPPLIES ONE REASON STRING, fed into the engine's own `WayUpWords.GatewayRefusal`. I agree
with this too. The window does not word the sentence; it supplies a fact (this Director's own host has not
finished starting) to the same function the engine calls when the Gateway does not answer, so there is
one sentence for "nothing could be read" and not two. The alternative - a menu item that silently does
nothing - reads as broken. If the engine ever grows a state of its own for this, it is a one-line move;
nothing here blocks it.

## The findings

### Finding 1 - LOW, a test gap. Nothing presses the history's Bring back button.

`src/CcDirector.Avalonia/SmartRestart/RestartHistoryWindow.axaml.cs`, line 97 (`BtnBringBack_Click`) and
line 160 (the `await ViewModel.LoadAsync()` after the offer window closes).

The button is drawn and its words are asserted, and the offer it builds is tested by calling
`BuildOffer()` directly - but no test CLICKS the button. The whole chain behind it is held by code alone:
the click handler, the offer window opened as a dialog over the history, and the re-read of the history
afterwards. If somebody deleted the re-read at line 160, no test would go red.

What it would do to a person: after bringing sessions back from a record, the history behind the offer
would keep saying that record still owes seats - a window that lies about the record, minutes after the
owner used it. The engine holds the safety line (a second press would reach an engine whose record says
the seats are already back, and its refusal is what the owner reads), so nobody would get a second copy
of a session; the harm is confusion and a false promise, not damage.

How sure I am: certain, from reading. The test list is complete and none of the eighteen history tests
presses the button; I did not mutate it, because one mutation is what the mandate allows and the
once-only latch was the higher-value break.

### Finding 2 - LOW, a test gap. The offer window's reopen button has no two-button test of its own.

`src/CcDirector.Avalonia/SmartRestart/WayUpOfferWindow.axaml.cs`, lines 129 to 148 (the click handler),
and `src/CcDirector.Avalonia.Tests/SmartRestart/WayUpOfferWindowTests.cs`, line 536
(`BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat`).

The history window has the strong test: two reopenable seats, press the second, the engine is told about
the second. The offer window's test has a fixture with exactly one reopenable row, so the rule that the
button carries its own row is held on that window only by a fixture that cannot show it failing: most
lookups by position would find the same row and the test would stay green. The second Developer's
mutation 2 proved the HISTORY'S rule is held; the offer window's half of the same rule is held by nothing.

What it would do to a person: only if somebody rewrote the offer window's handler to find its row by
position, nothing would catch it, and a person could reopen the wrong saved conversation - the exact
defect the history's test was written to stop. The risk is small (the glue is five lines, the mechanism
is identical to the history's, and the shared class holds the seat id), which is why this is LOW.

How sure I am: certain, from reading.

### Note, the record, not the code: the "thirteen lines" in MainWindow are fifteen.

`git diff origin/main...HEAD --numstat -- src/CcDirector.Avalonia/MainWindow.axaml.cs` says 15
insertions, 0 deletions. Both proof files and the second mandate say "thirteen lines". The two extra
lines are comment lines. Nothing turns on it; the change to that file is still the two blocks the proofs
describe and nothing else, which I verified by reading the whole diff of it.

## The state of the tree, worth the Tech Lead's eyes

`origin/main` moved two commits past this branch's merge base after the merge: `bedfb7ce2` (pull request
#3218, the one repository list) and `8fcda423b` (the phase 2 mission record). The first touches
`ControlApiHost.cs`, which is why a two-dot diff against today's main shows engine files - that is
main's change, not the branch's; the three-dot diff, which is what this branch adds, touches no engine
file, no Gateway file and no contract, which I verified. No file those two commits change is a file this
branch changes, so the merge should be clean. Per the repository's own rule that a branch is stale the
moment main moves past it, #3208 needs `origin/main` merged in (and its checks run again) before it
merges - that is the Tech Lead's call, not a defect in the work.

## What I could NOT check

- THE PICTURES. I could not open them: my image tool returned that this model does not support images, so
  I did NOT look at any of the eight, and I say so plainly rather than implying I did. What I did
  verify: all eight files exist under the attachments folder with sizes from 8,781 to 52,424 bytes, and
  the picture test that ran green asserts each frame carries the window background, the panel background
  and more than one hundred distinct colours. Whether they look RIGHT, and whether they show what the
  proofs say they show, is unchecked by me. The Tech Lead will have to look, as the mandate anticipated.
- No Gateway, no Director, no session and no real record was run by me. Every test drives a fake engine
  with the test's own words, so the real engine's sentences on a real screen are unproved here - the
  engine's own tests prove its words, and the rig run in phase 5 proves the whole flow. Both proof files
  say this honestly already.
- `WatchForRestartOffer` itself (the wiring in `MainWindow.axaml.cs`) has no test, exactly as both proofs
  say: that the File menu item appears, that pressing it opens the history, and that a real Director's
  start-up reaches the ask, are first exercised on the rig. I confirmed by reading that the two calls
  land in tested code, and that the attach point runs once.
- The starved-thread-pool run of the fixed test, which the second Developer set out to get and could
  not finish: I did not attempt it either. Four clean whole-project runs plus the reasoning above stand
  in its place.
- The parked suites, the web tests and the Python tests were not run. This branch touches none of
  them, but that is my reading, not a run.
- Escape on a real window frame: the headless close is `Close()`, which is what the frame's X does under
  the headless platform, per the existing tests. I probed Escape mid-flight on the headless platform
  only.

## What happens to these findings

Not mine to decide. Both are test gaps, not product defects, and neither blocks the pull request in my
judgement; the seat that built the work answers them.
