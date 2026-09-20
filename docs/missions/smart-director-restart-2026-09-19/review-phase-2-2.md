# Review - Smart Director Restart - phase 2, task 2: the shutdown progress screen

Reviewer seat for the code at commit `e3e66e110` (branch `smart-restart-p2-progress`), opened by the
phase 2 Tech Lead. This file is the review; the seat that built the work decides what happens to each
finding.

## Scope

What I read:

- The whole diff `origin/main...HEAD`: the four new screen files under `src/CcDirector.Avalonia/SmartRestart/`
  (`ShutdownProgressSource.cs`, `ShutdownProgressViewModel.cs`, `ShutdownProgressView.axaml`,
  `ShutdownProgressView.axaml.cs`), the three test files under
  `src/CcDirector.Avalonia.Tests/SmartRestart/`, the change to `HeadlessTestApp.cs`, the added
  `Avalonia.Skia` package reference, and the proof and pictures under `attachments/phase-2/`.
- The mission document, sections 4.4, 4.5, 5.3 items 4 to 8, section 7 (the check) and 5.5.
- The Developer mandate, the Developer's own proof (`attachments/phase-2/progress-screen-proof.md`).
- The settled phase 1 interface (`phase-1-interface.md`, read from `origin/main`).
- `docs/CodingStyle.md` sections 2 and 4; `docs/VisualStyle.md` section 13.
- `.github/workflows/ci.yml` and `release.yml`, to judge where the headless test change can bite.

What I ran (all in the foreground, nothing in the background, no sub-agents):

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  14 passed, 0 failed. Matches the Tech Lead's run.
- The whole `CcDirector.Avalonia.Tests` project at this commit: 568 passed, 0 failed, 1 minute
  22 seconds. Matches the Developer's claim.
- The same whole project at the current `origin/main` head (`912340ed8`, which has moved past the
  branch point and carries other work) in a separate throwaway worktree: 599 passed, 0 failed,
  1 minute 17 seconds. So the real-Skia change costs roughly five seconds on this suite and breaks
  nothing; both runs fit the two-minute local gate budget for this project.

What I could not reach:

- The eight committed pictures. The model running this seat cannot view images in this session, so
  the pictures were checked only by the test's distinct-colour presence check (a blank picture fails
  it) and by the Developer's own statement that he looked at each. That a picture is not blank is
  proved; that it looks right is not, by me.
- The two revert proofs in the Developer's proof file. Re-running them needs a tracked file changed,
  which this mandate forbids. I verified by reading instead: the view defines no `InitializeComponent`
  of its own, and the open test asserts every named control is not null, which is exactly what a
  hand-written loader would break.
- The real engine. Phase 1 has not landed on `origin/main` (no `ISmartShutdownRun` exists in
  `src/CcDirector.ControlApi` yet), so everything about the adapter is read against the settled
  phase 1 interface document, not against running code.
- The one-second timer firing by itself. No test sleeps; the tests call the same method the timer
  calls. Stated by the Developer, consistent with the code.

## Findings

### 1. The view model never lets go of the source, so the screen leaks for as long as the run lives

`ShutdownProgressViewModel.cs` line 50 subscribes: `_source.Changed += OnSourceChanged;`. Nothing in
the screen ever unsubscribes. There is no `Changed -=`, no `Dispose`, and no detach method anywhere in
`src/CcDirector.Avalonia/SmartRestart/` (checked by search across the folder). The view's
`OnDetachedFromVisualTree` (`ShutdownProgressView.axaml.cs` lines 43 to 46) stops the clock timer and
nothing else - the timer is handled correctly, the subscription is not.

Who is harmed and how: the integration task puts this view in place of the session view and later
swaps it away - after a cancel, for one. The phase 1 contract says the engine raises `Changed` on
every state change and at least once per ten-second poll, and it hands out the run through
`ControlApiHost.CreateSmartShutdown()`. From the moment the screen is detached, the run's event keeps
a delegate to the view model, so:

- The view model, its rows and their brushes can never be collected while the run lives.
- Every engine change keeps calling `Dispatcher.UIThread.Post(Refresh)` in
  `ShutdownProgressViewModel.cs` lines 183 to 186 - a dead screen keeps doing snapshot reads and row
  reconciliation on the interface thread of a Director the owner is actively working in. After a
  cancel the owner keeps that Director open, possibly for days, and nothing in this code ever ends
  that work.

This is not latent-only: the seam to fix it belongs to these files. When the integrator detaches the
view, the view model must be told to let go - an unsubscribe called from
`OnDetachedFromVisualTree` - and nothing outside `ShutdownProgressViewModel.cs` can add it later
without editing this screen again. The mandate names this exact harm (a subscription carried for
days after a cancel), and the code proves it: there is no code path that releases it.

### 2. `IShutdownProgressSource` cannot carry what the settled phase 1 contract requires, and the screen takes over what the engine is meant to own

The adapter task will map `ISmartShutdownRun` and its `SmartShutdownSnapshot` onto
`IShutdownProgressSource` (`ShutdownProgressSource.cs`, lines 57 to 81). Concretely, what breaks or
is wrong on screen:

a. **The buttons cannot honour `CanShutDownNow` and `CanCancel`.** The phase 1 interface settles
   that each request is honoured only while the snapshot says it may be; otherwise the engine
   ignores it and logs. `IShutdownProgressSource` has no such booleans, and the screen enables both
   buttons from its own state alone: `AreButtonsEnabled = !IsFinished && _request == OwnerRequest.None`
   (`ShutdownProgressViewModel.cs` line 205, bound in `ShutdownProgressView.axaml` lines 114 and 124).
   The concrete harm: at the limit the engine is in phase `EndingAtLimit` - a cancel there is
   meaningless and would be ignored - yet the screen still shows "Cancel and keep working" live. The
   owner clicks it; the engine ignores and logs; the screen sets its own request state, says
   "Cancelling - bringing your sessions back..." and disables both buttons
   (`ShutdownProgressViewModel.cs` lines 152 to 171, 210 to 216). The owner is told something is
   happening that is not, and watches a Director empty and close while believing his sessions are
   coming back. A dumb screen cannot know this without the engine's two booleans, and this interface
   has nowhere to put them.

b. **The screen words the states and the count itself, which the settled interface gives to the
   engine.** `ShutdownProgressRowViewModel.Describe` (`ShutdownProgressViewModel.cs` line 316) words
   all seven states, and the count is recomputed on the screen from its own rule
   (`IsGone`, line 115, used at line 195). The phase 1 interface settles the opposite: the engine owns
   `StateLabel`, `PhaseLabel` and `CountLabel`, shown as is, precisely so that "a new state is one edit
   in the engine and no new branch in a window". An adapter must force every engine state into this
   fixed seven-word enum and discard the engine's own words, and the count is now computed by two
   rulers - the engine's `Gone`/`Total` and the screen's `IsGone` - that nothing keeps agreed.

c. **Three engine states have no representation here: `Pending`, `KeptRunning`, `BroughtBack`.** A
   session under a lead is asked BY its lead, so it sits `Pending` - in the record, not asked - for
   as long as the lead takes; the screen can only draw it "asked", which is not true. After a cancel
   the engine's terminal states are `KeptRunning` and `BroughtBack`, and the gone count goes DOWN as
   sessions come back; the adapter must map those into one of seven words, all of which are lies, and
   the count reads "0 of 9 shut down" again - as though the shutdown restarted from zero - while the
   status says it is cancelling. The screen cannot show what actually happened after the button the
   mission exists to protect.

d. **The engine's phase, note, and completion have no home.** `PhaseLabel` and `Note` ("the most
   recent thing worth saying") cannot be shown: `StatusText` is the screen's own four fixed sentences
   (`DescribeStatus`, lines 219 to 233). `Completion` and `SmartShutdownOutcome` cannot arrive at all.
   The concrete harm: on a `Failed` outcome the engine stops with sessions still open - the screen
   sits on its default sentence or on "Shutting down now..." forever, both buttons live or disabled
   by its own guess, and the owner is told nothing. The screen's finished rule (line 198) is a proxy
   (all sessions gone and no cancel sent); it cannot see the run ending any other way, so it can
   neither say finished nor say why it will never finish.

e. **The ignore-all kind has no engine source to adapt.** The phase 1 interface gives the smart
   shutdown a run with snapshots, but the ignore-all path is
   `Task<IgnoreAllResult> ShutDownIgnoringAllAsync(...)` - a result returned after the fact, no run,
   no `Changed`, no per-session states. `ShutdownProgressKind.IgnoreAll`
   (`ShutdownProgressSource.cs` lines 7 to 40, used throughout the view model) cannot be fed by any
   adapter against the interface as settled, so either phase 1 must add an ignore-all run or this
   half of the screen is dead code - and the mission (section 4.4) says the progress screen comes up
   in both cases. Within ignore-all the screen's only word for a session still open is "asked", which
   nobody was.

This is one finding, not five preferences: the adapter task cannot be written without changing this
screen, and each item above names a screen that shows something untrue or shows nothing when the
owner needs to know. The screen is well built against the interface it invented; the interface is
too far from the one that was settled. The mandate asked for exactly this list.

## Points I checked and cleared

- **"No reason was given." against the no-fallback rule.** The phase 1 contract says `Detail`
  carries the reason for an undeliverable request; an empty one is an engine defect. The screen does
  not fabricate a reason - it draws words that are true, and it logs the anomaly with the session id
  (`ShutdownProgressRowViewModel.DescribeReason`). Throwing on the interface thread in the middle of
  a shutdown would be worse. Not a fallback that hides a problem: not a finding.
- **The thread proof.** The background-thread test records, for every collection change and every
  count change, which thread made it, asserts there was at least one of each, and asserts all were on
  the interface thread - a presence check, not an absence. Row-internal property changes are not
  directly asserted but happen inside the same `Refresh` call the test does prove. Good enough as it
  stands.
- **The count and the finished rule within this interface.** "Shut down" and "ended at the limit"
  count as gone; after a cancel the screen never claims finished (deliberately tested); no case found
  where all-gone is claimed too early under this interface. The cancel-path wrongness is finding 2c.
- **The clock.** Clamped at zero, rounded up so it opens on the full time; a change re-reads the
  clock; after "Shut down now" the time is hidden rather than shown disagreeing with the limit.
- **Double-click and disabled clicks.** The view model guards on its own request state and the real
  mouse click in the tests does not reach a disabled button. Tested, green on my run.
- **`HeadlessTestApp.cs` and `Avalonia.Skia`.** Whole project green before and after on my own runs;
  about five seconds slower, within the gate budget; the continuous integration job that runs this
  project is Windows only, and the release workflow runs no tests, so no Linux or macOS runner runs
  this suite. Not a finding.
- **The visual style.** The disabled and hover colours in the markup match `docs/VisualStyle.md`
  section 13 exactly, including the cursor change. Not a finding.

Two findings. The worse one is finding 2: the interface this screen invented cannot carry the
engine's button permissions, words, phases or outcomes, so the screen will report requests the engine
ignored as accepted and cannot tell the owner a shutdown failed - the adapter task cannot be written
without rebuilding parts of this screen against the settled contract.

END OF REVIEW
