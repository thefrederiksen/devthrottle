# Proof - phase 3, Developer: finishing the two WINDOWS of the way up

Written by the second Developer seat opened by the phase 3 Tech Lead (session 38f41a97),
20 September 2026. The first seat built the two windows; this one takes the engine's merge, draws the
history's reopen button, and names and fixes the flaky test.

- Branch: `smart-restart/p3-way-up-windows`, worktree `D:/ReposFred/devthrottle-smart-restart-p3-windows`.
- Pull request: **#3208**, against `main`. **It can merge now** - the engine is on `main`. It was not
  merged here and the hosted checks were not waited for, as the mandate says.
- My commits: `fbaa10fb5` (the merge) and `86fb25e6f` (the work), plus this document.

---

## The headline

| | |
|---|---|
| Task 1, the merge | Done. Nine engine files conflicted add against add; all nine take `main`'s side. One compile break, the one the mandate predicted. |
| Task 2, the history's reopen button | Done. Five new tests, one new picture, one new shared class so the two windows cannot drift apart. |
| Task 3, the flaky test | Done, and **the cause was observed, not guessed**: the assertion was racing the thread pool the press had just handed its work to. |

| Check | Before, on the merged tree | After |
|---|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 92 passed, 0 failed, 92 total | **97 passed, 0 failed, 97 total** |
| `dotnet test src/CcDirector.Avalonia.Tests` | 691 passed, 0 failed, 691 total | **696 passed, 0 failed, 696 total, eight runs in a row** |

Risen by 5 and by 5 - exactly the five tests written. The before-numbers are the mandate's own numbers
to beat, measured here on the merged tree rather than taken from the mandate.

---

## 1. Task 1 - the merge

`git merge origin/main`, with `origin/main` at `217b79f63`. The merge base was `8c81e7d63`. Nine files
conflicted, every one of them add against add: this branch carried the engine as it stood on the engine
branch at `0cda45788`, and `main` carries the squashed, reviewed engine.

```
src/CcDirector.ControlApi/ControlApiHost.cs
src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs
src/CcDirector.ControlApi/SmartRestart/IDirectorWayUp.cs
src/CcDirector.ControlApi/SmartRestart/IWayUpGateway.cs
src/CcDirector.ControlApi/SmartRestart/WayUpWords.cs
src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpBringBackTests.cs
src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpHistoryTests.cs
src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpOfferTests.cs
src/CcDirector.Gateway.UnitTests/Restart/WayUpTestRig.cs
```

**All nine take `main`'s side outright, and that is checkable rather than asserted.** The windows work
never changed a line of the engine: `git diff 8c81e7d63 HEAD` before the merge listed only Avalonia
files and this mission's documents. After resolving,
`git diff --stat origin/main -- src/CcDirector.ControlApi src/CcDirector.Gateway.UnitTests/Restart`
printed **nothing**, so those paths are byte for byte `main`'s.

### The one break, and it is the predicted one

    error CS7036: There is no argument given that corresponds to the required parameter 'Reopen' of
    'WayUpHistorySeat.WayUpHistorySeat(string, string, string?, string?, string, WayUpReopenOffer?)'

`WayUp.HistorySeat` in `WayUpFakes.cs` built one positionally. It now passes null - a seat that handed
over or has come back carries no offer - and a second builder, `WayUp.EndedHistorySeat`, makes a seat
that did end without a handover and so carries one. Nothing else in the merge failed to compile.

The other two engine changes touched nothing here, as the mandate said: no window fakes `IWayUpGateway`,
and no window builds `AgentPluginLaunchMetadata`.

---

## 2. Task 2 - the history's reopen button

### What the owner now sees

Every seat in the restart history that ended without a handover carries:

- the engine's own **button**, whose words are `WayUpReopenOffer.Offer`;
- the engine's **sentence** saying what pressing it would really do (`WayUpReopenOffer.What`), which is
  shown whether or not there is a button;
- and, once pressed, the engine's own answer (`WayUpReopenResult.Message`), drawn beside that seat.

A seat the engine says has no conversation shows the sentence and **no button**. A seat that handed over
or has already come back shows neither: the engine gives it no offer at all, and a button beside it would
start a second copy of a session the bring back is going to restore.

**It works on any record that holds such a seat**, including a cancelled one and one shut down ignoring
every session. I added no rule of my own about which records may show it; the engine decides which seats
carry an offer, and the window draws what it is handed.

### One class, so the two windows cannot drift apart

The mandate asked for the button to be worded by the engine "the same way the offer window already does
it, and ideally through the same code". It is the same code.

`src/CcDirector.Avalonia/SmartRestart/WayUpReopenViewModel.cs` is new and is the ONE place that decides
whether there is a button, what it says, what sentence sits beside it, and what asking the engine looks
like. Both `WayUpRowViewModel` (the start-up offer's rows) and `RestartHistorySeatViewModel` (the
history's seats) hold one. The offer window's XAML now binds through it, and
`WayUpOfferViewModel.ReopenAsync` delegates the call to it rather than building its own request.

That matters because the drift is what caused finding 3 in the first place: the history said something
the product could not do. Two copies of that rule would be free to disagree again; one cannot.

The offer window's behaviour is unchanged by that rewiring - the engine's answer still appears in its
result panel and its two replies are still replaced by Close - and the twenty-one tests the first seat
wrote all still pass, which is what says so.

### What is drawn is layout, never a word

`WayUpReopenViewModel` decides only whether a control is drawn. Its two busy inputs are kept apart on
purpose: `_inFlight` is this seat's own call, and `_screenIsBusy` is the offer window telling every row
that a bring back is running. Collapsing them would have meant the offer window's bring back no longer
deadened the reopen buttons, which is a behaviour the first seat chose deliberately.

---

## 3. Task 3 - the flaky test, and what it actually was

### It was reproduced first

Ten whole-project runs on the merged tree, before any fix. The fourth run failed:

    Failed CcDirector.Avalonia.Tests.SmartRestart.WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat [6 ms]

Six milliseconds, which matches the Tech Lead's reading. The other nine runs were clean, so it is roughly
one run in ten on this machine, not one in four.

### The cause was OBSERVED, not read off the code

There were two candidate causes and the code alone cannot choose between them: the test could be losing
`Assert.Single(engine.ReopenRequests)` after the press, or `Assert.Single(RowButtons(window))` before it.
A failure that takes six milliseconds looks the same either way.

So I wrote a throwaway diagnostic that starved the thread pool - minimum worker threads set to one, 512
blocked work items queued - opened the real window, pressed the real button, and read both numbers at the
instant the press returned:

    OBSERVATION: row buttons drawn = 1; engine requests the instant Click() returned = 0;
    the engine call arrived 25000 ms later; requests now = 0.

**Row buttons drawn = 1**, so the button selection was never the problem. **Engine requests = 0 at the
instant the press returned**, so the press had handed its work to a thread pool thread that had not run.
On an idle machine the same diagnostic reported 1 and "arrived 3 microseconds later" - the pool winning
the race by a hair, which is the passing case and explains the thirty milliseconds a passing run took.

**The cause, in one sentence: a press hands its work to a thread pool thread, and
`Dispatcher.UIThread.RunJobs()` runs what is already queued on the interface thread and never waits for
the pool - so the assertion after the press was racing it.** The engine is called off the interface
thread on purpose, because it reads the Gateway and starts real sessions (CLAUDE.md rule 1), so the work
being elsewhere is correct and the test was wrong about it.

The diagnostic was deleted; it is not in the branch.

### The fix waits for the thing it cares about

No sleep and no retry was added. Each window now remembers the task its last press started:

```csharp
internal Task WorkTheLastPressStarted { get; private set; } = Task.CompletedTask;
```

The handler stays `async void` and **awaits that same task**, so a failure is still raised exactly once
on the dispatcher and is never left unobserved; the test awaits the same task and so cannot assert before
the work has run. The two tests that press a button -
`BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat` and
`BtnBringBack_Clicked_ReachesTheEngineWithThisRecord` - now await it instead of draining the queue.

**The bring back one had the same defect.** It is the same shape and the same race; it just loses less
often. It is very likely the unnamed intermittent red the first seat's proof reported and could not
identify (its section 2), though I cannot prove that, because that failure's name was never captured.

### Eight whole-project runs after the fix

    run 1: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 2: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 3: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 4: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 5: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 6: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 7: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696
    run 8: Passed!  - Failed: 0, Passed: 696, Skipped: 0, Total: 696

Every run reached its end and printed its total, so none is an aborted run reporting a partial count.

**What those eight runs do NOT prove.** At roughly one failure in ten, eight clean runs is good evidence
and not certainty. The stronger argument is the one above: the test now awaits the exact task the press
started, so there is no longer a window in which it can assert before the engine has been called.

**And one thing I could not finish.** I tried to close that gap by running the FIXED test under the same
starved thread pool, which would have shown the fix surviving the exact condition that broke it. Starving
the pool also starves the test host, and the run had not got past discovery after several minutes, so I
stopped it and restored the file. The tree was checked clean afterwards (`git status --short` and
`git diff HEAD --stat` both empty) and the restore run below is a full build. That experiment is the one
piece of evidence I set out to get and did not.

---

## 4. The revert proofs - two mutations, each shown able to turn tests red

The work was **committed first** (`86fb25e6f`), so `git checkout --` could only restore the committed
file and never eat the change. Every run is a full build.

### Mutation 1 - the history seat is given no offer

In `RestartHistorySeatViewModel`, `seat.Reopen` replaced by `null`, which is the state the history was in
before the engine fix: the seat knows nothing about reopening.

    Failed Load_RecordsThatOweNothing_StillOfferTheirSavedConversations
    Failed Load_ASeatThatEndedWithoutAHandover_DrawsAButtonWordedByTheEngine
    Failed BtnReopen_Clicked_ReachesTheEngineNamingThatSeatAndShowsItsAnswer
    Failed BtnReopen_OnTheSecondSeat_NamesTheSecondSeatAndNeverTheFirst
    Failed Load_ASeatWithNoConversation_ShowsTheEnginesSentenceAndNoButton
    Failed WayUpScreenshotTests.Capture_EveryMomentOfTheTwoWindows_DrawsARealPicture
    Failed!  - Failed:     6, Passed:    91, Skipped:     0, Total:    97

**6 failed, 91 passed, 97 total.** All five new tests, plus the picture test - which proves the new
assertion inside it is load bearing, rather than a picture test that would pass on a window with no
button in it.

### Mutation 2 - the button finds its seat by position instead of by its own seat

In `RestartHistoryWindow.ReopenFromThePressAsync`, the sender's own seat replaced by
`ViewModel.Entries[0].Seats.First(x => x.Reopen.HasButton)` - the first seat that has a button anywhere in
the window, which is what a button that did not carry its row would do.

    Failed BtnReopen_OnTheSecondSeat_NamesTheSecondSeatAndNeverTheFirst
    Failed!  - Failed:     1, Passed:    96, Skipped:     0, Total:    97

**1 failed, 96 passed, 97 total.** Exactly one test holds that rule, and it is the one written for it.
No other test noticed, which is worth knowing: without that test, a button that reopened the wrong
session would have shipped green.

### Restored, and green again on a FULL build

After the restores, `git status --short` and `git diff HEAD --stat` both printed nothing. The source files
were touched to force a recompile and the check run again:

    CcDirector.ControlApi -> ...\CcDirector.ControlApi.dll
    CcDirector.Avalonia -> ...\cc-director.dll
    CcDirector.Avalonia.Tests -> ...\CcDirector.Avalonia.Tests.dll
    Passed!  - Failed:     0, Passed:    97, Skipped:     0, Total:    97

All three assemblies were rebuilt in that run, so the green is the restored SOURCE and not a leftover
binary.

---

## 5. What the five new tests prove, in plain words

All five are in `RestartHistoryWindowTests` and all five open the real window and read what the real
controls drew.

1. **A seat that ended without a handover draws a real button, worded by the engine.** Exactly one button
   is drawn for a history holding one reopenable seat, its text is the engine's offer, it belongs to that
   seat, and the engine's sentence for what reopening really does is on screen. The seat that handed over
   draws neither a button nor a sentence.
2. **A seat the engine says has no conversation shows that sentence and NO button.** A button that could
   never work is the defect this rule exists to stop.
3. **Records that owe nothing back still offer their saved conversations.** Both records in the fixture
   are ones the bring back cannot touch - one cancelled, one shut down ignoring every session - and the
   test asserts neither carries a bring back button while the reopen button is there anyway. This is the
   half of finding 3 that a rule of my own would have quietly broken.
4. **Pressing the real button reaches the engine naming THAT seat and THAT record, off the interface
   thread, and the engine's answer is drawn.** The fake engine records whether any call ever arrived on
   the interface thread, so a call moved onto it turns this red. It also asserts no bring back was
   started.
5. **Each button carries its own seat.** Two reopenable seats in one record; pressing the second names the
   second. A button that found its seat by position would reopen the wrong session and nothing on screen
   would tell the owner it had. This is the test mutation 2 turns red.

---

## 6. The picture

One new picture, drawn the same way the seven existing ones are and committed under
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/`:

| File | What it shows |
|---|---|
| `way-up-8-history-a-seat-that-ended-without-a-handover.png` | A record that owes NOTHING back - shut down ignoring every session - holding two seats that ended without a handover: one drawn with the engine's "Reopen its saved conversation" button and the engine's sentence about what really arrives, and one drawn with the engine's "no conversation was recorded" sentence and no button. Beneath it, a cancelled record with an ordinary seat. This is the picture of finding 3 answered. |

**It is asserted to be really drawn, and specifically that the BUTTON is in it.** The shared `Capture`
check ("more than a hundred distinct colours, the window background, the panel background") cannot tell a
history with a button from one without, so `AssertTheReopenButtonIsReallyDrawn` runs before the capture:
exactly one visible button whose data context is a history seat, with the engine's words on it, belonging
to the seat that can be reopened. Mutation 1 turns the picture test red, which is that assertion working.

**One existing picture changed, deliberately.** `way-up-5-history-three-records.png` had a seat whose
outcome read "Its saved conversation can be reopened" while carrying no offer - which is exactly the
defect finding 3 named, preserved in a picture. That seat now carries a real offer, so the picture shows
something the engine can actually produce. I opened both pictures and read them.

---

## 7. What I could NOT reach, and what this proof does not cover

- **The starved-pool run of the FIXED test did not finish**, as section 3 says. It is the one piece of
  evidence I set out to get and did not; the eight clean runs and the await stand in its place.
- **No Gateway, no Director, no session and no real record was used.** Every test drives a fake engine,
  and every record in them is the TEST's own wording. What is proved is that the windows show whatever
  the engine hands them; the real engine's sentences are proved by the engine's own tests.
- **The two lines in `MainWindow.axaml.cs` still have no test**, unchanged from the first seat's proof and
  still the largest gap: nothing here proves the File menu item appears, that pressing it opens the
  history, or that start-up reaches `WayUpStartUpAsk`. I did not touch that file.
- **Nothing here proves a reopen really restores a conversation** for any agent. That is the engine's own
  open question, being measured on the isolated rig. These windows show whatever the engine says.
- **A reopen across a Director restart is still not guarded.** The engine's once-only claim is held by the
  process; the engine's own proof says so and says closing it is a Gateway change and the Delivery Lead's
  decision. Nothing in a window can close it.
- **Only these two commands were run.** No `-Parked` suite, no web test, no Python test, and not
  `scripts\test-local.ps1`. Nothing here touches them.
- **No change to `src/CcDirector.ControlApi`**, none to the Gateway, none to any contract, and none to
  `MainWindow.axaml.cs`. The engine told me everything the button needed, so there is nothing missing to
  report under that heading.
- **The hosted checks on #3208 were not waited for**, as the mandate says. It can merge now.
