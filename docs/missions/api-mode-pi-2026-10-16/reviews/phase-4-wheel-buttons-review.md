# Review: phase 4, the Terminal tab's wheel button on the desktop, the Cockpit and the phone (pull request #4186)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Reviewed on 17 October 2026 against
commit 2d606339a, the head of pull request #4186 (branch `apimode/p4-wheel-buttons`), in a detached
worktree of that commit at `../devthrottle-review-4186`. The branch merges pull request #4184's branch
(`api-mode-wheel-action-live`), so the diff I reviewed is this branch against the merge point with that
branch (`git diff origin/api-mode-wheel-action-live...HEAD`: 28 files, 1,810 lines added, 6 removed).
Pull request #4184's own files were read for the contract only and are not judged here. origin/main
was at 30556737f and is the merge point with this branch, so nothing has moved under it. Reviewer seat,
opened by the Delivery Lead, running a different agent family from the Developer.

**Verdict: the phase holds. One finding, about merge order rather than the code, and it is the
Developer's and the Delivery Lead's to decide. Within the scope below, the button on all three
surfaces renders the Gateway's four fields and nothing else, a press sends exactly the verb given,
every refusal is shown in the Gateway's own words, the desktop and the web shells show the same
thing, the screen responds before any wait, and the tests are proofs rather than self-testimony.**

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:**

- The contract this change renders: `ApiModeWheelAction` and `ApiModeWheelVerbs` in
  `src/CcDirector.Gateway.Contracts/ApiModeDisplayDtos.cs`, and pull request #4184's addition of
  `SetDisplayStateRequest.ApiMode`, with the Gateway side that stamps it: `ApiModeRowStamp.Stamp`,
  `DisplayFold`, `FleetDisplayStateObserver.BuildRequest` and its change gate, and the hub's push after
  an event batch.
- The Gateway's wheel routes and their refusals: `RunApiModeVerbAsync` in
  `GatewayEndpoints.ApiModeVerbs.cs` from top to bottom, `WheelRefusal` (the body is `{ error, code }`),
  `MapDirectorFailure` for a refusal the Director returns, and `TunnelFailure` for a Director that is
  not connected.
- The shared web control: `packages/client-core/src/sessions/wheelAction.ts`,
  `TerminalWheelButton.tsx`, `terminalWheelButton.css`, the two new verbs in `api/client.ts`, and the
  path a refusal takes through `GatewayError.from`, `readFailureDetail`, `failureDetailFromText` and
  `stringFromErrorBody` so that `serverReason` is the Gateway's `error` string.
- The two web shells: the Cockpit's `TerminalPane.tsx`, `SessionDetail.tsx` and `SessionsView.tsx`;
  the phone's `Terminal.tsx` and `useSessionManage.ts`, including the existing `refresh` and the
  session row guard it already had. The styling each bar gives the control (`.term-bar` in the
  Cockpit, `.term-statusbar` on the phone).
- The desktop: `TerminalWheelPresenter.cs` whole, every change to `MainWindow.axaml` and
  `MainWindow.axaml.cs` (the styles, the two controls, `UpdateWheelBar`, the click handler, and the
  four places `Attach` is called), and the tab-switch method that calls `UpdateWheelBar`.
- The Director: `Session.GatewayApiMode` and `ApplyGatewayApiMode`, `FleetDisplayStateExecutor`,
  the echo in `ControlEndpoints.Map`, `IGatewayWheel`, the two new calls on `GatewayClient` and the
  existing `RelayFailureAsync` they throw through (it reads the body's `error` string).
- Every test the pull request adds or changes, line by line: `TerminalWheelPresenterTests.cs` (22),
  `GatewayClientWheelTests.cs` (4), the new case in `SessionCommandExecutorTests.cs`,
  `wheelAction.test.ts` (12), `TerminalWheelButton.test.tsx` (16), `wheel.test.ts` (3), the Cockpit's
  `terminalWheelMount.test.tsx` (3), the phone's `terminalWheelMount.test.tsx` (2) and the one-line
  change to `stopSessionSheet.test.tsx`.
- Beyond the diff, on the same commit: who else subscribes to `Session.OnGatewayDisplayStateChanged`
  (only `SessionViewModel`, which posts a fold re-projection); every caller of
  `StampFleetRolesAndFold`, to see whether any client-facing read could serve the Director's echoed
  verdict without re-folding it (none can: `ApiModeRowStamp.Stamp` sets every row's verdict to null
  before it folds, on every call); every reader of a pushed row's `ApiMode` in the Gateway outside
  the stamp (there is none, so the echo is never an input to a ruling); every mount of
  `TerminalPane` in the Cockpit (one); and whether a Director-side executor for `wheel-take` or
  `wheel-hand-back` exists on this branch or on origin/main (it does not; see finding 1).
- Pull requests #4184 (open; moved by one commit since this branch merged it, and that commit adds
  only a review document, so the contract this branch reads is unchanged) and #4094 (open).

**Ran, on devlinux, one project at a time, filtered, after checking that no other test run was going:**

| Run | Result |
|---|---|
| client-core vitest: `wheelAction.test.ts`, `TerminalWheelButton.test.tsx`, `wheel.test.ts` | 31 passed, 0 failed |
| Cockpit vitest: `terminalWheelMount.test.tsx` | 3 passed, 0 failed |
| Phone vitest: `terminalWheelMount.test.tsx`, `stopSessionSheet.test.tsx` | 12 passed, 0 failed |
| `npm run typecheck` across every web workspace (client-core, cc-assistant, Cockpit, phone) | clean |
| `CcDirector.Avalonia.Tests`, filtered to `TerminalWheelPresenterTests` | 22 passed, 0 failed |
| `CcDirector.Gateway.UnitTests`, filtered to `GatewayClientWheelTests`, `SessionCommandExecutorTests`, `ApiModeWheelOnTheDisplayPushTests`, `FleetDisplayStateObserverTests` | 91 passed, 0 failed |

These are the same counts the pull request reports, confirmed by my own runs rather than read from
the description. The web dependencies were installed fresh in the review worktree (the shared
checkout has none), with the Node 22.20.0 at `~/.local/share/node/bin` and the .NET 10.0.301
toolkit at `~/.dotnet`.

**Could not reach:**

- The local gate script is Windows PowerShell and was not run here; the pull request says the same.
  The default gate does not run the Gateway unit tests or any web test, so the runs above are the
  coverage for this change, not a substitute for the gate.
- No press was made against a real Gateway and a real Director, because the Director side of the
  verbs does not exist on this branch (finding 1). Every proof of "a press sends the verb and shows
  the refusal" is against a fake seam or a stub transport, which is what the brief asked for; what it
  does not cover is the round trip through a Director.
- Neither web shell nor the desktop was rendered. The layout notes in section 3 are read from the
  stylesheets and the markup, not seen.

## 2. The six checks the brief asked for

**(1) Rule 7: no view or presenter decides what a state means.** Holds on every surface.

- Nothing in `wheelAction.ts`, `TerminalWheelButton.tsx`, `TerminalWheelPresenter.cs` or the
  window's `UpdateWheelBar` reads the wheel holder or the connection mode. The two source tests
  (`wheelAction.test.ts` and `WheelButtonSource_ReadsNeitherWheelHolderNorConnectionMode`) pin this
  on the files themselves with comments stripped first, and each guards against an empty read with a
  length check, so an absence cannot pass for a clean run.
- `readWheelAction` and the presenter's `Problem` refuse a verb they do not know or a label with no
  words. That is the right side of rule 7: a client that cannot read the verdict shows the problem and
  no button, and never guesses one.
- The one branch on the verb that remains is which of two styles the button is drawn in (accent blue
  for taking the wheel, grey for handing back), in both the desktop and the web control. That is a
  style lookup keyed on a value the Gateway chose, the same as the `Kind` and `Tone` lookups the
  contract already allows, and it decides nothing about state. I do not count it as a breach.
- `readWheelAction` and the presenter both drop a disabled reason the Gateway sent on an enabled
  button. The contract says the reason is null whenever the button is enabled, so this normalises a
  contract violation rather than ruling on one, and both surfaces do it the same way.

**(2) No button without an action; disabled with the reason; the label as given.** Holds.

- A null verdict, a verdict with no `wheelAction`, or no session row renders nothing on the web
  (three tests) and shows nothing on the desktop (`Attach_NoVerdict_ShowsNoButton`,
  `Attach_NoSession_ShowsNoButton`).
- `enabled: false` disables the control and shows `disabledReason` beside it. On the web the reason
  is also the button's `aria-describedby` target and its tooltip; on the desktop it is the note line
  and the tooltip. A click on a disabled button sends nothing (tested on both).
- The label is rendered as sent (`Attach_LabelTheGatewayChose_IsShownAsGiven` with words the
  Gateway does not use today; the web test reads the button by its accessible name).

**(3) A click sends exactly the given verb, and a refusal is shown.** Holds.

- The web maps `takeTheWheel` to `POST /sessions/{sid}/wheel/take` and `handBack` to
  `POST /sessions/{sid}/wheel/hand-back` through one table; `wheel.test.ts` pins the method and the
  path, with the session id encoded. The desktop maps the same two verbs to `IGatewayWheel`, and
  `GatewayClientWheelTests` pins the real client's method and path through a stub handler.
- A refusal body of `{ error, code }` arrives as `GatewayError.serverReason` on the web and as the
  exception message on the desktop, and both show that sentence verbatim beside the button (tests on
  both, with the Gateway's real "already held" sentence). A failure with no sentence gets the shared
  wording naming the action, never a bare status number.
- A second press while one is pending sends nothing (tested on both). A reply that lands after the
  screen moved to another session is dropped rather than written onto the new session (tested on
  both).
- The desktop goes through the Gateway's route, never an in-process switch, so the Gateway's ruling
  and the Director's contract to report the new holder before answering apply to it as they do to
  the browser.

**(4) Drift between the desktop and the web shells.** None in content.

Both show: nothing, or one button with the Gateway's label, enabled or disabled with the reason
beside it, a refusal's sentence after a failed press, and the "cannot read this verdict" sentence
with no button. Both clear a refusal when the verdict changes or the session changes. The only
differences are layout, which rule 8's reasoning permits: the desktop appends "..." to the label while
pending where the web shows a spinner; the desktop trims a long note to 360 pixels with a tooltip
where the web wraps it (section 3, note 2). The web asks its shell to re-read the roster at once after
an accepted press; the desktop waits for the Gateway's next display-state push, which pull request
#4184 makes event-driven and which the Director's contract makes immediate. Both end in the same
verdict.

**(5) The screen responds within 100 milliseconds and the screen thread never blocks.** Holds.

- Web: `press` sets the busy state synchronously before awaiting the call;
  `TerminalWheelButton_Press_GoesBusyAtOnceAndCallsTake` asserts busy in the render after the click
  with the reply still unresolved.
- Desktop: `PressAsync` sets `IsPending` and raises `Changed` before its first await, so
  `UpdateWheelBar` redraws in the click's own frame;
  `PressAsync_Take_GoesPendingBeforeTheGatewayAnswersAndCallsTake` asserts this with the reply held.
  The Gateway's stamp arrives on the hub's thread and is posted to the screen thread
  (`GatewayStamp_ArrivesOffTheScreenThread_IsHandledThroughThePost`). Nothing in the window's
  drawing does input or output; the seam is read at press time through a property.
- Director: `ApplyGatewayApiMode` serialises a small object on the executor's thread and raises the
  change event only when the text differs, so a Gateway re-stamping the same verdict does not redraw
  the rail (tested).

**(6) The tests are real proofs.** They are.

Every run uses a fake or a stub: a hand-released `TaskCompletionSource` for the desktop seam, a
stub `HttpMessageHandler` in front of the real `GatewayClient`, mocked module functions and a
mocked `fetch` on the web, and the real `SessionCommandExecutor` for the Director's storage and echo.
No test waits on a clock, a port or a process. The shell mount tests mock the button and prove the
wiring (the right row and the right re-read reach it), which is their whole claim. The source tests
pin rule 7 with a presence check on length so that a missing file cannot pass. I found no test that
asserts less than its name says.

## 3. Findings and observations

### Finding 1 (merge order, not code): without pull request #4094 every press ends in "unknown verb 'wheel-take'"

The Director has no executor for `wheel-take` or `wheel-hand-back` on this branch or on origin/main;
the only mention of those command names is the Gateway's constants. Pull request #4094, "take the
wheel and hand back, the Director side", is still open. So on a build carrying #4184 and #4186 but
not #4094, a press goes: the client posts the verb; the Gateway rules the refusals it can see from
the pushed state (terminal mode, already held, not held) and lets the rest through; it sends the
command to the Director; `SessionCommandExecutor.DispatchAsync` answers `BadRequest` with the text
`unknown verb 'wheel-take'`; `MapDirectorFailure` returns that as a 400 with `{ error }`; and all
three surfaces show, verbatim and correctly, **unknown verb 'wheel-take'** beside the button.

What breaks: a shipped button whose every press fails with a developer's sentence, on the desktop
and in both browsers. Why it must change: either #4094 merges before #4186, or the two are released
together, and the pull request or the mission record should say which. This is the sequencing the
pull request's own description leaves implicit ("the Director side of the wheel commands (#4094) is
not needed here" is true of this code and false of a release). The rendering code needs no change.

### Observations (no harm proven; recorded so the next seat does not re-derive them)

1. **One redraw can read two verdicts.** The presenter's properties read `Session.GatewayApiMode`
   live, and that reference is swapped on the executor's thread while `UpdateWheelBar` reads it
   several times on the screen thread. A stamp landing mid-draw could mix fields from two verdicts for
   one frame; the posted redraw that follows the same stamp repairs it. Sub-second at most, and the
   change gate makes the window tiny. Not a finding.
2. **A long note on the phone.** The phone's status bar is a flex row with no wrap; the control
   wraps its reason or refusal inside itself, so a two-sentence refusal (the Gateway's "already held"
   sentence is about 150 characters) takes three or four lines at phone width and pushes the terminal
   down while it is shown. The Cockpit's bar behaves the same but has the width to spare; the desktop
   trims to 360 pixels with a tooltip. Content is identical; only layout differs. Not seen rendered,
   so not a finding.
3. **The window after an accepted press.** On every surface the old button stays enabled with its old
   label until the next verdict arrives (the web re-reads at once; the desktop waits for the push). A
   second press in that window is answered by the Gateway's "already held" or "not held" sentence,
   which is shown. Correct by rule 7, and short.
4. **The web's refusal-clearing key omits the disabled reason** while the desktop's includes it. A
   refusal can only exist after a press, which needs an enabled button, and any verdict that then
   disables it changes the key on both. No visible difference today.
5. **The verb-to-style mapping lives twice** (the web control and the desktop window). Both reject an
   unknown verb before styling, so they cannot diverge for any verb the Gateway sends today.

## 4. What this review does not cover

A real round trip through a Director, the local gate on Windows, and the rendered layouts. The
Director side is pull request #4094's to prove, and its reviewer should check the contract this
code relies on: that the Director awaits the delta push with the new holder before it answers the
wheel command, since both the desktop's and the web's "next verdict" depend on it.
