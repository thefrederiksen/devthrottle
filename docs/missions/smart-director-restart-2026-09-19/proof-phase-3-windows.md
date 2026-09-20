# Proof - phase 3, Developer: the two WINDOWS of the way up

Written by the Developer seat opened by the phase 3 Tech Lead (session 38f41a97), 20 September 2026.

- Branch: `smart-restart/p3-way-up-windows`, cut from the ENGINE branch `smart-restart/p3-way-up-engine`
  = `0cda45788`, not from `main`.
- Worktree: `D:/ReposFred/devthrottle-smart-restart-p3-windows`.
- Commits: `d4a4bfd65` (the work), `f3781c3b3` (`origin/main` = `8c81e7d63` merged in).
- Pull request: **#3208**, against `main`.

## IT CANNOT MERGE BEFORE #3202, AND THIS SAYS SO

The engine this calls - `IDirectorWayUp`, `WayUpWords` and `ControlApiHost.CreateDirectorWayUp()` - is
pull request **#3202** and is NOT on `main`. **#3202 merges first.** Merging #3208 before it would take
the windows without the engine they are built on, and nothing would compile. This is written in the
pull request's own text as well as here. The hosted checks were not waited for, as the mandate says.

**The engine did not move while this was built.** The mandate says to read
`docs/missions/smart-director-restart-2026-09-19/phase-3-status.md` once before opening the pull
request and to merge the engine in again if it had changed. That file does not exist - not on `main`,
not on either branch - and `origin/smart-restart/p3-way-up-engine` is still at `0cda45788`, the exact
commit this tree was cut from. So there was nothing to merge in. Checked at 08:05 on 20 September 2026;
if the engine moves after that, this branch needs the merge and the checks run again.

---

## 1. What was built

Nine new files under `src/CcDirector.Avalonia/SmartRestart/`, namespace
`CcDirector.Avalonia.SmartRestart`, and thirteen lines in `MainWindow.axaml.cs`. **No change to
`src/CcDirector.ControlApi`**, none to the Gateway, none to any contract, and none to the phase 1 or
phase 2 files.

| File | What it is |
|---|---|
| `WayUpOfferWindow.axaml` / `.axaml.cs` | "A restart is available". The start-up window, and the window the history opens for a record that still owes seats. |
| `WayUpOfferViewModel.cs` | Everything that window shows, and the two calls it makes to the engine. |
| `WayUpRowViewModel.cs` | One row, and one seat line inside it. |
| `RestartHistoryWindow.axaml` / `.axaml.cs` | File, Restart history. Also holds `NoHostWayUp`. |
| `RestartHistoryViewModel.cs` | Everything that window shows, one record, and one seat line. |
| `WayUpStartUpAsk.cs` | The start-up ask: once, off the interface thread, and nothing shown unless the engine offers something. |

### The thirteen lines in `MainWindow.axaml.cs`

That file is seven thousand lines, other missions are editing it, and phase 2 holds a branch that
changes its File menu and its `OnClosing`. So what landed there is two blocks and nothing else
(`git diff` says `1 file changed, 13 insertions(+)`, no deletions):

1. In `TryAttachGatewayMonitor`, one call: `SmartRestart.WayUpStartUpAsk.WatchForRestartOffer(host, this)`.
   It follows the host's own `GatewayMonitor`, exactly as the mandate says, and everything it does is
   in `WayUpStartUpAsk`.
2. One File menu item, **"Restart history..."**, added immediately after "Drain this Director for
   restart..." - which is what is actually there on this base; phase 2 has not renamed it on `main`
   yet. If phase 2 renames that item, this one sits beside the renamed one and needs no edit.

Nothing else in that file was touched. The two pre-existing non-ASCII characters in it (a byte-order
mark on line 1 and an arrow on line 4532) are somebody else's and were left alone; **every file this
work added is ASCII only**, checked with `LC_ALL=C grep -n '[^ -~\t]'` over every added `.cs` and
`.axaml`.

---

## 2. The commands that were run, and the counts

### The baseline, on this tree UNTOUCHED, before a line was written

```
dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
```

    Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47

```
dotnet test src/CcDirector.Avalonia.Tests
```

    Passed!  - Failed:     0, Passed:   646, Skipped:     0, Total:   646

**47 and 646, 0 failed in both.** Exactly the numbers the Tech Lead measured on untouched
`origin/main` = `ab2770c4a`, which confirms the engine branch this tree starts from added nothing to
either - it added no Avalonia test.

### After, on the MERGED tree

Same two commands:

    Passed!  - Failed:     0, Passed:    92, Skipped:     0, Total:    92
    Passed!  - Failed:     0, Passed:   691, Skipped:     0, Total:   691

**92 and 691, 0 failed in both.** Risen by 45 and by 45 - exactly the number of tests written
(21 + 13 + 10 + 1, listed in section 4). No new failure.

### One intermittent red I could not name, reported rather than buried

The FIRST full-project run on the merged tree - the run that also compiled it - reported
`Failed: 1, Passed: 690, Total: 691`. The test's name was not captured: the output tail held only
`at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs`, which says only that it was a parameterless
test method. **Four consecutive full runs of the same binary afterwards were all `691 passed, 0
failed`**, and every filtered run before and after it was `92 passed, 0 failed`.

I cannot say which test it was, so I do not claim it was not mine. What I can say is that it did not
recur in four runs, that it was not in the SmartRestart filter on any run, and that the total was 691
in that run too - so it was a failure, not a missing test. If it reappears, it is worth catching with
`--logger "console;verbosity=detailed"` on the first run after a build.

### What these counts do NOT cover

- **No Gateway, no Director and no session was run.** Every test is against a fake engine, and every
  record in them is the TEST's own wording, not the engine's (see section 6).
- **No `-Parked` suite, no web test and no Python test** was run. Nothing here touches them.
- The `dotnet test` on one project is not `scripts\test-local.ps1`. The mandate named these two
  commands and these two are what was run.

---

## 3. The revert proofs - three mutations, each shown able to turn tests red

The work was **COMMITTED FIRST** (`d4a4bfd65`), so `git checkout --` could only ever restore the
committed file and never eat the change. Each mutation was applied to the source and then reverted;
**every run is a full build, never `--no-build`**, so what was measured is the source and not a stale
assembly.

### Mutation 1 - the old window defect, on the offer window

The exact defect `DrainDirectorDialog` shipped with. In `WayUpOfferWindow.axaml.cs`, one member added:

```csharp
private void InitializeComponent() { }
```

That hand-written method replaces the generated one, so no named control is connected and the window
throws the moment it is opened.

    Failed!  - Failed:    20, Passed:    72, Skipped:     0, Total:    92

**20 failed, 72 passed, 92 total.** Every test that OPENS the offer window went red, including the
picture test. Twenty tests hold that defect where the old window had none.

Note the first attempt used `private new void InitializeComponent()`, which the compiler refused with
`CS0109: does not hide an accessible member`. That refusal is itself evidence: at compile time nothing
else declares the method, which is precisely why the generated one never runs.

### Mutation 2 - the window counts for itself instead of showing the engine's label

The drift critical rule 7 forbids. In `WayUpOfferViewModel.cs`:

```
-  public string SeatsOwedLabel => Record.SeatsOwedLabel;
+  public string SeatsOwedLabel => Rows.Count == 1
+      ? "One session is waiting to be brought back."
+      : $"{Rows.Sum(r => r.Seats.Count)} sessions are waiting to be brought back.";
```

    Failed CcDirector.Avalonia.Tests.SmartRestart.WayUpOfferWindowTests.Show_LabelsThatDisagreeWithTheirOwnNumbers_TheWindowShowsTheLabelItWasGiven
    Failed CcDirector.Avalonia.Tests.SmartRestart.RestartHistoryWindowTests.ARecordsOffer_IsTheSameOfferTheStartUpWindowMakes
    Failed!  - Failed:     2, Passed:    90, Skipped:     0, Total:    92

**2 failed, 90 passed, 92 total.** Both are the tests that own the rule, and no other test noticed - so
those two, and only those two, are what hold it.

**And that is the whole point of the disagreeing-label test.** `Show_ThreeMissionHeads_ShowsTheEngines-
HeadlineWhenReasonAndCount` stayed GREEN under this mutation, because its record's rows happen to hold
six seats and its label happens to say six. A test whose fixture agrees with the re-derivation cannot
catch the re-derivation. The test that catches it is the one whose label is deliberately a lie.

### Mutation 3 - the same old window defect, on the history window

`private void InitializeComponent() { }` added to `RestartHistoryWindow.axaml.cs`.

    Failed!  - Failed:    10, Passed:    82, Skipped:     0, Total:    92

**10 failed, 82 passed, 92 total.** Every test that opens the history window, plus the picture test.

### Restored, and green again on a FULL build

After the third restore, `git status --short` and `git diff HEAD --stat` both printed nothing, so the
source is byte for byte the committed source. The source files were then touched to force a recompile
and the check run again:

    CcDirector.ControlApi -> ...\CcDirector.ControlApi.dll
    CcDirector.Avalonia -> ...\cc-director.dll
    CcDirector.Avalonia.Tests -> ...\CcDirector.Avalonia.Tests.dll
    Passed!  - Failed:     0, Passed:    92, Skipped:     0, Total:    92

All three assemblies were rebuilt in that run, so the green is the restored SOURCE and not a leftover
binary.

---

## 4. What each new test proves, in plain words

### `WayUpOfferWindowTests` - 21 tests

1. **The window OPENS and every named control is connected** - the test the old window never had. Goes
   red under mutation 1 with nineteen others.
2. **A label that disagrees with its own numbers is still shown as given.** The engine is handed an
   answer that says one session is waiting while carrying three rows and three seats, whose headline
   says nothing is available, and whose row title claims to reopen a conversation. Every one of those
   sentences is drawn unchanged, and the row is still a bring back row because the ENGINE said its kind
   was. This is the mandate's own required test and the one that catches mutation 2.
3. **The headline, when, reason and count are the engine's**, shown as given.
4. **Three mission heads are three rows, in the engine's order, all ticked.**
5. **The seats are drawn under their own row, in the engine's order**, each showing the engine's own
   sentence - which already names the seat, so the name is not drawn twice.
6. **A seat that ended without a handover is its own row, beside the others, unticked**, and its tick
   box is dead: the engine has already said in words that the row is not brought back with the rest.
7. **It is coloured apart from the rows that come back** - colour, never a word.
8. **It carries its own button in the engine's words**, plus the engine's sentence saying what pressing
   it would really do.
9. **A seat the engine says has NO conversation gets that sentence and NO BUTTON.** A button that could
   never work is the defect this rule exists to stop.
10. **The two answers are Bring back and Not now**, with bring back the default and focused, and not now
    the cancel.
11. **Not now writes nothing**: the window closes and the engine is asked for nothing at all, so the
    record stays as it was and is offered again.
12. **Escape and the window's own close are the same answer as Not now.**
13. **Two of three ticked names those two rows to the engine**, by the engine's own row ids, in the
    engine's order - and **off the interface thread**, which the fake engine proves by recording whether
    any call arrived on it.
14. **What came of a bring back is the engine's message with one line per seat**, also the engine's, and
    the two answers are replaced by the one that closes the window.
15. **Nothing ticked is still sent to the engine**, and the ENGINE's refusal is what the owner reads. A
    window that pre-empted it would be a second way of saying the same thing.
16. **A second press asks the engine only once** - running the restore twice would start a second copy
    of every session.
17. **The real Bring back button reaches the engine with this record.**
18. **The reopen names THAT seat**, off the interface thread, and shows the engine's answer.
19. **The real reopen button carries its own row with it**, so the second ended row could never be
    reopened by pressing the first one's button.
20. **A view model with no engine is refused**, rather than building a window that fails when a button is
    pressed.
21. **A view model with no record is refused.**

### `RestartHistoryWindowTests` - 13 tests

1. **The window OPENS and every named control is connected**, and its title is Restart history.
2. **It is up and says it is reading BEFORE the engine has answered** (CLAUDE.md rule 1), so a Gateway
   that takes seconds never leaves a blank or frozen window.
3. **The engine is asked off the interface thread.**
4. **Three records are shown newest first, each in the engine's words** - a smart shutdown that still
   owes seats, a cancelled one and an ignore-all one - and the test asserts they are DRAWN in that order,
   not merely held in that order.
5. **Every seat says what became of it in the engine's words**, beside its name. The history's seat
   sentence does not name the seat, unlike the offer's, so the name is drawn.
6. **An empty history says so and is not a refusal.**
7. **A Gateway that did not answer shows the refusal and NOT an empty history**, and the test asserts the
   empty history's words are absent - an empty list would read as "you have no records", which is a lie.
8. **A record that still owes seats carries the offer; the other two do not.**
9. **It is the SAME offer the start-up window makes** - the same view model type, carrying the engine's
   own rows, words and ticks. This is what stops a second wording drifting away from the first, and it
   is the second test mutation 2 turns red.
10. **A record that owes nothing refuses to build an offer**, rather than handing back an empty one that
    would look like an offer with nothing in it.
11. **Close closes and changes nothing.**
12. **A Director whose own host has not started shows the ENGINE's own refusal wording** and an empty
    list is never shown in its place.
13. **And that same stand-in refuses everything else in the same sentence** - the offer, the bring back
    and the reopen - starting nothing and claiming nothing.

### `WayUpStartUpAskTests` - 10 tests

1. **Nothing is asked until the connection is Connected.** Not configured, connecting, failed and no
   tailnet identity all ask nothing and show nothing.
2. **Ten Connected events ask once and show once.** The Gateway connection goes up and down all day; the
   offer is a start-up question, and asking it again would put a window in front of the owner at work.
3. **A reconnection after a disconnection is still not a second ask.**
4. **The engine is asked OFF the interface thread and the window is shown ON it** - the test reads
   `Dispatcher.UIThread.CheckAccess()` inside both delegates, so a call moved onto the wrong thread turns
   it red.
5. **Nothing waiting shows nothing at all.**
6. **A refusal shows nothing at all** - the one the mandate is most explicit about. A start-up that
   interrupts the owner to say it could not check is a worse product than one that stays quiet.
7. **A seam that throws does not take start-up with it**, and still shows nothing.
8. **An answer that says Offered but carries no record shows nothing**, rather than an empty window.
9. **A window that throws on the way up does not take start-up with it.**
10. **It refuses to be built without an ask or without a show.**

### `WayUpScreenshotTests` - 1 test, 7 pictures

Draws all seven and asserts each is a REAL drawing: it carries the window background `#252526`, the
panel background `#1E1E1E`, and more than one hundred distinct colours, which only drawn text produces.
A blank frame is one flat colour. That check exists because nobody downstream may be able to open an
image, so "the picture was written" has to mean something without anyone looking.

---

## 5. The pictures, and what each one shows

Committed under `docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/`. They are
WRITTEN only when `SMART_RESTART_SCREENSHOT_DIR` names a folder, so an ordinary test run leaves nothing
on disk.

| File | What it shows |
|---|---|
| `way-up-1-offer-three-missions-all-ticked.png` | The start-up window with THREE mission heads - a three-seat mission, a two-seat one and a single session - every row ticked, each seat's own sentence under its row. The mandate's first required picture. |
| `way-up-2-offer-with-a-seat-that-ended-without-a-handover.png` | The same window with TWO rows that ended without a handover beside a bring back row: both unticked and amber, one with its Reopen its saved conversation button, one with the engine's sentence saying there is nothing to reopen and no button. The mandate's second required picture. |
| `way-up-3-offer-one-session.png` | One session owed - the singular wording, and the smallest the window gets. |
| `way-up-4-offer-after-a-bring-back.png` | After the answer: the engine's "5 came back; 1 could not", six per-seat lines including one that did not come back and why, and the two answers replaced by Close. |
| `way-up-5-history-three-records.png` | The history with THREE records - one still owing seats and carrying its Bring back button, one CANCELLED, one IGNORE-ALL - each with its kind, reason, outcome and per-seat lines. The mandate's third required picture. |
| `way-up-6-history-empty.png` | An empty history, saying so in the engine's words. |
| `way-up-7-history-gateway-did-not-answer.png` | A Gateway that did not answer: the engine's refusal, and NOT an empty list. |

I opened pictures 2 and 5 myself and read them. They are right: the amber unticked rows sit beside the
ticked one with the correct button on one and none on the other, and the history's three records read
in order with the offer on the only one that owes seats.

**What these do not prove:** that a picture looks RIGHT. A person opens the files for that. And the
words in them are the TEST's stand-ins for the engine's labels, written to read the way the mission
words them - the real engine's words may differ.

---

## 6. Every decision made along the way

1. **The history's offer is the SAME window, opened for that record**, not a second layout. The mandate
   says a record that still owes seats "carries the same bring-back offer the start-up window does", and
   the cheapest way to make that true forever is for there to be one window. Pressing Bring back on a
   history record opens `WayUpOfferWindow` over the history, and the history re-reads itself afterwards
   so a record that has just come back says so.
2. **The tick box on a row that ended without a handover is drawn UNTICKED AND DEAD.** The mandate says
   "unticked"; the engine already says in `Detail` that such a row is not brought back with the rest, and
   refuses it by name if it is sent. Drawing that is rendering the engine's sentence, not deciding it.
   The alternative - a tick that is guaranteed to be refused - is a button that reports nothing.
3. **"Bring back", "Not now", "Close", "Bring back..." and the window title "Restart history" are
   CONSTANTS, and the engine does not supply them.** They are the mission document's own words and they
   never change with any state, so they are not verdicts. They are held as named constants on the view
   models and bound, rather than typed into the XAML, so there is one spelling of each.
   **If the Tech Lead wants them in the engine too, that is one small addition to `WayUpWords` and a
   binding change here - I did not reach into the engine to do it.**
4. **"Reading..." in the history window is chrome, not a verdict.** CLAUDE.md rule 1 requires the window
   to appear at once and say it is working; the engine's own sentence replaces it the moment one arrives
   and is never shown beside it.
5. **A Director whose own host has not started is given `NoHostWayUp`**, which answers the ENGINE's own
   `WayUpWords.GatewayRefusal(...)` - the same function the engine calls when the Gateway does not answer
   - with the reason "this Director's own host has not finished starting". **This is the one place a
   window supplies a reason string.** It is fed INTO the engine's wording rather than worded here, so
   there is one sentence for "nothing could be read" and not two. The alternative was a menu item that
   silently does nothing, which reads as broken. If the Tech Lead would rather the engine own this case
   as a state of its own, that is an engine change and I did not make it.
6. **The start-up ask lives in its own file and MainWindow gets one call.** The mandate says keep the
   change to that file tiny, and a testable unit is also the only way to prove "once, ever" and "off the
   interface thread" at all - neither is provable through a seven-thousand-line window.
7. **The ask is kept alive by its own subscription**, so MainWindow needs no field for it. That is what
   keeps the change to one line rather than two.
8. **It fires on the monitor's CURRENT status as well as on its Changed event.** A monitor that is
   already Connected when the window attaches raises no event, and a start-up check that only listened
   would never fire on a fast Gateway.
9. **`Task.Run` is used for every engine call from a window**, not `ConfigureAwait(false)` alone. The
   engine reads records from the Gateway one at a time and `BringBackAsync` runs the whole restore before
   it answers; `Task.Run` puts all of it on a thread pool thread, and the fake engine records whether any
   call ever arrived on the interface thread so this cannot silently regress.
10. **Nothing ticked is still sent to the engine.** The engine already refuses it with its own words; a
    window that refused first would be a second way of saying the same thing.
11. **The two answers stay DRAWN while the engine is working and are merely dead.** Buttons that vanished
    mid-call would read as broken. After an answer they are replaced by Close, because "Not now" is not a
    true thing to offer after a bring back has already run.
12. **The reopen button and the history's bring back button read their own DataContext**, never an index
    or a name. A button that found its row by position could reopen the wrong seat, and one test proves it
    does not.
13. **Both windows have a designer constructor**, which the XAML compiler requires, and each is handed a
    stand-in engine (`DesignerWayUp`) that throws with an explanation. The alternative - allowing a null
    engine - would move the failure from "the window was built wrong" to "a button did nothing", which is
    the worse of the two.
14. **Colour is by row kind only**: the guide's primary text for a bring back row, the guide's warning
    amber for one that ended without a handover. Both are `docs/VisualStyle.md` section 1 values, and one
    test asserts the two differ so a change that made them the same is caught.
15. **The menu item went after "Drain this Director for restart..."**, which is what is on this base.
    Phase 2 has not renamed it on `main`.

---

## 7. What I could NOT reach, and what this proof does not cover

- **No Gateway, no Director, no session and no real record was ever used.** Every test drives a fake
  engine, and **every record in every test is the TEST's own wording, not the engine's**. What is proved
  is that the windows show WHATEVER the engine hands them, which is the rule they exist under. What is
  NOT proved is that the real engine's sentences read well on screen - the pictures are stand-in words
  written to read the way the mission words them.
- **The two lines in `MainWindow.axaml.cs` have no test.** The File menu is built in code in a
  seven-thousand-line file with no test harness around it, and `TryAttachGatewayMonitor` needs a real
  `ControlApiHost`. So nothing here proves that the menu item appears, that pressing it opens the
  history, or that a real Director's start-up reaches `WayUpStartUpAsk`. **That is the largest gap in
  this proof.** Both lines are one call each into code that IS tested, which is why they were kept to one
  call each; the first time either is exercised for real is the phase 5 run on the rig.
- **`WayUpStartUpAsk.WatchForRestartOffer` itself has no test**, for the same reason: it needs a real
  `ControlApiHost` and a real `GatewayConnectionMonitor`. Its three rules are all tested on the class it
  builds; what is untested is the wiring - that `GatewayMonitor.Changed` fires and that
  `CreateDirectorWayUp()` gives a working engine.
- **No real `GatewayConnectionMonitor` was ever run**, so "the monitor reports Connected when the tunnel
  comes up" is a premise these tests assume rather than prove.
- **Whether reopening a saved conversation really restores its context** is untested here, for every
  agent - it is the engine's own open question (its proof, section 6) and is being measured on the
  isolated rig. These windows show whatever `WayUpWords.ReopenOffer` says, so if the rig shows Claude
  Code or Pi does not really come back, the words change in the engine and nothing here moves.
- **The `-Parked` suites, the web tests and the Python tests were not run.** Nothing here touches them.
- **One intermittent failure in the first full run on the merged tree could not be named.** Section 2
  says exactly what is and is not known about it.
- **The hosted checks on #3208 were not waited for**, as the mandate says, and **#3208 cannot merge
  before #3202**.
