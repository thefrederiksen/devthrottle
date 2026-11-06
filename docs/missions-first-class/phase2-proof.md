# Missions First-Class - Phase 2 proof (the Director)

## What was built

- **The verdict reaches the session.** `set-display-state` (`FleetDisplayStateExecutor`) now passes the Gateway's
  `MissionButton` to `Session.ApplyGatewayDisplayState`, which stores a copy as `Session.GatewayMissionButton`,
  compares it by value so a rename or a new goal redraws the button, and logs it. `ControlEndpoints.Map` echoes it
  like the other Gateway display fields; the Gateway's fold assigns it fresh, so the echo never survives. The new
  parameter is optional and defaults to null, so nothing else that calls the method changed.
- **`MissionButtonPresenter`** (`src/CcDirector.Avalonia/MissionButtonPresenter.cs`) holds everything the button
  decides, so it is tested without a screen. It follows one session (`Attach`), redraws on the Gateway's push, and
  gives the window the label and the tooltip verbatim, the look, and whether there is anything to open. The only
  mapping it does is the colour key to a style class: `mission` -> `missionFilled`, `mission-outline` ->
  `missionOutline`, `warning` -> `missionWarning`. An unknown key is logged as a problem and no button is drawn -
  never a guessed look. No verdict at all (a Gateway older than phase 1) means no button.
- **Two buttons, one presenter.** `TabBarMissionButton` sits just before `TabBarCockpitButton`, so the Cockpit button
  keeps its spot at the right edge. `BtnMission` sits just before the toolbar `BtnCockpit`. Both show the selected
  session; the presenter is attached wherever the window changes the selected session (select, removal, close-all,
  shutdown), so the corner follows the selection.
- **The looks** are styles in `MainWindow.axaml` over four brushes added to `App.axaml` (`MissionBrush` #4338CA,
  `MissionOutlineText` #A5B4FC, `MissionWarningBrush` #EAB308, `MissionWarningText` #1E1E1E): indigo filled with
  white text; transparent with an indigo border and light indigo text; yellow with dark text. Long names are cut
  with an ellipsis at 280 pixels.
- **The click** opens the Gateway base this Director is connected to (`CockpitUrlResolver.ResolveCockpitBase`, the
  same base the Cockpit button uses) joined with the Gateway's `/r/<id>`, through the same `OpenUrlInBrowser`. The
  address is joined, never composed: an address that is not a path on the Gateway (no leading slash, or a `//`
  host) throws and is shown as an error rather than opened.

### What a click does with no address (the choice the brief asked me to state)

With no address - a Mission with no document yet, or no Mission - the click logs "the Gateway gave no document
address, opening nothing" and returns. That is what the tab bar Cockpit button does when its target is gone: it
logs and opens nothing rather than opening something else. The button stays enabled so its tooltip still shows on
hover, and its cursor is the arrow rather than the hand in those two looks, so it does not invite a click.

### What the corner shows

The mockup drew the corner as "Mission: <name>". The corner shows the Gateway's label verbatim - the Mission's name,
or "No mission" - with no prefix, because a client-side "Mission: " prefix would read "Mission: No mission" for the
warning, and avoiding that would mean the view branching on the verdict, which rule 7 forbids. The goal's own words
("The Director's corner button shows the selected session's Mission name, or yellow 'No mission'") are met exactly.
If a prefix is wanted, it belongs in the Gateway's label for the corner.

## The tests, and what each proves

`src/CcDirector.Avalonia.Tests/MissionButtonPresenterTests.cs` (verdicts built field for field as
`MissionButtonFold` builds them)
- `LookFor_KnownColourKey_ReturnsItsOneLook` - each of the three keys maps to its one look.
- `LookFor_UnknownColourKey_ReturnsNoLook` - an unknown key, a key differing only in case, empty and null map to no look.
- `Attach_UnknownColourKey_ShowsNoButtonAndNamesTheProblem` - an unknown key draws no button and the problem names the key.
- `Attach_MissionWithDocument_CarriesLabelAndTooltipVerbatimInTheFilledLook`,
  `Attach_MissionWithoutDocument_OutlineLookAndNothingToOpen`, `Attach_NoMission_WarningLookSayingNoMission` - the
  three states, with label and tooltip carried verbatim.
- `Attach_GatewaySentNoVerdict_ShowsNoButton` - the merge plan's promise: an older Gateway means no button.
- `Attach_NoSessionSelected_ShowsNoButton`.
- `Attach_SelectionMovesToAnotherSession_FollowsTheNewSession` - the corner follows the selection.
- `Attach_SelectionMovedAway_OldSessionsNewVerdictNoLongerRedraws` - a session the window left cannot redraw it.
- `GatewayPush_NewVerdictForTheSelectedSession_RedrawsWithTheNewWords` - publishing the document turns the outline
  button filled without reselecting.
- `GatewayPush_VerdictWithdrawn_HidesTheButton`.
- `ApplyGatewayDisplayState_StoresACopy_ALaterChangeToTheCallersObjectIsNotSeen`.
- `BuildOpenUrl_WithAddress_JoinsTheGatewayBaseAndTheAddress` (with and without trailing slashes),
  `BuildOpenUrl_NoAddress_OpensNothing`, `BuildOpenUrl_AddressNotAPathOnTheGateway_Throws` (relative, `//host`,
  absolute), `BuildOpenUrl_NoGatewayBase_Throws`, `BuildOpenUrl_SelectedSessionsVerdict_OpensThatSessionsDocument`.

`src/CcDirector.Gateway.UnitTests/SessionCommandExecutorTests.cs`
- `DispatchAsync_SetDisplayState_StoresTheMissionButtonVerbatim_MapEchoesIt_AndALaterStampWithoutOneClearsIt` - the
  verdict goes through the real JSON payload and the real verb onto the session, field for field, comes back out of
  `Map`, and a later push without it clears it.

## In a real Director (slot 5, connected to the live hosted Gateway)

Built with `scripts\local-build-avalonia.ps1 -Slot 5 -NoRuleHook` (this machine has no C++ linker, so the rule hook
cannot be compiled; it plays no part in this change) and run as instance `slot-5` against
`https://gateway.devthrottle.com`. Two PowerShell sessions were opened in it: session 129 in a fixture Mission
"Missions First-Class - phase 2 fixture", and session 149 in no Mission.

| Screenshot | What it proves |
|---|---|
| `02-mission-without-document.png` | Before the fixture document was published: the Mission's name in the indigo outline, in the tab bar and in the corner. |
| `03-mission-without-document-tooltip.png` | Its tooltip, the Gateway's words: the name and "This Mission has no document yet." The border stays on hover. |
| `04-mission-without-document-click.png` | The frame after clicking it. A click that opens nothing looks the same as no click, so the frame is not the evidence; the Director's log is, quoted below. |
| `05-no-mission.png` | Session 149 selected: yellow "No mission" in the tab bar, and the corner followed the selection to "No mission". The session works normally. |
| `06-no-mission-tooltip.png` | Its tooltip, the Gateway's words, telling how to attach a Mission. |
| `11-no-mission-click-opens-nothing.png` | The frame after clicking "No mission" in the tab bar. As with 04, the evidence that nothing opened is the log quoted below. |
| `07-mission-with-document.png` | Session 129 after `python tools\cc-dev-reports\main.py open ...\phase2-fixture-report.html --mission-document` run from source inside it: the Gateway answered with the mission document link and the goal, and both buttons turned indigo filled without reselecting. |
| `08-mission-with-document-tooltip.png` | The tab bar tooltip: "Mission: Missions First-Class - phase 2 fixture" and "Goal: Show the three states of the Mission button in a slot-5 Director, and open this page from it." |
| `09-corner-tooltip.png` | The corner button, same session, same verdict. |
| `10-mission-with-document-click-opens-report.png` | The click opened `https://gateway.devthrottle.com/r/f7cce310-60c1-4af7-93d1-1428dd8b9601`, which landed in the Cockpit on the fixture report. The corner button's click logged the same address. |

Every Mission button click in that run, quoted exactly from the test Director's log
(`%LOCALAPPDATA%\cc-director\instances\slot-5\logs\director\director-2026-11-06-5980.log`; a slot instance keeps
its log under its own folder). Session `9a84795a...` is session 129, in the fixture Mission; `6d2b8a8c...` is
session 149, in no Mission.

```
2026-11-06 07:03:52.797 [MainWindow] MissionButton_Click: session=9a84795a-327f-4967-8bd9-19f9f6d97567, address=(none)
2026-11-06 07:03:52.798 [MainWindow] MissionButton_Click: the Gateway gave no document address, opening nothing
2026-11-06 07:16:04.330 [MainWindow] MissionButton_Click: session=9a84795a-327f-4967-8bd9-19f9f6d97567, address=/r/f7cce310-60c1-4af7-93d1-1428dd8b9601
2026-11-06 07:16:04.330 [MainWindow] MissionButton_Click: opening https://gateway.devthrottle.com/r/f7cce310-60c1-4af7-93d1-1428dd8b9601
2026-11-06 07:21:36.447 [MainWindow] MissionButton_Click: session=9a84795a-327f-4967-8bd9-19f9f6d97567, address=/r/f7cce310-60c1-4af7-93d1-1428dd8b9601
2026-11-06 07:21:36.448 [MainWindow] MissionButton_Click: opening https://gateway.devthrottle.com/r/f7cce310-60c1-4af7-93d1-1428dd8b9601
2026-11-06 07:22:03.874 [MainWindow] MissionButton_Click: session=6d2b8a8c-0552-4bc6-9e58-6b1260f1da58, address=(none)
2026-11-06 07:22:03.875 [MainWindow] MissionButton_Click: the Gateway gave no document address, opening nothing
2026-11-06 07:22:15.717 [MainWindow] MissionButton_Click: session=6d2b8a8c-0552-4bc6-9e58-6b1260f1da58, address=(none)
2026-11-06 07:22:15.718 [MainWindow] MissionButton_Click: the Gateway gave no document address, opening nothing
```

In order: 07:03:52 is the outline button clicked before the document existed (screenshot 04); 07:16:04 is the tab bar
button after it was published (screenshot 10); 07:21:36 is the corner button on the same session; 07:22:03 is the tab
bar "No mission" (screenshot 11) and 07:22:15 the corner "No mission". Those are the only five Mission button clicks
in the log.

The fixture report is committed as `phase2-fixture-report.html`. The fixture Mission was completed afterwards
(`cc-devthrottle mission complete 1d5f359f-c09b-4363-bb38-05ef85c765d5`), the test Director was stopped with its named
signal (which ended its two sessions), and slot 5's configuration was put back as it was. The real mission
document was not republished; that is phase 3.

Two things about the run itself, so nobody repeats them: the `cc-director-launch` scheduled task failed to start on
this machine with 0xC0000142 (the known desktop failure under load), so the Director was started through WMI
`Win32_Process Create`, which is also outside this session's process tree; and a slot build must be started with
`--instance slot-5`, or it targets the default instance (the single-instance guard refused that first attempt, which
touched nothing and was stopped).

## A defect found in the screenshots, and fixed

The first build lost the outline button's indigo border under the pointer: `Styles/ButtonStyles.axaml` keeps a
button's own background on hover but not its border or text colour, which the Fluent theme repaints. The Mission
button now keeps its own border and text colour on hover and press (a style scoped to `Button.missionBtn`; the
shared policy for every button is unchanged). `03-mission-without-document-tooltip.png` is from the fixed build.

## The gate

`.\scripts\test-local.ps1` on commit 367d2a8cd, tree clean:

```
CcDirector.Core.UnitTests                outcome=NO-TRX       total=0      executed=0
CcDirector.Avalonia.Tests                outcome=Completed    total=1242   executed=1242
CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
CcDirector.Launcher.Tests                outcome=NO-TRX       total=0      executed=0
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=111    executed=111
CcDirector.Reclaim.Tests                 outcome=Completed    total=307    executed=307
CcDirector.DeliveryQualification.Tests   outcome=Completed    total=151    executed=151
CcDirector.RuleHook.Tests                outcome=Completed    total=70     executed=70
cc-director-setup.Tests                  outcome=Completed    total=43     executed=43
cc-director-setup-engine.Tests           outcome=NO-TRX       total=0      executed=0
cc-director-setup-cli.Tests              outcome=Completed    total=78     executed=78
CcDirector.Gateway.UnitTests             outcome=Completed    total=17033  executed=17033
CcDirector.Core.Tests                    outcome=Completed    total=770    executed=770
RESULT: OVER BUDGET - 3 suite(s) exceeded the 120-second ceiling and were STOPPED:
  CcDirector.Core.UnitTests, CcDirector.Launcher.Tests, cc-director-setup-engine.Tests
```

The script's verdict is OVER BUDGET, not green. Those three were stopped at the two-minute ceiling, not failed: the
machine was heavily loaded (the whole Gateway unit suite took 18 to 20 minutes on each run). On the run one commit
earlier they finished inside the ceiling (1 minute 16, 1 minute 17 and 15 seconds). The script will not re-run an
over-budget run, so I ran each of the three directly on 367d2a8cd with the gate's filter (`Gate!=Quarantine`):

```
CcDirector.Core.UnitTests        Passed!  - Failed: 0, Passed: 2926, Skipped: 0, Total: 2926
CcDirector.Launcher.Tests        Passed!  - Failed: 0, Passed:  234, Skipped: 0, Total:  234
cc-director-setup-engine.Tests   Passed!  - Failed: 0, Passed: 1023, Skipped: 6, Total: 1029
```

So every test in the default run passed on this commit, but not inside one script run. Two earlier reds, both
fixed before this run:
- On the first run, before rebasing, `FactorySeatStartTests.ReadPublished_APublishedVersionWithASeatHavingAnEmptyBrief_RefusesItWithClearGuidance`
  failed. It failed the same way alone, and it came in red on main with 68de3f920; main fixed it in #4606. After
  rebasing onto origin/main it passes (16 of 16 in its class).
- On the run one commit earlier, `GateTestsDoNotDependOnTheMachineTests` refused my test for naming a loopback
  address with a port in a test case (`http://127.0.0.1:7878`). The case now uses a hostname; 367d2a8cd is that fix.

**Not run: `CcDirector.Gateway.Tests`.** The script names it as a coverage gap because `Session.cs` and
`ControlEndpoints.cs` changed. The brief scopes `-Parked` to a Gateway change, and this one makes no Gateway change;
the change to those two files is an optional parameter that defaults to null and one more echoed field, and the
selected parked tests that reach them (all of `Gateway.UnitTests`, 85 classes of `Core.Tests`) passed. The
reviewer may ask for `-Gateway`.

## Answers to the review (`_briefs/missions-first-class/phase2-review.md`)

1. **Screenshots 04 and 11 do not prove that nothing opened - ACCEPTED.** They cannot: a click that opens nothing
   leaves the same frame as no click. The two rows now say so, and the evidence is the Director's log, quoted above
   with timestamps for all five Mission button clicks of the run, including both clicks that opened nothing on the
   outline button and both on "No mission".
2. **The gate's verdict was OVER BUDGET - SETTLED by the Delivery Lead.** The Delivery Lead ran
   `.\scripts\test-local.ps1 -Parked` on 5fc485fc5 and it was GREEN, every suite Completed, `CcDirector.Gateway.Tests`
   included. The code changes made for findings 4 and 5 were then gated again with the default run; that result is in
   "The gate after the review" below.
3. **The four colours are not in `docs/VisualStyle.md` - ACCEPTED.** The four brushes are rows in section 1's
   Semantic Colors table (the yellow noted as deliberately not the warning amber, because the owner asked for yellow),
   and the three looks are rows in section 13's Button States table, with a note that the Gateway's key chooses them
   and that they keep their border and text colour under the pointer.
4. **The presenter read the verdict live, so one redraw could read two verdicts - ACCEPTED, fixed.** The presenter
   now copies the session's verdict into one field, on the screen thread only (in `Attach` and in the posted redraw),
   and every getter reads that copy, so the comment's promise is now true. The session replaces its verdict by one
   reference assignment of a fresh copy, so the copy is always one whole verdict. Test:
   `GatewayPush_BeforeThePostedRedrawRuns_EveryGetterStillReadsTheShownVerdict` - with a screen thread the test runs
   by hand, a stamp leaves label, tooltip, look and address all on the old verdict until the posted redraw runs, then
   all on the new one. Reading the session live, as before, fails its first assertion.
5. **An unknown colour key is invisible on screen - CHANGED, as the Delivery Lead ruled.** The presenter has a `Note`
   (its `Problem`, verbatim, null otherwise), and the window shows it in place of the button, in both places
   (`TabBarMissionNote` beside the tab bar Cockpit button, `BtnMissionNote` beside the toolbar Cockpit button), in the
   problem colour `TabBarWheelNote` uses (#F48771), cut with an ellipsis and with the whole sentence as its tooltip.
   Tests: `Note_UnknownColourKey_IsTheProblemSoTheScreenSaysWhyThereIsNoButton`, `Note_KnownColourKey_IsNull` (all
   three keys), `Note_GatewaySentNoVerdict_IsNull` (an older Gateway is not a problem and says nothing). Not
   screenshotted: the live Gateway sends only known keys, so a real Director cannot be shown one; like the wheel's
   note, the window's copying of the note onto the controls has no test, because it needs a screen.

The reviewer's observation about a Director with no Gateway configured (the base is the loopback default, which the
Cockpit button refuses on principle) needs no change: such a Director is sent no Mission verdict by any Gateway it is
not connected to, and the brief asked for the configured base joined with the address. Noted here as the reviewer
suggested.

## The gate after the review

`.\scripts\test-local.ps1` on commit 09f904b74 (the review changes, rebased onto origin/main), tree clean:

```
CcDirector.Core.UnitTests                outcome=Completed    total=3298   executed=3298
CcDirector.Avalonia.Tests                outcome=Completed    total=1248   executed=1248
CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
CcDirector.Launcher.Tests                outcome=Completed    total=234    executed=234
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=111    executed=111
CcDirector.Reclaim.Tests                 outcome=Completed    total=307    executed=307
CcDirector.DeliveryQualification.Tests   outcome=Completed    total=151    executed=151
CcDirector.RuleHook.Tests                outcome=Completed    total=70     executed=70
cc-director-setup.Tests                  outcome=Completed    total=43     executed=43
cc-director-setup-engine.Tests           outcome=Completed    total=1029   executed=1023
cc-director-setup-cli.Tests              outcome=Completed    total=78     executed=78
CcDirector.Gateway.UnitTests             outcome=Completed    total=17233  executed=17233
CcDirector.Core.Tests                    outcome=Completed    total=770    executed=770
RESULT: GREEN at commit 09f904b742aaa2389e92252c88547f8592e444ab, tree clean.
```

The only commit after it adds this section to this file. Before the green run, on the same code:
- On 2ce0dd13b the run was OVER BUDGET (Core.UnitTests and Launcher.Tests stopped at the ceiling, none failed) and
  then, re-run, failed one test, `MacOsResourceProbeSafetyTests.Read_OnAPlatformThisProbeCannotRead_SaysSoForEveryResourceAndNeverReportsZero`,
  which failed again alone under `-RerunFailed`. It came from main (c102fa2e4, the Resource Monitor) and main had
  since changed that code; after rebasing onto origin/main it passes (5 of 5 in its class).
- On 09f904b74 the first run was OVER BUDGET on Avalonia.Tests alone while two other worktrees ran the whole Gateway
  unit suite at the same time; the second run on the same commit is the green one above.

The Delivery Lead's own default run on 09f904b74 was also GREEN.

`CcDirector.Gateway.Tests` is still named as a coverage gap by the default run; the Delivery Lead's `-Parked` run on
5fc485fc5 covered it, and the review changes touch only the Director's presenter, window and style files, which that
suite does not reach.

## Answers to the re-check (`Re-check of 98fce4161` in the review)

- **The snapshot was taken before the subscription in `Attach` - ACCEPTED, fixed.** `Attach` now subscribes to
  `OnGatewayDisplayStateChanged` first and takes the snapshot second, so a stamp landing between the two posts a
  redraw that re-reads the session; in the old order it was in neither the snapshot nor a redraw, and because the
  Gateway's push is change-gated it could stay stale until the verdict changed again or the session was reselected.
  No test reproduces the gap: it is two adjacent lines on one thread with no seam to stop between them, and a test
  that hand-built the interleaving would not watch the real caller.
- **A literal tab in the gate command, twice - ACCEPTED, fixed.** Both now read `.\scripts\test-local.ps1`.
- **The coverage gap for `CcDirector.Gateway.Tests`** is answered by the Delivery Lead's `-Parked` GREEN on 5fc485fc5,
  which included that suite. `git range-diff` shows the four commits it ran as identical patches after the rebase,
  and the commits since touch only the Director's Avalonia files (the presenter, the window, its markup, the presenter
  tests) and documents, none of which that suite reaches.
- **The Delivery Lead's default gate on 09f904b74 was GREEN**, recorded under "The gate after the review".
- The gate on the final commit of this pull request is below.

### The gate on the final commit

(Filled in below.)
