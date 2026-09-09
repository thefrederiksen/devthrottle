# Worker E notes - the Director window's close, re-pointed at the stop

Mission "Stop a session", issue #2633, Phase B, Seat 3. Branch `mission/stop-a-session`, worktree
`C:\ReposFred\devthrottle-stop-a-session`. Written 9 September 2026.

---

## What I built

### 1. `GatewayClient.StopSessionAsync` - `src/CcDirector.ControlApi/GatewayClient.cs`

The Director's own door onto the one stop route. It posts `{ "reason": ... }` to
`POST /sessions/{sessionId}/stop` on the Director's existing authenticated client - the same one
`RecordHoldAsync` uses, already pointed at the resolved Gateway address and already carrying the fleet
token - and returns the parsed `SessionStopResponse`.

It fails loud and it has no fallback:

- Not configured with a Gateway: throws, and says that the reason for a stop is recorded on the Gateway.
- The call did not succeed: throws through `RelayFailureAsync`, which carries the Gateway's own sentence
  where there is one and the status line where there is not. This is the same helper the neighbouring
  relays use, so a Director-side fault that names a machine reaches the operator naming that machine.
- A 200 with no headline: throws. Every answer this route gives carries a headline, so one that does not
  is a Gateway that did not understand the request. This is the command line's "broken instrument, not a
  fourth verdict" rule applied to the same answer - returning it would leave the window with nothing to
  render and no idea anything was wrong.
- A blank reason: refused before the round trip, the way the command line refuses it.

**It never falls back to a local kill.** Ruling 5 names that as the wrong fix in terms.

I also added an `internal` constructor overload that takes the `HttpMessageHandler`. That is the test
seam and nothing else: production still dials through `GatewayHttp.Handler()`, exactly as before. The
precedent for an internal test seam on this class is `ProbeGatewayCandidate`, three hundred lines above
it, which exists for the same reason.

### 2. `ControlApiHost.StopSessionAsync`

One delegating method, so the window reaches the route the way it reaches every other Gateway
operation. Throws when this Director has no Gateway client at all, which is the point and not a gap.

### 3. `StopSessionDialog` - `src/CcDirector.Avalonia/StopSessionDialog.axaml(.cs)`

A new window, drawn to `docs/VisualStyle.md` (background `#252526`, `Margin="20"`, the accent
`#007ACC` primary button and the `#3C3C3C` secondary, `CenterOwner`, the same shape as
`DrainDirectorDialog`, which is the nearest existing thing: ask, work, report, stay up).

- It says, in plain words, what stopping does, that the files are not touched, and that the reason is
  recorded and why.
- The confirm control is **off while the box is empty or only whitespace**. The Gateway requires the
  reason, so a control that offered the click would be offering a refusal.
- While the stop is in flight it says so, and the interface thread is never blocked.
- When the Gateway answers it shows the `Headline`, then each `Details` line, **in the Gateway's order,
  verbatim, and adds nothing**. It stays up until it is dismissed.
- A failure shows the failure, in words, in the same place, carrying the sentence it came with. The
  control is offered again, because a failed stop can honestly be tried again.

**The window never looks at the verdict word.** There are four today; a fifth is one edit on the Gateway
and none here. The only thing it decides is failure against success, and that comes from whether an
answer arrived or an exception did - never from the verdict.

The one sentence the window writes about an outcome is the `The session was not stopped:` prefix on a
failure the Gateway never folded. That is the same shape and the same reason as the command line's
`Not stopped:` prefix in `tools/cc-devthrottle/src/session_ops.py`.

### 4. `MainWindow` - the menu entry and the flow

- `Close Session` becomes **`Stop Session`**, with a tooltip that says what it does and that the reason
  is recorded.
- `CloseSessionAsync` is gone. `StopSessionAsync` opens the dialog and wires it to the Gateway route.
- **The row is no longer pruned locally.** The Gateway sends the stop back down this Director's own
  tunnel, `SessionManager.RemoveSession` raises `OnSessionRemoved`, and
  `MainWindow.OnExternalSessionRemoved` drops the rail row and does the active-session teardown. That
  teardown is not duplicated; it now lives in exactly one place for a stopped session. I corrected the
  two comments there that the change made false.
- **`CloseAllSessionsAsync` is untouched.** It is this Director shutting itself down, it must not depend
  on the Gateway, and nothing was shared with it or renamed under it.

---

## The mutation table - every test watched failing on purpose

Each mutation was applied to the real source, built, and run; the file was restored afterwards. The
"red said" column is the assertion message as it actually printed.

### The dialog - `StopSessionDialogTests`, 13 tests

| # | Mutation | Test that went red | Red said |
|---|---|---|---|
| MD1 | The reason gate ignores the text: `BtnStop.IsEnabled = !_running;` | `WithOnlyWhitespaceTyped_TheStopButtonIsStillOff` | `Assert.False() Failure  Expected: False  Actual: True` |
| MD2 | The blank-reason guard removed from `StopNowAsync` | `WithABlankReason_NothingIsSent` | `Assert.Equal() Failure: Values differ  Expected: 0  Actual: 1` |
| MD3 | The reason is not trimmed before it is sent | `TheReasonIsSentAsItWasWritten` (and `WithABlankReason_NothingIsSent`) | `Expected: "spawned into the wrong mode"  Actual: "  spawned into the wrong mode  "` |
| MD4 | The window composes a sentence: `"Stopped successfully."` prepended to the render | `ItShowsTheHeadlineAndEveryDetailLineVerbatim_AndAddsNothing`, `AVerdictWordItHasNeverSeen_RendersLikeAnyOther`, `AnAnswerWithNoDetailLines_ShowsTheHeadlineAlone` | `Expected: "stopped 9c41e7a2 - process 51884 ended an"...  Actual: "Stopped successfully.\r\nstopped 9c41e7a2 -"...` |
| MD5 | The failure is swallowed - nothing is written to the answer area | `AFailureIsShownInWords_CarryingTheSentenceItCameWith` | `Assert.Contains() Failure: Sub-string not found  String: ""  Not found: "The session was not stopped:"` |
| MD6 | Nothing says the stop is in flight | `WhileTheStopIsInFlightItSaysSo_AndTheLineGoesWhenItAnswers` | `Expected: "Stopping - asking the Gateway, and waitin"...  Actual: ""` |
| MD7 | The in-flight guard removed, so a second press sends a second stop | `ASecondPressWhileOneIsInFlight_SendsNothing` | `Assert.Equal() Failure: Values differ  Expected: 1  Actual: 2` |
| MD8 | The answer area starts empty instead of saying nothing has happened | `BeforeAnythingIsAsked_ItSaysNothingHasHappened` | `Expected: "Nothing has happened yet. Say why this se"...  Actual: ""` |
| MD9 | **The window branches on the verdict** and writes its own line for an unknown one | `AVerdictWordItHasNeverSeen_RendersLikeAnyOther` | `Expected: "a sentence written on the Gateway\r\nand a "...  Actual: "The session was stopped."` |
| MD10 | The in-flight line is left on screen beside the finished answer | `WhileTheStopIsInFlightItSaysSo_AndTheLineGoesWhenItAnswers` | `Expected: ""  Actual: "Stopping - asking the Gateway, and waitin"...` |
| MD11 | The button starts enabled in the XAML | `WithNoReasonTyped_TheStopButtonIsOff` | `Assert.False() Failure  Expected: False  Actual: True` |
| MD12 | The gate never turns the button on: `BtnStop.IsEnabled = false;` | `WithAReasonTyped_TheStopButtonComesOn` | `Assert.True() Failure  Expected: True  Actual: False` |
| MD13 | The control is never offered again after a failure | `AfterAFailureTheStopCanBeTriedAgain_AfterAnAnswerItCannot` | `Assert.True() Failure  Expected: True  Actual: False` |

**MD9 is the one that matters most** - it is the exact defect the phase's governing rule exists to
prevent, and the test names the cost: a fifth verdict word would print "The session was stopped." over
whatever the Gateway actually said.

### The Gateway client - `GatewayClientStopSessionTests`, 9 tests

| # | Mutation | Test that went red | Red said |
|---|---|---|---|
| MC1 | Posts to `sessions/{id}/kill` instead of `.../stop` | `ItPostsTheReasonToTheOneStopRoute` | `Expected: ..."9c41e7a2-...-444455556666/stop"  Actual: ..."9c41e7a2-...-444455556666/kill"` |
| MC2 | Sends an empty body - no reason | `ItPostsTheReasonToTheOneStopRoute` | `Expected: "spawned into the wrong mode"  Actual: null` |
| MC3 | Parses only the headline and the verdict, dropping the details and the facts | `ItParsesTheWholeFoldedAnswer` | `Assert.Equal() Failure: Values differ  Expected: 2  Actual: 0` |
| MC4 | Normalises an unrecognised verdict word to `stopped` | `ItCarriesAVerdictWordItDoesNotRecognise` | `Expected: "somethingTheGatewayAddedLater"  Actual: "stopped"` |
| MC5 | Throws its own sentence instead of carrying the Gateway's | `ARefusalThrowsCarryingTheGatewaysOwnSentence`, `AFailureWithNoSentenceStillThrows` | `Expected: "A stop needs a reason. Say why this sessi"...  Actual: "the stop did not succeed"` and `Sub-string not found  String: "the stop did not succeed"  Not found: "502"` |
| MC6 | **Falls back**: a failed call returns a synthesised `stopped` answer instead of throwing | `ARefusalThrowsCarryingTheGatewaysOwnSentence`, `AFailureWithNoSentenceStillThrows` | `Assert.Throws() Failure: No exception was thrown  Expected: typeof(System.InvalidOperationException)` |
| MC7 | Accepts a 200 with no headline as an answer | `A200WithNoHeadlineIsNotAnAnswer` | `Assert.Throws() Failure: No exception was thrown  Expected: typeof(System.InvalidOperationException)` |
| MC8 | The no-Gateway guard removed | `WithNoGatewayConfiguredItThrowsRatherThanStoppingLocally` | `Sub-string not found  String: "An invalid request URI was provided. Eith"...  Not found: "not connected to a Gateway"` |
| MC9 | The blank-reason guard removed | `ABlankReasonIsRefusedBeforeAnythingIsSent` (both cases) | `Assert.Throws() Failure: No exception was thrown  Expected: typeof(System.ArgumentException)` |

**MC6 is the one that matters most.** It is the fallback Ruling 5 names in terms - a stop that quietly
succeeds without the Gateway, and therefore with no recorded reason. Two tests go red on it.

MC8 is worth reading closely: with the guard gone the call still throws, but with
`"An invalid request URI was provided"` - a plumbing accident, not an answer. The test asserts on the
SENTENCE, not on the exception type, which is why it catches this.

---

## What I could NOT test, named as gaps

1. **Nothing here proves a real session is stopped.** Every one of these tests drives a stub. The stop
   itself - the Gateway route, the tunnel, the Director ending a process - is Phase A's work and is
   proved against its own stubs. The end-to-end run is the local stack the Phase B Manager is standing
   up and the QA report a later seat photographs. My tests stop this feature from being *unphotographable*;
   they are not the photograph.
2. **Nothing here proves the rail row disappears.** That happens in
   `MainWindow.OnExternalSessionRemoved`, reached only when a real Gateway sends a real `kill` down a
   real tunnel to a real `SessionManager`. I read the chain and it is intact
   (`SessionCommandExecutor.KillAsync` -> `SessionManager.RemoveSession` ->
   `OnSessionRemoved` -> `OnExternalSessionRemoved`), but reading is not proving. A photograph of the
   row going is the QA seat's frame 6.
3. **No pixels are asserted.** The tests drive the dialog's logic - the gate, the rendering, the failure
   path, the in-flight state - through internal members on a headless window. Nothing here says the
   window is legible, sized right, or compliant with `docs/VisualStyle.md`; I matched the colours,
   spacing and button shapes to the guide and to `DrainDirectorDialog` by hand. A screenshot is the
   only proof of that and it is not mine to take.
4. **`MainWindow.StopSessionAsync` itself is untested.** It is four statements - read the host, build
   the dialog, show it, log - and constructing a real `MainWindow` needs the whole application. The
   logic that could be wrong lives in the dialog and in the client, and both are tested. What is
   genuinely unguarded is the WIRING: that the menu entry calls this method and that this method passes
   the right session id.
5. **The local gate does not cover the web surfaces or the Python toolbelt**, so nothing I ran says
   anything about Worker D's half of this phase.
6. **Two parked Gateway tests could not run on this machine at all** - the file-symbolic-link ones
   above. They are unrelated to this change, but "the parked suite was green" is not a sentence I can
   write, and I have not written it.

---

## Things I found that are worth someone else knowing

1. **A hand-written `InitializeComponent` silently breaks named controls.** `StopSessionDialog` first
   carried `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);`, copied from
   `DrainDirectorDialog`. It compiles, and every `x:Name` field is left NULL at runtime - the first
   headless test hit a `NullReferenceException` in the constructor. Removing it and letting the XAML
   compiler generate the method fixed it. The generated method is what assigns the named fields; a
   hand-written one of the same name wins overload resolution, loads the XAML, and assigns nothing.
   `DrainDirectorDialog` is the ONE other window in `src/CcDirector.Avalonia` that writes that line
   (`App.axaml.cs` is the application and is a different, correct case).

   **I checked it rather than guessing, and here is exactly what the check does and does not say.** A
   throwaway headless test that did nothing but `new DrainDirectorDialog(null, "probe")` failed with
   `System.NullReferenceException` at `DrainDirectorDialog.axaml.cs:50`, which is the line that sets
   `TxtReport.Text` - the same failure, on the same line of the same kind, as mine. The probe was
   deleted afterwards and is not part of this change.

   **That does NOT establish that the drain dialog is broken in the real application**, and I am not
   claiming it is. It was shipped with a QA report showing it working, so something differs between
   the headless host and the running desktop - most likely how the compiled XAML is resolved when the
   entry assembly is a test runner. What it DOES establish is that the pattern is fragile and that
   this dialog cannot be exercised in a headless test at all today. That is worth someone's ten
   minutes; it is not this mission's work and I have not touched it.
2. **`TextBox.TextChanged` does not fire for a programmatic set in the headless test host.** The reason
   gate was first hung on that event, and the "a reason turns the button on" test failed while every
   other one passed - the gate looked tested and was not. It is now hung on the `Text` property
   through `PropertyChanged`, which fires however the text arrives.
3. **A test that deadlocks under its own mutation is a test that cannot go red.**
   `ASecondPressWhileOneIsInFlight_SendsNothing` originally awaited the second press. With the
   in-flight guard removed, that second call waits on the same outstanding answer the first is waiting
   on, and the run hung instead of failing. The test now starts the second press without awaiting it,
   asserts, then releases both. I only found this because the mutation was actually run.
4. **`phone/CcDirectorClient/Voice/GatewayClient.cs` still has its own `CloseSessionAsync`** that calls
   the legacy `DELETE /sessions/{sid}` with no reason. That is the native phone application, which is
   deliberately out of this mission's scope (the Gateway keeps the legacy door for exactly it), and it
   is NOT the `apps/mobile` client Worker D is moving. Recording it so nobody later reads "every
   surface goes through the one route" as covering that one: it does not, and the legacy door is why
   that is still honest.
5. **Nothing contradicts a ruling.** The answer shape in `SessionStopDtos.cs` is what the brief said it
   was, `RecordHoldAsync` is the model the brief said it was, and `OnExternalSessionRemoved` does what
   the brief said it does.

## One judgement call, recorded rather than buried

The brief says to match the word the Cockpit and the phone use. Worker D's brief has the Cockpit saying
`Stop session`; I used **`Stop Session`**, Title Case, because every other entry in this menu is Title
Case (`Close Session`, `Copy Handover Info`, `Relink Session...`) and a single lower-case entry among
them reads as a mistake. The WORD is the same on both surfaces, which is what Ruling 5 is about; only
the capitalisation follows each surface's own convention. If the Architect wants them identical to the
letter, it is one string.

---

## Test numbers

**22 new tests**, all passing: 13 in `src/CcDirector.Avalonia.Tests/StopSessionDialogTests.cs` and 9 in
`src/CcDirector.Gateway.UnitTests/GatewayClientStopSessionTests.cs`. Every one of the 22 was watched
failing on purpose, against the mutation named beside it in the tables above.

`.\scripts\test-local.ps1` (the default run):

```
CcDirector.Core.UnitTests            Completed   227 passed
CcDirector.Gateway.UnitTests         4223 passed, 0 failed, 8 skipped   (3 m 57 s - see below)
CcDirector.Avalonia.Tests            Completed   420 passed
CcDirector.Engine.Tests              Completed    63 passed
CcDirector.HostedAgent.Tests         Completed    88 passed
CcDirector.Launcher.Tests            Completed   188 passed
CcDirector.Terminal.Avalonia.Tests   Completed    25 passed
cc-director-setup.Tests              Completed    25 passed
cc-director-setup-engine.Tests       Completed   541 passed
```

**Nothing failed. One thing is wrong with the run and it is NOT mine, and I checked rather than
assumed.** The script reports `OVER BUDGET` and stops `CcDirector.Gateway.UnitTests` at the
120-second ceiling. I ran the same gate a second time with my new test file MOVED OUT of the tree, and
it reported exactly the same `OVER BUDGET - CcDirector.Gateway.UnitTests`, at 4,214 tests in 3 minutes
11 seconds against 4,223 in 3 minutes 57 seconds with it. The difference between the two runs is my
nine tests, which take 502 milliseconds when run on their own. **The suite was already over the
ceiling; my nine tests did not put it there and cannot take it back under.**

> **Corrected 9 September 2026 by the Phase B Manager, on a measurement I did not take.** This
> paragraph originally said the suite "takes about four minutes, so it is over the budget by a factor
> of two". That is wrong, and wrong in the direction that would have sent someone to park a suite that
> does not need parking. The Manager measured it ALONE on a quiet machine at **1 minute 51 seconds -
> inside the ceiling**. It is BORDERLINE, and what pushes it over is the gate's own parallel load, not
> the suite's length. Both of my timings were taken inside a full parallel gate run, so neither of them
> was ever a measurement of the suite; I generalised from two loaded runs to a property of the suite.
> Filed as issue #2780. Left as a correction rather than edited away, because the reasoning error is
> the useful part.

The important part is that the suite still ran to completion in both cases and reported ZERO failures
including mine. `OVER BUDGET` is a verdict about how long the suite takes under the gate's load, not
about whether it passed.

The important part is that the suite still ran to completion in both cases and reported ZERO failures
including mine. `OVER BUDGET` is a verdict about how long the suite takes, not about whether it passed.

### `.\scripts\test-local.ps1 -Parked` - it was run, in full, and here is what it said

I ran it because this change edits `GatewayClient`, which the parked `CcDirector.Gateway.Tests` suite
covers, and the selector said so out loud: `COVERAGE GAP - this change touches code covered by PARKED
suite(s) that did not run`. It took 1 hour 40 minutes end to end; `CcDirector.Gateway.Tests` alone took
1 hour 24 minutes.

```
CcDirector.Core.UnitTests            227 passed
CcDirector.Gateway.UnitTests        4223 passed, 0 failed, 8 skipped     (3 m 35 s)
CcDirector.Avalonia.Tests            420 passed
CcDirector.Engine.Tests               63 passed
CcDirector.HostedAgent.Tests          88 passed
CcDirector.Launcher.Tests            188 passed
CcDirector.Terminal.Avalonia.Tests    25 passed
cc-director-setup.Tests               25 passed
cc-director-setup-engine.Tests       541 passed
CcDirector.Core.Tests               4372 passed, 0 failed, 8 skipped    (10 m 57 s)
CcDirector.Gateway.Tests            2389 passed, 2 FAILED, 47 skipped   (1 h 24 m)
```

**Two tests failed, and they are not mine.** Both are in
`src/CcDirector.Gateway.Tests/PathContainmentLinkEscapeTests.cs`:

- `ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused`
- `ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused`

Both fail on the same line, `CreateFileLink` at line 112, with the same message, which the test itself
wrote:

> This host cannot create a FILE symbolic link (A required privilege is not held by the client.). On
> Windows that needs Developer Mode or elevation. The link-escape regression cannot be proven without a
> real link, so this test fails loudly instead of silently skipping into a false green.

That is a machine privilege on SORENLAPTOP, not a defect, and the test is deliberately built to fail
rather than skip - which is the right design and the reason it is visible at all. They exercise
`ControlEndpoints.ResolveSessionFile` and the screenshot path containment; this change touches neither,
and its own nine tests in that dependency chain (`CcDirector.Gateway.UnitTests`) all passed. Whoever
owns this machine can settle it by turning Developer Mode on and re-running just that class.

**What I am NOT claiming.** I have not re-run those two under elevation, so I have not personally seen
them green on this machine. What I have established is what they failed ON: creating a symbolic link,
before any product code was reached. The stack trace stops inside the test's own setup helper.
