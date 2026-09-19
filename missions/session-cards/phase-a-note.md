# Phase A - what was built, and what it does NOT prove

Branch `mission/session-cards`, cut from `origin/main` at v2.8.0. Two commits, both pushed. No pull
request - the Delivery Lead lands the work.

---

## What was built

### 1. An unrecognised colour name paints a neutral, and magenta means one thing again

`StatusPalette` had one sentinel doing two jobs. It now has two.

- **Magenta `#FF00FF`** keeps its own fault and only that one: the Director is connected and settled
  and the Gateway has stamped nothing. The push seam is broken. Still loud, still logged by
  `ReportMissingStamp`.
- **The neutral `#E5E7EB` (gray-200)** is new: a colour NAME this build was never taught, which only
  ever means this Director is older than its Gateway. Quiet, logged by `ReportUnknownColor`, and the
  log line now says NEUTRAL and says why rather than describing the pixel the code stopped painting.

**The value is measured, not chosen.** The binding constraint was that it must not read as the
palette grey `#6B7280`, which on this rail MEANS snoozed or exited:

| Candidate | vs `#6B7280` | on `#1E1E1E` | on `#252526` | Verdict |
|---|---|---|---|---|
| gray-400 `#9CA3AF` | **1.90:1** | 6.57:1 | 6.03:1 | Rejected. Indistinguishable at 14 pixels, and already on the `StatusPaletteTests` list of hand-rolled strays. |
| gray-300 `#D1D5DB` | 3.28:1 | 11.31:1 | 10.39:1 | Passes, buys nothing over gray-200. (This was the Architect's guess; it was checked, not shipped unchecked.) |
| **gray-200 `#E5E7EB`** | **3.90:1** | **13.47:1** | **12.37:1** | **Shipped.** |
| gray-100 `#F3F4F6` | 4.39:1 | 15.15:1 | 13.92:1 | Rejected. 1.10:1 from white, so it reads as a highlight. |

3:1 is the bar for telling one interface element from another. `#1E1E1E` and `#252526` are
`PanelBackground` and `SidebarBackground` from `docs/VisualStyle.md`.

**"In both themes" resolves to one theme, and that is a finding rather than a shortcut.** The
desktop Director is dark-only: `App.axaml` pins `RequestedThemeVariant="Dark"` and a whole-project
search finds nothing anywhere that changes it. (The Theme dialog writes `~/.claude.json` - that is
the agent's terminal theme, not the Director's.) So those two backgrounds are the only ones this
pixel is ever drawn on. The browser shells have light and dark and never paint this neutral: they
paint the hex the Gateway stamped, and a Gateway is never older than itself.

**The hex is written in `StatusPalette` and deliberately not in `SessionColorPalette`.** That map is
fold-NAME to pixel and the neutral answers to no name. Putting it in the canonical map would invite
the next reader to give it a name and teach the fold to emit it, which is the one thing this mission
must not do.

**A consequence that had to be fixed with it.** The cross-surface agreement check compared the
desktop's `HexFor` against the canonical `HexFor` for whatever name a live Gateway sent. With the
two now answering an unknown name with DIFFERENT sentinels on purpose, it reported *"the desktop
StatusPalette paints 'chartreuse' #E5E7EB, the canonical map #FF00FF ... the desktop palette has
drifted"* - a lie about a build that is simply older than its Gateway. A name no palette knows is
now answered once, as `palette-missing`, naming what each surface paints.

### 2. "What the colours mean" on the desktop

A `?` beside the SESSIONS heading opens a window that renders the Gateway's own legend verbatim -
the same words the Cockpit and the phone show, from `GET /gateway/session-colours`.

It reads the ROUTE and does not compile the words in, even though `SessionColourLegend` is in this
same solution. That is the point: a legend baked into an old Director explains the colours THAT
build knows instead of the ones its Gateway is sending, which is the version gap this whole mission
exists to close. There is no built-in copy to fall back on - a failed read says what went wrong and
draws nothing.

New pieces: `IGatewayColourLegend` (the seam), `GatewayClient.GetSessionColourLegendAsync` (the
read), `SessionColourLegendCache` (last-known copy, warmed when the Gateway goes green, never blocks
and never throws), `ColourLegendDialog` (the window).

### 3. The colour hover says what the Gateway said

The dot hovered `LastStatusReason` - a sentence the Director writes itself, from the only thing it
knows (running or stopped), before the Wingman has judged the turn. A purple "Carrying on" dot said
"needs you". Two more sentences were hard-coded beside it, for snoozed and for dictating.

The hover is now the legend's name for the colour plus the Gateway's stamped label -
`SessionDotHover`, the same rule and the same shape as the web client's `dotTitle`. Nothing is lost
by dropping the two hard-coded sentences: the fold already stamps "Snoozed" and the dictation state
into the label the hover renders.

### The sweep

`missions/session-cards/phase-a-rail-sweep.md`. Seven further places the rail writes its own sentence
about a session, three places it reads the shared fold's words at compile time rather than over the
wire, two dead ones, and one gap Phase A itself opens. **Nothing found by the sweep was changed.**

---

## Proof, and every fix was watched failing

Each fix was reverted, the test watched going red with the reported symptom, the controls watched
staying green, and the fix restored. The tree is clean and the suite is back to baseline.

| Reverted | What went red, with what symptom |
|---|---|
| the neutral arm, back to magenta | 4 tests. `Expected #ffe5e7eb, Actual Fuchsia` on the unknown name end to end through the real view model, and `Expected Not Fuchsia, Actual Fuchsia` on the two-faults-two-pixels test - the exact collapse the mission forbids. The unstamped, grey and canonical-hex controls stayed green. |
| the agreement-check guard | `AColourNoBuildKnows_...` red, printing the finding it would otherwise have shipped: *"the desktop StatusPalette paints 'chartreuse' #E5E7EB ... the desktop palette has drifted from SessionColorPalette"*. |
| the Director's own sentence back on the hover | 3 tests. `Expected "Monitor fix round 2 progress", Actual "needs you"` - the shipped defect reproduced exactly: a calm purple row hovering the words the owner scans for. |
| the legend read, back to the compiled-in copy | 2 tests. `Expected GET, Actual null` - nothing was dialled - and a refused read stopped throwing. |
| the cache inventing a legend when the Gateway fails | `TheCache_OnAFailedRead_...` red, printing the whole made-up legend it had substituted. |
| the window writing its own sentence | 2 tests, printing the Gateway's sentence beside the one the window had written. |

**Acceptance asked for by the mission, and where it is:**
- a colour name no build knows asserts the neutral -
  `SessionRailStateTests.StatusColorBrush_UnknownStampValue_PaintsTheNeutral_NotTheMagentaAlarm`
- a missing stamp still gives magenta, separately -
  `StatusPaletteTests.AnUnknownName_AndAMissingStamp_AreTwoDifferentPixels`
- the two writers disagree and the Gateway wins -
  `SessionRailStateTests.ColourHover_IsTheGatewaysWords_NotTheDirectorsOwnReason`, which names the
  Gateway's words AND asserts the Director's are gone rather than checking the hover is non-empty

---

## What this does NOT prove

1. **Nothing was run.** No Director was launched and no screen was looked at. Every claim here is a
   test or a measurement, not a screenshot. The neutral's contrast is arithmetic against the style
   guide's hexes; nobody has seen the dot.
2. **The local gate could not be run on this machine, at all.** `scripts/test-local.ps1` builds
   `cc-director.sln`, which contains two Windows-only projects (`CcDirector.Terminal`, `CcClick`);
   on macOS the build fails with NETSDK1100 before a single test runs. This is not something this
   change caused - `origin/main` at v2.8.0 behaves identically. What was run instead is every
   default-gate project individually.
3. **Seven tests in `CcDirector.Avalonia.Tests` fail on macOS, and they failed before this work.**
   Verified by checking out `origin/main` at v2.8.0 into a scratch worktree and running the same
   seven: identical result. They are `MicCaptureConstructionQueriesNoDeviceTests` (2),
   `SpeakDialogReadyCueBlankingTests` (3), `SpeakDialogCloseDuringStartupTests` (1) - all
   `dlopen(winmm.dll)`, the Windows audio library - and `LegacyWorkspaceImportTests` (1). With those
   seven, the suite is **567 passed, 7 failed, 574 total**. Against the owner's standing rule of zero
   failures on any platform, these are a real debt; they are not this mission's, and they were not
   adopted.
4. **The other default-gate suites fail on macOS too, and cannot be reached by this change.**
   Core.UnitTests 4, Engine.Tests 1, Launcher.Tests 13, Terminal.Avalonia.Tests 1, Reclaim.Tests 28,
   setup-engine.Tests 24; `cc-director-setup.Tests` does not build here. Not one of those projects
   references `CcDirector.Avalonia`, `CcDirector.ControlApi` or `CcDirector.StateAgreementCheck` -
   checked in their project files - so this diff cannot reach them. **They were not baselined against
   v2.8.0 one by one.** Somebody should run the gate on Windows before this lands.
5. **The Gateway side is untouched and unproven by this phase.** No Gateway test was run. The legend
   route, its words and the fold are exactly as v2.8.0 shipped them.
6. **The cross-surface agreement check was not run against a live fleet**, only against the injected
   faults in `AgreementCheckFaultInjectionTests`.
7. **The window and the hover were proved through their pieces, not through a mounted window.** The
   rows are asserted string for string against the Gateway's own legend; nobody has opened the
   window. The `?` button's placement in the rail header has not been rendered.
8. **The rail repaints when the legend lands** through a new `SessionColourLegendCache.Changed`
   subscription in `MainWindow`. The event is proved; the subscription is not - it is wiring inside
   `TryAttachGatewayMonitor`, which no test drives.
9. **The tests for the Director's Gateway read live in `CcDirector.Avalonia.Tests`**, not beside
   `SnoozeOptionsCacheTests` in `CcDirector.Gateway.UnitTests`, because that suite is parked
   (issue #2824) and a proof that lives only there is a proof nobody sees at commit time. That
   needed one line - `InternalsVisibleTo` for `CcDirector.Avalonia.Tests` on
   `CcDirector.ControlApi.csproj`.

---

## The one thing the Architect has to settle

Phase A leaves the desktop with **two rendering sentinels the Gateway has no words for**, and
neither can be given words without changing the Gateway - which Phase A excludes ("render it
verbatim, no new wording anywhere").

The Gateway's legend carries one note about magenta, worded for the browser clients: *"Not a state.
The screen received a colour this app does not understand."* On the desktop that is **no longer what
magenta means** - it now means the Gateway stamped nothing, and a colour this build does not
understand paints the neutral. So:

- the desktop legend window **leaves the magenta note out**, rather than show a sentence that is
  false on this surface;
- an unstamped session's hover is **empty** - the Gateway said nothing, so the rail says nothing;
- the neutral has no entry anywhere, because it is not a colour the Gateway decides.

Nothing is lost against today: the desktop had no legend at all before this, and the hover it had
was the local sentence the ruling removes. But a person can now see two pixels the app cannot
explain.

**Recommendation:** give the legend a note per RENDERING sentinel rather than one note for
"magenta", each saying what that pixel means and what to do about it, so every surface renders
whichever ones it paints. That is a Gateway change and belongs in a later phase. It is the
Architect's call, and it is recorded rather than guessed at.
