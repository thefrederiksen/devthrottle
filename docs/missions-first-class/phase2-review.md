# Missions First-Class - Phase 2 review

Reviewer: a separate review session, 6 November 2026.
Reviewed: pull request 4619, branch `missions-first-class-phase2`, commit 5fc485fc5, four commits on top of
7fd6561ea (origin/main has since moved by one documentation commit only, 0641d53b4, which touches nothing here).

## Verdict

No defect in the code that proves harm. The Director renders the Gateway's verdict verbatim and decides nothing.
Two things in the proof must be settled before merge (findings 1 and 2); both are cheap. Three smaller findings
follow; none blocks.

## Scope

What I read:
- The mission document, sections 3, 4, 5 and 7; the phase 2 Developer brief; `docs/missions-first-class/phase2-proof.md`
  and all ten screenshots, at full size.
- The whole diff `origin/main...HEAD`: `MissionButtonPresenter.cs`, `MainWindow.axaml` and `.axaml.cs`, `App.axaml`,
  `Session.cs`, `FleetDisplayStateExecutor.cs`, `ControlEndpoints.cs`, the two test files, the fixture report.
- The code it rests on, from the worktree at the reviewed commit (unchanged on origin/main since): the Gateway's
  `MissionButtonFold` and its stamp site in `GatewayEndpoints.cs`, the display state push in
  `FleetDisplayStateObserver.cs`, `MissionButtonDisplay`, `SetDisplayStateRequest`, `SessionDto`, `DevReportLinkRoute`,
  `CockpitUrlResolver`, `GatewayConfig`, both Cockpit click handlers and `OpenUrlInBrowser`, `TerminalWheelPresenter`
  (the precedent this copies), `Styles/ButtonStyles.axaml`, `docs/VisualStyle.md` sections 1, 4 and 13, every place
  `_activeSession` is assigned, and the over-budget and coverage-gap rules in `scripts/test-local.ps1`.
- The pull request title and body, and the four commit messages (no attribution to any tool in either).

What I ran:
- A detached scratch checkout of 5fc485fc5 outside the worktree, then
  `dotnet test src/CcDirector.Avalonia.Tests --filter FullyQualifiedName~MissionButtonPresenterTests`:
  27 passed, 0 failed. The scratch checkout was removed afterwards. Nothing in the worktree was edited.
- A non-ASCII sweep over every added line in the diff: none.

What I could not reach:
- I did not run a Director, so I did not see the buttons myself; the screenshots are my only sight of them.
- I did not run `CcDirector.Gateway.Tests`. I read the one test class in it that reaches changed code
  (`GhostSessionReapableTests`, which calls `ControlEndpoints.Map`): it asserts nothing about the Mission button, and
  the change to `Map` is one more assigned field, so I accept the Developer's reasoning for not running it.
- I cannot confirm from screenshot 03 at its size that the outline button's border survives the pointer; I take the
  Developer's word, with that caveat.
- No screenshot shows a Mission name long enough to be cut at 280 pixels, so the ellipsis is untested on a screen.

## Findings

### 1. Screenshots 04 and 11 do not prove what the proof says they prove

What breaks: the proof file says screenshot 04 proves "Clicking it opens nothing. The Director's log: `address=(none)`
then `the Gateway gave no document address, opening nothing`", and screenshot 11 proves "the log shows both clicks and
'opening nothing'". Neither screenshot contains a log. Screenshot 04 is pixel for pixel the same scene as screenshot
02; screenshot 11 is the same scene as screenshot 05. A click that opens nothing produces a frame identical to no
click at all, so the frame cannot be the evidence, and the log lines the proof names are paraphrased, not quoted.

Why it must change: a check that can be answered without the evidence is not a check. The fix is small: paste the
exact log lines, with their timestamps, from `%LOCALAPPDATA%\cc-director\logs\director\director-2026-11-06-<pid>.log`
into the proof under those two rows (the `[MainWindow] MissionButton_Click: session=..., address=(none)` line and the
`opening nothing` line that follows it, for each click), or retake the two screenshots with the log open beside the
Director. The code path itself is right: `BuildOpenUrl` returns null for a null address and the handler logs and
returns, and `BuildOpenUrl_NoAddress_OpensNothing` pins it.

### 2. The gate's own verdict is OVER BUDGET, not green

What breaks: `.\scripts\test-local.ps1` on 367d2a8cd ended with `RESULT: OVER BUDGET` and exit code 1. Three suites
were stopped at the two-minute ceiling under machine load. The Developer then ran each of the three directly with the
gate's filter, and all passed (2926, 234, and 1023 of 1029 with 6 skipped). So every test in the default run passed on
this commit, but not inside one run of the script, and the repository rule reads "a red local run is a red change".

Why it must change: the merge record should hold a green run of the script, or a written acceptance of the hand-run
evidence by the Delivery Lead on the pull request. A single re-run of the default gate when the machine is quieter is
the cheaper of the two; on the run one commit earlier those three suites finished in 1 minute 16, 1 minute 17 and 15
seconds, so a quiet machine will take it green. I am not asking for anything beyond the default run: the change makes
no Gateway change, the selected parked tests that reach `Session.cs` and `ControlEndpoints.cs` ran and passed (all of
`Gateway.UnitTests`, 85 classes of `Core.Tests`), and the `Gateway.Tests` reach is covered under Scope above.

### 3. The four new colours are not registered in docs/VisualStyle.md

What breaks: `App.axaml` gains `MissionBrush` #4338CA, `MissionOutlineText` #A5B4FC, `MissionWarningBrush` #EAB308 and
`MissionWarningText` #1E1E1E, and `MainWindow.axaml` gains three looks over them. `docs/VisualStyle.md` section 1 is
the palette every UI change must comply with, and section 13 is the button-state table; neither mentions any of them.
The guide already has a "Warning amber" #D97706, so a reader of the guide now sees one warning colour while the
application paints another. The owner's word for no Mission was "yellow", so a new yellow is defensible; the defect is
only that it is unregistered.

Why it must change: the guide is the one place a later developer looks for the colour to reuse, and a colour that
lives only in `App.axaml` is one they will not find and will re-invent. Add a row for each Mission brush to the Core
or Semantic colour table and the three looks (filled, outline, warning) to the Button States table, in the same pull
request. No code change.

### 4. The presenter does not snapshot the verdict on the screen thread, so one redraw can read two verdicts

What breaks: `MissionButtonPresenter` reads `Session.GatewayMissionButton` live in every property getter (`Verdict`,
`Problem`, `IsVisible`, `Label`, `Tooltip`, `Look`), and `UpdateMissionButtons` reads those properties about ten times
across the two buttons. The Control API thread replaces `GatewayMissionButton` on every stamp. A stamp that lands in
the middle of one `UpdateMissionButtons` can draw the label of the new verdict with the look of the old one, or - in
the `AllLooks` loop - compare each look against a different verdict and leave the button with no look class at all.
The stamp also posts `ApplyDisplayState`, so the next dispatcher turn redraws it right; the mis-draw lasts one frame.
The constructor comment promises the opposite: "every change to what the buttons show is made on the screen thread so
the window never reads half of one". `TerminalWheelPresenter` has the same shape, so this is inherited, not new.

Why it should change: the promise in the comment is the one the window relies on, and keeping it costs one field.
Copy the verdict into a private field inside `Attach` and `ApplyDisplayState` (both on the screen thread) and have the
getters read that field. Low severity, self-healing, not blocking; if left, correct the comment so it does not claim
what the code does not do.

### 5. An unknown colour key is invisible on screen - the Delivery Lead decides

What breaks: for a colour key this build does not know, the presenter logs one line and draws nothing. On screen that
is exactly what "the Gateway sent no verdict" looks like. The day the Gateway adds a fourth key, every older Director
goes quiet about Missions with no visible sign, and the one line in a log file is the only trace. The wheel button
precedent (`TerminalWheelPresenter.Problem`, shown in `TabBarWheelNote`) puts the problem on screen beside the empty
spot.

Why it may need to change: the brief said "An unknown colour key is an error that is logged, not a guessed look", and
the Developer met that sentence exactly, with a test (`Attach_UnknownColourKey_ShowsNoButtonAndNamesTheProblem`) that
names the key. So this is a choice for the Delivery Lead, not a defect in the Developer's work: either the brief's
wording stands and nothing changes, or the presenter's `Problem` text is shown the way the wheel's is. I lean to
showing it, for the reason the wheel does.

## Checked and found right

- Rule 7, the client is dumb. `UpdateMissionButtons` copies `IsVisible`, one style class, the label text and the
  tooltip, and nothing else; there is no conditional on `Verdict`. The only mapping is colour key to style class, by
  ordinal dictionary, so `"Mission"` with a capital is unknown (tested). The click branches only on whether the
  Gateway gave an address. The cursor (hand or arrow) is set by the look's style, keyed to the Gateway's colour key.
- The corner button follows the selection. `_missionPresenter.Attach` sits beside `_wheelPresenter.Attach` at all
  four places `_activeSession` is assigned (selection, external removal, close all, shutdown), and there is no fifth
  assignment. Screenshot 05 shows the corner moving to "No mission" when session 149 is selected, and screenshots 07
  to 09 show it back on the fixture Mission for session 129.
- The click address. `BuildOpenUrl` joins the configured Gateway base (`CockpitUrlResolver.ResolveCockpitBase`, the
  same base the Cockpit button probes) to the Gateway's `/r/<id>` and refuses a relative address, a `//host` address and
  an absolute address (tested, and thrown rather than guessed). The Gateway's `MissionButtonFold` builds the address
  from `DevReportLinkRoute.Prefix`, so the two cannot drift. Screenshot 10 shows the browser on
  `gateway.devthrottle.com/report/f7cce310-...`, which is where `/r/<id>` redirects a desktop browser, with the fixture
  report's heading, so the click reached the report.
- A Gateway that sends no verdict shows no button. Both buttons start `IsVisible="False"` in the markup;
  `FleetDisplayStateExecutor` passes `request.MissionButton`, which is null from an older Gateway, through an optional
  parameter that defaults to null; the presenter's `IsVisible` is false for a null verdict
  (`Attach_GatewaySentNoVerdict_ShowsNoButton`); a later stamp without one clears it (the Gateway unit test).
- UI thread safety. All four `Attach` calls run on the screen thread (the external removal path is inside
  `Dispatcher.UIThread.Post`; the others are window handlers). The Gateway's stamp arrives on the Control API thread
  and the presenter posts `ApplyDisplayState` through `Dispatcher.UIThread.Post`. See finding 4 for the one gap.
- Logging. `Attach` logs the session; the click logs the session and address, then the address it opens or "opening
  nothing", and a failure with the exception; `Session.ApplyGatewayDisplayState` logs verdict, colour key and
  address; the executor's line gained the verdict.
- Styles. Colours are `DynamicResource` brushes in `App.axaml`, none inline. The hover rule scoped to
  `Button.missionBtn` re-asserts border and text colour only, not background, so it does not breach the guide's
  "never add a `:pointerover` background" rule and leaves the shared policy untouched. `Focusable="False"` as the
  guide asks for toolbar buttons. `BorderThickness="1"` departs from the guide's "0 on all buttons" but matches the
  tab bar Cockpit button it sits beside, which was drawn bordered on purpose so it does not read as a tab.
- Fallback programming. None found. A null base throws, a malformed address throws, an unknown key draws nothing and
  says why in the log; nothing substitutes a default.
- Tests. 27 presenter tests, named `Method_Scenario_Result`, cover the three keys, four unknown keys (including case
  and null), the three states verbatim, no verdict, no session, following the selection, the left session no longer
  redrawing, a new verdict redrawing, a withdrawn verdict hiding, the stored copy, and the address joining with and
  without trailing slashes. The Gateway unit test drives the real verb through real JSON and out of `Map`. No test
  drives `UpdateMissionButtons` itself; it needs a screen, and the wheel precedent has none either.
- The corner's label has no "Mission: " prefix, unlike the mockup. The Developer's reason is sound: a client-side prefix
  would read "Mission: No mission" or need a branch on the verdict, which rule 7 forbids, and the mission document's
  section 3 wording ("shows the selected session's Mission name, or yellow 'No mission'") is met. If the prefix is
  wanted it is a Gateway label change for a later phase.
- One observation, no change asked: for a Director with no `gateway.url` configured, the base is the loopback default
  `http://127.0.0.1:7878`, so the Mission button would open a loopback address - something the Cockpit button refuses
  to do on principle and instead asks the Gateway for a tailnet address. The brief told the Developer to join the base
  and the address, and `/r/<id>` on a loopback Gateway does resolve on that machine, so it works; it is simply a
  different rule from the button beside it. Worth one line in the mission document if anyone asks later.
- The fixture Mission was completed, the test Director stopped with its named signal, slot 5 restored, and the real
  mission document not republished, as the brief required.

## Re-check of 98fce4161

Checked out `98fce4161` in this worktree (detached) at the Delivery Lead's request. The code is `09f904b74`; the last
commit adds proof text only (`git show 98fce4161 --stat` names one file, the proof).

**The rebase brought in nothing unexpected.** `git range-diff 7fd6561ea..5fc485fc5 d0d36d3c1..98fce4161` shows the
four original commits as `=` (identical patches) and two new commits: the answer to the review and the gate text. The
new base `d0d36d3c1` is on origin/main, eight commits behind its tip when I looked, all of them documentation and
resource-monitor work unrelated to this change.

**What I ran:** a detached scratch checkout of `09f904b74` outside this worktree, then the presenter tests:
33 passed, 0 failed (27 before, 6 new). The scratch checkout was removed. I grepped the real slot-5 Director log on
this machine for the quoted click lines. I did not run the gate; the Delivery Lead is running it. A non-ASCII sweep
of the new commits found nothing; neither commit message carries any attribution.

Verdict per finding:

1. **Screenshots 04 and 11 - CLOSED.** The proof now says the frame is not the evidence and quotes ten log lines with
   timestamps. I read the file the proof names
   (`%LOCALAPPDATA%\cc-director\instances\slot-5\logs\director\director-2026-11-06-5980.log`): every `MissionButton_Click`
   line in it matches the quoted block exactly, and there are no others. Five clicks, two of them opening nothing on
   the outline button and the "No mission" button, as claimed.
2. **The gate - CLOSED on the Delivery Lead's evidence.** The proof records the Delivery Lead's `-Parked` run on
   5fc485fc5 as GREEN with `CcDirector.Gateway.Tests` included, and a default run on 09f904b74 as GREEN, every suite
   Completed. I did not re-run either. One text defect in that section, which must be fixed because a reader cannot copy
   the command: lines 187 and 217 of the proof contain a literal tab character where a backslash and a `t` were meant,
   so the command reads `.\scripts<tab>est-local.ps1` instead of `.\scripts\test-local.ps1`. Low; a one-word edit.
3. **The colours in docs/VisualStyle.md - CLOSED.** Four rows in the Semantic Colors table, with the yellow marked as
   deliberately not the warning amber, and three rows in the Button States table with a note that the Gateway's key
   chooses the look. The values match `App.axaml` and the three styles.
4. **The verdict snapshot - CLOSED, with one new low finding.** `MissionButtonPresenter` now holds `_shown`, written
   only in `Attach` and in the posted `ApplyDisplayState`, and every getter reads it; the constructor comment's promise
   is now true. The new test runs the posted work by hand and proves label, tooltip, look and address all stay on the
   old verdict until the redraw runs, then all move together.

   New, low: in `Attach` the snapshot is taken one line BEFORE the subscription
   (`_shown = session?.GatewayMissionButton;` then `session.OnGatewayDisplayStateChanged += ...`). A stamp that lands
   between those two lines is in neither the snapshot nor a posted redraw, and because the Gateway's push is
   change-gated, the stale verdict stays on both buttons until that session's verdict changes again or it is
   reselected. The window is microseconds wide and opens only on selection, so this is not blocking; the fix is to
   swap the two lines so a stamp in between posts a redraw that re-reads. `TerminalWheelPresenter` does not have this
   gap only because it reads live; the snapshot is still the right design.
5. **The unknown key on screen - CLOSED.** `Note` returns `Problem`; the window shows it in `TabBarMissionNote` and
   `BtnMissionNote`, in place of the button, with the sentence as tooltip. The two are mutually exclusive by
   construction: a non-null `Problem` makes `Verdict` null, so the button hides when the note shows. Three tests cover
   the unknown key, the three known keys, and no verdict (which says nothing, as an older Gateway should). The note's
   colour is inline `#F48771`, the same inline value `TabBarWheelNote` uses, so I do not ask for a resource. The window's
   copying onto the controls is untested, as the wheel's is; the Delivery Lead's green run is the only sight of it
   compiling and the Developer says plainly it was not screenshotted and why.

**Nothing new that proves harm.** The change at 98fce4161 is fit to merge. The two low items (the tab characters in
the proof, the subscribe-before-snapshot order) can go in the same pull request or straight after; neither should hold
it.
