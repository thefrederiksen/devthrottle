# Phase A inspection

Inspected 19 September 2026 at `9c707d2a473d14c1184cb767e572b411a5cab35f`, against `origin/main` at `643304d3f36b6744220b52256cdff9457caf068a`. Separate worktree: `/Users/soren/ReposFred/devthrottle-inspect-a`; branch: `inspection/session-cards-phase-a`. The mission worktree was not touched. No implementation fixes were made. All experimental implementation changes were restored byte-for-byte.

**Two runtime defects found**, plus a demonstrated test blind spot recorded separately below. The worst is the legend layout: the explanations this feature exists to show extend beyond its viewport. Neither the platform build limitation nor the already-reported suite failures is counted as a finding.

Scope: read the mission rulings, implementation note, rail sweep, production diff, changed test classes, existing offline-floor tests, stamp normalization, canonical palette and legend contracts. The fetched range actually contains four commits: the mission document commit `a468a3c7b`, implementation commits `9f932b407` and `6a7884afb`, and note commit `9c707d2a4`. Production findings below concern the two implementation commits.

## Findings

### F1 — Legend explanations do not wrap within the dialog (P1)

`src/CcDirector.Avalonia/ColourLegendDialog.axaml.cs:157` puts the dot and its entire words column inside a horizontal `StackPanel`. That panel measures the words with unbounded horizontal space. Consequently `TextWrapping.Wrap` at line 149 has no finite width at which to wrap. The dialog starts at 620 pixels wide (`ColourLegendDialog.axaml:5`), with a 20-pixel margin, and its scroll viewer at line 36 provides vertical scrolling, not a way to read the overflowing explanation horizontally.

Reproduction used the actual `ColourLegendDialog`, invoked its private `Render` with `SessionColourLegend.Build()`, called `Show()` and `UpdateLayout()` in an `AvaloniaFact`, then enumerated all ten explanation `TextBlock`s. Actual headless layout measurements:

| Explanation | Text width | Rows viewport width |
|---|---:|---:|
| Needs you | 920 | 580 |
| Working | 760 | 580 |
| Done | 1290 | 580 |
| Being read | 1570 | 580 |
| Process died | 320 | 580 |

Nine of ten explanations exceeded the viewport; the check failed on measured bounds, not on a missing control or a startup exception. These are headless font measurements, not a screenshot or a claim about exact native-font pixel widths. They expose the real layout mechanism: the text receives unlimited width and therefore stays on one line regardless of the window's available width.

Harm: opening “What the colours mean” hides the ends of the Gateway's explanations, including the alternatives that explain the state. The note's claim that the window “renders the Gateway's own legend verbatim” (`phase-a-note.md:55`) is true of stored `Text` values but unsupported as a claim about readable screen content. `ColourLegendDialogTests.ARow_ShowsTheGatewaysTitle_ItsAsksForYou_AndItsSentence_AndNothingElse` (`:29`) only enumerates strings on an unmounted row. It cannot catch this.

### F2 — Offline hover combines a current local colour with a contradictory stale Gateway label (P2)

The new `SessionViewModel.ColourHover` at `src/CcDirector.Avalonia/SessionViewModel.cs:483` passes `EffectiveColor` and `ActivityLabel` together. They do not necessarily describe the same state:

- `EffectiveColor` uses `RailColor` (`:214`). When disconnected and locally working, it returns `blue` regardless of the frozen stamp (`:240`).
- `ActivityLabel` continues to return the last Gateway label (`:668`).
- `SessionDotHover.For` concatenates the legend title for that local colour with the stale label (`src/CcDirector.Avalonia/SessionDotHover.cs:45`, `:50`).

Constructed state: the last Gateway answer is grey / “Snoozed”; the tunnel drops; the session is now locally Working. Executing the actual `RailColor(true, "grey", ActivityState.Working, true, false)` and passing its result plus the stamped “Snoozed” to the actual hover formatter produces **“Working: Snoozed”**. The probe expected “Working” as one noncontradictory example; its decisive evidence is the actual contradictory string. This is not a proposed authorization to change the offline floor or invent a new state sentence.

The floor and frozen row label predate this diff. The new defect is explicitly combining them into one explanatory hover. `SessionDotHover.cs:19` claims the hover “cannot disagree with the dot it is attached to”; the code does not support that guarantee. The note (`phase-a-note.md:74`) describes the normal online composition without this exception. Existing hover tests use mutually consistent inputs and never combine the offline floor with a stale stamp.

Evidence limit: this probe executed the two actual functions composed by the getter, not a live disconnect through `MainWindow` and `GatewayConnectionMonitor`. The getter's wiring and the stale label source were inspected directly.

## What a constant can replace while the tests stay green

**Observed mutation, not speculation:** in `GatewayClient.GetSessionColourLegendAsync`, replace the assignment at `src/CcDirector.ControlApi/GatewayClient.cs:850` with:

```csharp
await resp.Content.ReadFromJsonAsync<SessionColourLegendDto>(ct);
var legend = SessionColourLegend.Build();
```

This still makes the GET, checks its status and parses JSON, but discards every word received and substitutes the constant vocabulary compiled into this Director. All **100 targeted tests passed, zero failed, zero skipped**, with this mutation. Restored afterward.

The test that should catch it is `SessionColourLegendReadTests.TheDirectorAsksTheGatewaysOwnRoute_WithItsCredential_AndParsesTheAnswer` (`src/CcDirector.Avalonia.Tests/SessionColourLegendReadTests.cs:58`). Its response body is serialized from the same compiled `Build()` (`:49`), and its assertions only check count, presence of cyan and nonempty title/meaning (`:74`). An old build returning its own old words passes. Supplying distinct wire-only wording/new entries and asserting exact propagation would distinguish the two implementations; the current test does not.

This is a demonstrated coverage defect, recorded separately from the two runtime defects: the shipping implementation does return the deserialized object. I do not claim it currently substitutes the constant. Nor do I claim the entire repository suite is green: the 100-test selection is the evidence.

A second unproved claim in the note is “a failed read says what went wrong and draws nothing” (`phase-a-note.md:61`). That only holds before a successful read. The cache deliberately retains a previous legend on failure (`SessionColourLegendCache.cs:150`), and the dialog renders a non-null cached legend without checking `Error` (`ColourLegendDialog.axaml.cs:61`, `:78`). A stale-cache refresh failure is logged but not shown on that open dialog. Retaining a last-known answer is explicitly documented elsewhere in the same note, so I count this as inaccurate reporting, not a separate runtime finding.

## Sentinel reachability and the pre-existing floors

For real rail inputs, I found **no currently known colour that reaches neutral**, and **no connected, settled, genuinely unstamped session that reaches neutral**. The tempting empty-string counterexample is false:

1. `Session.ApplyGatewayDisplayState` normalizes blank/whitespace colour strings to null and trims nonblank ones (`src/CcDirector.Core/Sessions/Session.cs:463`).
2. `RailColor` returns `unstamped` for a null stamp when connected and settled (`SessionViewModel.cs:260`, `:266`).
3. `StatusPalette.BrushFor` has an explicit magenta arm for `unstamped` (`StatusPalette.cs:177`). Its known-name arms (`:163`) precede the neutral catch-all (`:180`). `HexFor` agrees (`:185`).

Direct calls `BrushFor(null)` and `BrushFor("")` return neutral, as the tests deliberately assert (`StatusPaletteTests.cs:100`), but a real missing rail stamp does not reach that API as null/empty. Treating a palette-only call as an end-to-end counterexample would be a false alarm.

A wire name literally equal to `unstamped` would paint magenta despite being nonempty, because the internal sentinel and wire names share a string namespace. The current Gateway fold never emits that name; this reserved-name collision is not counted as an existing product defect or a new regression.

The tests separately reach unknown-name neutral through a real view model (`SessionRailStateTests.cs:123`), reach missing-stamp magenta through the palette (`:146`), and assert the connected/settled decision in `OfflineFloorRailColorTests.Online_NoStamp_Settled_IsTheMagentaUnstampedSentinel` (`:54`). That is composed coverage, not an end-to-end live-tunnel proof.

I compared the text of `RailColor` and the `StatusColorBrush` getter to `origin/main`; both were identical. Pixel consequences, accounting for the changed palette:

| Existing path | Name | Before | After |
|---|---|---|---|
| Offline, Working/Starting | blue | #3B82F6 | #3B82F6 |
| Offline, idle and not held | red | #EF4444 | #EF4444 |
| Offline, held, no stamp | grey | #6B7280 | #6B7280 |
| Offline, held with ordinary grey stamp | grey | #6B7280 | #6B7280 |
| Connected, no stamp, not settled | unknown | #6B7280 | #6B7280 |
| Connected, no stamp, settled | unstamped | #FF00FF | #FF00FF |
| Offline, held with unrecognized frozen stamp | unrecognized name | #FF00FF | #E5E7EB |

The last row matters: the floor's held-session branch preserves any non-null stamp (`SessionViewModel.cs:254`), not just grey. Thus saying the offline floor's pixel is *universally* unchanged would be false, although its ordinary blue/red/grey outputs and its decision logic are unchanged. The not-yet-settled placeholder remains the original palette grey, not the new neutral.

## Logging, guards and the wire-read failure cases

No existing rail log was removed. `StatusColorBrush` still edge-logs unknown names and missing stamps (`SessionViewModel.cs:338`); the unknown log now describes neutral (`StatusPalette.cs:221`) and missing-stamp logging remains separate (`:235`). The intentional unknown-name alarm-to-neutral change is the ruling, not a lost guard.

`AgreementCheck.cs:396` now emits `palette-missing` and continues before comparing sentinel pixels. This does skip the later client-table comparison for an unknown canonical name, but it still produces a finding rather than certifying agreement. Known-name comparisons remain. Its new test checks a single *matching* palette-missing finding, not that the whole findings list has exactly one element (`AgreementCheckFaultInjectionTests.cs:213`); the note's “answered once” must be read as the palette portion, not a guarantee of one finding for the entire session.

The new cache catches exceptions (`SessionColourLegendCache.cs:150`), retains the previous answer, stores the message and logs it. It does not rethrow. Failure does not raise `Changed`; success does (`:148`). This is new failure handling, not removal of a previously guarded read.

| Wire/read situation | Code path and visible result |
|---|---|
| HTTP failure / network exception | `GatewayClient.cs:847` throws; cache records/logs it. With no previous legend, the dialog's `Fail` shows the error and clears rows (`ColourLegendDialog.axaml.cs:70`, `:165`). |
| No configured Gateway | Client returns null (`GatewayClient.cs:843`); cache logs and returns (`SessionColourLegendCache.cs:135`). The window says there is no Gateway, unless there is an older cached answer. |
| JSON `null`, `{}`, or empty entries | `GatewayClient.cs:851` rejects null/zero entries. First-load dialog shows failure, not an apparently successful empty legend. |
| Empty HTTP body / syntactically invalid JSON / incompatible property types | JSON deserialization throws at `GatewayClient.cs:850`; same cache/dialog failure route. |
| Structurally valid but malformed entry | Only list count is validated. Bad hex reaches `Color.Parse` (`ColourLegendDialog.axaml.cs:119`) and is caught by `LoadAsync`; rows are cleared and an error shown. Null entries or null titles can instead throw in `SessionDotHover.TitleFor` (`SessionDotHover.cs:75`). Valid hex with missing title/meaning is accepted and can show an unexplained swatch. Thus malformed-body handling is not comprehensive semantic validation. These outcomes are source analysis, not wire-fault executions. |
| Slow read, no cache | `Loading...` is present from XAML (`ColourLegendDialog.axaml:29`); `LoadAsync` awaits the network without blocking the UI thread. Shared HTTP timeout is ten seconds (`GatewayClient.cs:109`). Opening a cold dialog can initiate an additional read: `Current` starts `BeginRefresh`, then `LoadAsync` calls `RefreshAsync` directly, bypassing the in-flight guard. |
| Failed/slow refresh with previous cache | Existing words are immediately rendered. The dialog neither subscribes to `Changed` nor checks `Error` when a cached answer exists. Staleness/failure is not distinguished on that open window; this is the last-known-answer behavior described above. |

For ordinary first-load failure/absence, **yes**, a missing legend is distinguishable on screen from successful content. A supposedly successful zero-entry response is rejected. For semantically incomplete entries or a stale cached answer, that stronger guarantee does not hold. No mounted live-Gateway failure-flow test was run.

## Colour decisions and the mission rulings

Nothing in this diff changes the Gateway fold, emitted colour names/labels, `RailColor`, or stamp application. The changed switches choose pixels for names; the hover chooses words to display; the agreement check diagnoses pixels. I found no forbidden change to how a session's colour is decided.

The `LastStatusReason` hover and local snooze/dictation sentences were removed from the view model; the actual dot binding now uses `ColourHover` (`MainWindow.axaml:546`). Other local state prose remains and is explicitly inventoried for later phases in the rail sweep. I do not misreport those pre-existing, expressly deferred items as new Phase A defects.

The legend omits `Broken`, while displaying entries and `VerdictNote` (`ColourLegendDialog.axaml.cs:95`, `:98`). This is not literally the complete response rendered verbatim. The note explicitly discloses that omission and asks for a later Gateway ruling; I do not duplicate that disclosed design gap as a newly discovered implementation finding. Likewise, new-name swatches in the legend use the wire hex, while a session dot for that name uses neutral: the comment at `ColourLegendDialog.axaml.cs:117` claiming the two pixels match “by construction” is false in the mission's very version-gap case. The owner's prohibition is on guessing the session dot; I found no new wire hex added to that seam.

## Re-ran two claimed failure demonstrations

I chose the Gateway read and the hover because their claimed proof crosses boundaries that can be replaced by a convenient local value. These mutations were confined to this worktree and restored from byte-for-byte backups.

1. **Replaced the legend-read body with `await Task.CompletedTask; return SessionColourLegend.Build();`.** Ran `dotnet test src/CcDirector.Avalonia.Tests --filter FullyQualifiedName~SessionColourLegendReadTests --verbosity normal`. Result: **4 passed, 3 failed, 7 total**. The route test printed `Expected: GET / Actual: null`; the refused-read test reported that no exception was thrown. Those are the note's reported symptoms, not a build failure. A third failure correctly caught the unconfigured client returning a legend instead of null; my mutation replaced the entire body, including its config guard, so it is broader than the note's two-failure mutation. The four cache controls passed.
2. **Replaced `ColourHover` with the prior local expression:** `Session.OnHold ? "Snoozed (set aside by you)" : Session.IsReceivingDictation ? "Receiving a dictation from your phone" : Session.LastStatusReason ?? ""`. Ran `dotnet test src/CcDirector.Avalonia.Tests --filter FullyQualifiedName~SessionRailStateTests --verbosity normal`. Result: **20 passed, 3 failed, 23 total**. The disagreement test printed `Expected: "Monitor fix round 2 progress" / Actual: "needs you"`; the other two failures expected `"Snoozed"` and `""`, respectively, and received `"needs you"`. This reproduces the exact claimed defect; unaffected rail controls passed.

The separate, subtler constant substitution described above then passed all 100 selected tests. Watching the first broad mutation fail therefore does not prove the response words are actually protected.

## Executed checks and their limits

The SDK was installed at `/Users/soren/.dotnet/dotnet`, but was not on this shell's PATH; the first bare `dotnet` invocation exited 127 and ran no tests. Every reported test result used that absolute SDK executable.

Targeted selection (before mutations and again for the surviving-constant experiment):

```sh
/Users/soren/.dotnet/dotnet test src/CcDirector.Avalonia.Tests --filter 'FullyQualifiedName~StatusPaletteTests|FullyQualifiedName~SessionRailStateTests|FullyQualifiedName~SessionDotHoverTests|FullyQualifiedName~SessionColourLegendReadTests|FullyQualifiedName~ColourLegendDialogTests|FullyQualifiedName~AgreementCheckFaultInjectionTests|FullyQualifiedName~OfflineFloorRailColorTests' --verbosity quiet
```

Baseline: **100 passed, 0 failed, 0 skipped**. Constant mutation: **100 passed, 0 failed, 0 skipped**. Temporary adversarial probes: **2 executed, 2 failed**, with the layout widths and contradictory string reported above. Probe source was removed after the experiment; no tests or production files are included in this review commit.

The layout probe called the actual private `Render` via reflection, mounted the actual dialog, and asserted all ten explanation widths fit `ColourRows.Bounds.Width - 24`. The hover probe composed the actual floor and formatter with `true, "grey", Working, true, false` and `"Snoozed"`; its observed output is recorded above. Both are reproducible without a configured Gateway.

Full restored Avalonia suite result is recorded below after completion. I did not run the solution-level gate, the Windows-only projects, other default-gate suites, Gateway suites, a live fleet agreement check, a production Director, a browser or screenshots. The known Mac `NETSDK1100` solution limitation is not a finding. The new dialog was mounted only under the headless test application, not under the production app with a running `ControlApiHost`. Consequently this inspection does not prove the real connection subscription, live cache refresh, or native rendered appearance.
