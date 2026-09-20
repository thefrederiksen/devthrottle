# Phase A fixes - what was fixed, how each was proved, and what is still unproved

Branch `mission/session-cards`, in a worktree cut from `origin/main`. No pull request - the Delivery
Lead lands the work.

The independent inspection (`missions/session-cards/inspection-a.md`, Codex, a different agent
family) found two runtime defects and one coverage defect in Phase A. **The builder's own suite was
green for all three, and it had watched six reverts fail.** So none of what follows leans on a green
run: for each fix, the substitution that the old tests could not see was made HERE, the new test was
watched going red with the real symptom, and only then was the substitution restored.

---

## The four substitutions, and what each one printed

| Substitution made in this worktree | What went red, with the symptom it printed |
|---|---|
| **The exact one the inspector made.** In `GatewayClient.GetSessionColourLegendAsync`, read the response and then throw it away: `await resp.Content.ReadFromJsonAsync<SessionColourLegendDto>(ct); var legend = SessionColourLegend.Build();` | `TheWordsThatReachTheDesktopAreTheOnesTheGatewaySent_NotThisBuildsOwnVocabulary` red: *Expected `["Pressing ahead", "Held at the ford", "Put by"]`, Actual `["Needs you", "Working", "Done", "Carrying on", "Being read", ...]`* - this build's own vocabulary, named, where the Gateway's words should be. **Under the same substitution the inspector measured all 100 targeted tests passing.** |
| The legend row's layout back to the shipped horizontal `StackPanel` | Two tests red. `EveryExplanationFitsInsideTheWindow_RatherThanRunningOffIt`: *"The agent is putting terminal output on the screen this very second ..." measures 1970 wide in a 580 viewport - its end is off the right-hand edge of the window.* And `ALongExplanationWraps_InsteadOfBeingCutShort`: *the long explanation is 10 tall against the short one's 10 - it did not wrap onto further lines.* |
| `SessionDotHover.For` joining the colour and the label unconditionally, which is what Phase A shipped | 7 tests red. *Expected "Working", Actual "**Working: Snoozed**"* - the inspector's exact string, reproduced; and the same shape on four other frozen stamps, and *Expected "Needs you", Actual "Needs you: Monitor fix round 2 progress"* on the red half of the floor. |
| `SessionColourLegendCache.ReadNowAsync` starting a read unconditionally, past the in-flight guard | `AColdWindowReadingThenWaiting_DialsTheGatewayOnce_NotTwice` red: *Expected 1, Actual 2* calls to the Gateway for the same words. |

Each substitution was restored from a byte-for-byte backup and the tree re-verified with `git diff`.

---

## Fix 1 - the legend's words run off the window

`ColourLegendDialog.Row` built the dot and the words column in a HORIZONTAL `StackPanel`, and a
horizontal `StackPanel` measures its children with unbounded width - so `TextWrapping.Wrap` had no
finite width to wrap at and every explanation stayed on one line, however wide.

Both horizontal `StackPanel`s in the row are now `Grid`s with `ColumnDefinitions("Auto,*")`: the dot
and the title take what they need, the words and the explanation are measured against the width that
is actually left. The vertical `StackPanel` is untouched and correct - a vertical stack passes its
children the full width it was given and leaves only the HEIGHT unbounded.

**The proof is measured bounds on a MOUNTED window, because a string-equality test on an unmounted
row is what missed this.** `Render` was made `internal` so a test can fill a shown window and let it
lay itself out:

- `EveryExplanationFitsInsideTheWindow_RatherThanRunningOffIt` - every `TextBlock` under the rows
  panel measured against that panel's own width, failing with the offending sentence and both
  numbers.
- `ALongExplanationWraps_InsteadOfBeingCutShort` - the long explanation must be taller than the short
  one, so a clip or an ellipsis cannot pass as a fix.

Both were watched red against the shipped layout, with the numbers above.

## Fix 2 - the hover could read "Working: Snoozed"

`SessionViewModel.ColourHover` passed `EffectiveColor` and `ActivityLabel` together. With the tunnel
down those are about DIFFERENT MOMENTS: `RailColor` paints a live local reading of the terminal,
while `SessionDto.StateLabel` is frozen on whatever the Gateway last said. Joined, they produced a
row contradicting itself.

**The offline floor is not changed** - that is a separate ruling nobody has made, and its pixels are
identical. What changed is the COMBINING.

`RailColor` is now one line over a new `SessionViewModel.RailDotFor`, which returns the colour AND
whether that colour is the Gateway's own stamp for this session (`RailDot`). One function, so the
colour and its provenance cannot drift apart. `SessionDotHover.For` takes the `RailDot`: when the
rail chose the pixel for itself - the floor's blue and red, its grey for a held session with no frozen
stamp, the warm-up placeholder, the unstamped sentinel - it names the colour and stops. That is still
the Gateway's word, because the name comes from the Gateway's legend, and it is the whole of the
owner's ruling: hover the colour, see what that colour means.

The case that is NOT the floor's own word is proved too: a held, idle, offline session keeps the
Gateway's frozen stamp as its colour, so its frozen label describes that same answer and still belongs
on the hover (`Offline_AHeldIdleSession_WearsTheGatewaysOwnStamp_SoItsLabelStillBelongsOnTheHover`).
A fix that simply dropped the label whenever the tunnel was down would have lost that, and would have
passed a test that only looked for the contradiction.

The new tests live in `OfflineFloorRailColorTests`, next to the floor that causes the condition, and
drive the REAL functions the view model composes.

## Fix 3 - the legend-read test could not tell the Gateway's words from this build's own

The old test built its fake response by serialising `SessionColourLegend.Build()` - the vocabulary
compiled into this Director - and asserted the entry count, the presence of cyan, and that no string
was blank. An old build serving its own old words passed. That is not a weak test of the right thing;
it is a test whose expected value is the very thing it exists to rule out.

`GatewayWordsNoBuildKnows` is a legend whose every word exists ONLY on the wire: three entries (not
ten), one of them a colour name this build has never heard of, titles, explanations, "asks for you"
answers, hexes and a verdict note that appear in no compiled constant. Every field is now asserted
character for character, and the titles are asserted FIRST so a substituting build fails by naming the
vocabulary it swapped in.

**The instrument is checked too.** `TheWireOnlyWords_AppearInNoCompiledConstant` serialises the whole
of `SessionColourLegend.Build()` and asserts none of those words appears in it - so nobody can one day
add "Pressing ahead" to the shipped legend and quietly turn the test back into one that passes on a
Director explaining itself.

The same words are then followed the rest of the way: through the cache both readers share
(`TheCache_HandsOnTheGatewaysWords_Unaltered`), onto the hover
(`TheHoverCarriesTheGatewaysOwnWords_EvenWhenThisBuildKnowsNoneOfThem`), and onto the mounted window
(`TheWordsOnTheWindowAreTheGatewaysOwn_EvenWhenThisBuildKnowsNoneOfThem`), which asserts the exact
sequence of strings on screen.

## Fix 4 - two untrue claims in the Phase A note

Corrected in place in `missions/session-cards/phase-a-note.md`, each marked and dated, rather than
silently rewritten:

- *"a failed read says what went wrong and draws nothing"* holds only before a FIRST successful read.
  The cache keeps the previous legend on purpose and the window renders a non-null cached legend
  without checking `Error`, so a failed refresh is invisible on an open window. What holds without
  qualification is the narrower claim: a failure never becomes invented words.
- *"answered once"* about the agreement check reads as a claim of exactly one finding. The guard emits
  `palette-missing` and CONTINUES, and its test asserts a single MATCHING finding, not a one-element
  list. The proved claim is about the palette comparison alone.

Two further sentences in that note were **superseded** by these fixes and are marked as such in place:
the hover's unconditional "plus the Gateway's stamped label", and item 7 of "What this does NOT prove"
("nobody has opened the window"), which is exactly the hole the inspection went through.

## The two smaller items

- **The "by construction" comment was false in this mission's own case.** `ColourLegendDialog.Row`
  claimed the legend swatch and the session dot are the same pixel by construction. For a colour this
  build does not know they are NOT: the swatch shows the Gateway's hex and the rail paints
  `StatusPalette.Neutral`. Corrected, in the code and in `ColourLegendDialogTests`, to say what is
  true - they match for every colour this build knows, and where they do not, the legend is the place
  that can still show the real colour.
- **A cold dialog started two reads.** `Current` begins a background refresh when there is nothing
  held, and `LoadAsync` then awaited `RefreshAsync` directly, walking past the in-flight guard.
  `SessionColourLegendCache.ReadNowAsync` now returns the read already running or starts one, and the
  dialog awaits that; `BeginRefresh` is one line over it, so there is one guarded path.

---

## What was run, exactly

The .NET SDK is not on this shell's `PATH`; every run below used `/Users/soren/.dotnet/dotnet`.

1. **The targeted selection**, the same filter the inspection used:
   `StatusPaletteTests`, `SessionRailStateTests`, `SessionDotHoverTests`, `SessionColourLegendReadTests`,
   `ColourLegendDialogTests`, `AgreementCheckFaultInjectionTests`, `OfflineFloorRailColorTests`.
   **121 passed, 0 failed, 0 skipped** (100 before this work; 21 added).
2. **The whole `CcDirector.Avalonia.Tests` suite: 588 passed, 7 FAILED, 595 total.**
3. **Builds** of every project that depends on what changed - `CcDirector.StateAgreementCheck`,
   `CcDirector.Gateway.UnitTests`, `CcDirector.Gateway.Tests` - all succeeded. `CcDirector.Avalonia`,
   `CcDirector.ControlApi` and `CcDirector.Avalonia.Tests` build with **0 warnings, 0 errors**.

### The suite is RED on this machine, and that is not a pass

Seven tests fail in `CcDirector.Avalonia.Tests` on macOS. They are the same seven the Phase A seat
reported, and they are untouched by this work - four of them load `winmm.dll`, the Windows audio
library:

- `MicCaptureConstructionQueriesNoDeviceTests` (2)
- `SpeakDialogReadyCueBlankingTests` (3)
- `SpeakDialogCloseDuringStartupTests` (1)
- `LegacyWorkspaceImportTests` (1)

**This is stated as a red result, not as a baseline.** The owner's standing ruling is zero test
failures on any platform and never quoting a "pre-existing failures" count as if it were a pass, so
nothing here claims the Avalonia suite is green on this machine. It is not. Whether these seven are
this mission's to fix is the Architect's call; they were not adopted here.

## What this does NOT prove

1. **Nothing was run as an application.** No Director was launched, no window was opened by a person,
   no screenshot was taken. The window's layout is proved by headless font measurements, which
   establish the MECHANISM - the text is handed a finite width and wraps at it - and do not claim exact
   native pixel widths on a real screen.
2. **The local gate could not be run on this machine at all.** `scripts/test-local.ps1` builds
   `cc-director.sln`, which holds two Windows-only projects; on macOS the build fails with NETSDK1100
   before a single test runs, and `origin/main` at v2.8.0 behaves identically. What was run instead is
   listed above. **The default gate must still be run on Windows before this lands** - that was already
   the Architect's condition and nothing here changes it.
3. **The hover fix is proved through the composition, not through a live tunnel drop.** The tests call
   the real `SessionViewModel.RailDotFor` and the real `SessionDotHover.For`, which is what
   `ColourHover` composes - but the three getters that bind the floor's inputs (`IsGatewayOffline`,
   `IsGatewaySettled`, the legend cache) are application-wide and are not driven here. Nobody has
   dropped a real tunnel and hovered a real dot. This is the same limit the inspection recorded for its
   own probe.
4. **No other default-gate suite was run**, and no Gateway suite. Nothing outside
   `CcDirector.Avalonia`, `CcDirector.ControlApi` and their tests was changed, and every project that
   depends on them builds - but "it builds" is not "its tests pass".
5. **The cross-surface agreement check was not run against a live fleet.**
6. **The stale-cache blind spot found by the inspection is recorded, not fixed.** A failed REFRESH is
   still invisible on an already-open legend window, which goes on showing the last-known words. That
   is the deliberate last-known-answer behaviour; whether an open window should say its words are stale
   is a design question for the Architect, and it is not in the fix list.
7. **Phase A2 is untouched.** The desktop still paints two rendering sentinels the Gateway has no words
   for.
