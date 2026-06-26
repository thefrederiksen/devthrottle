## Problem

Claude Code and several other supported agents now use the terminal alternate screen. In that mode, the agent owns the visible terminal grid and usually owns scrolling inside the grid. The normal terminal scrollback is intentionally empty or hidden.

This breaks an assumption in remote surfaces such as the Cockpit Blazor terminal and the future Android application: a user scroll gesture cannot always mean "scroll the browser terminal history". Sometimes it must mean "send a scroll input event back to the running agent, let the agent redraw, then render the new visible terminal screen".

The desktop Avalonia terminal already has the right model: when the parser is on the alternate screen and mouse reporting is enabled, it hides the local scrollbar and forwards wheel input to the running application.

The remote web terminal needs the same behavior.

## Current behavior

Relevant code:

- `src/CcDirector.Cockpit/Components/TerminalPane.razor`
- `src/CcDirector.Cockpit/wwwroot/js/cockpit-terminal.js`
- `src/CcDirector.ControlApi/TerminalStreamEndpoint.cs`
- `src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs`
- Desktop reference: `src/CcDirector.Terminal.Avalonia/TerminalControl.cs`
- Parser reference: `src/CcDirector.Terminal.Core/AnsiParser.cs`

Today the Cockpit terminal streams raw terminal bytes through a web socket and renders them in `xterm.js`. This is good and should remain the live-terminal path.

However, the browser-side terminal still has local scrollback configured. That is only correct when the running agent is in the normal terminal buffer. When the agent enters the alternate screen, local scrollback is not the right history model.

## Desired model

The user should get one natural scroll gesture, but the implementation should route the gesture according to the current terminal mode.

| Current terminal mode | User scroll action | Expected behavior |
| --- | --- | --- |
| Normal terminal buffer | Scroll wheel or touch scroll | Scroll local browser terminal history. Do not send scroll input to the agent. |
| Alternate screen with mouse reporting | Scroll wheel or touch scroll | Send mouse wheel report bytes to the running agent. The agent redraws the visible terminal. The browser renders the new visible screen from the stream. |
| Alternate screen without mouse reporting | Scroll wheel or touch scroll | Use a safe fallback, such as Page Up and Page Down, only if validated for the agent; otherwise show a small hint that the agent does not expose remote scroll input. |
| Separate history or transcript view | Scroll wheel or touch scroll | Scroll our own transcript, turn summaries, or recorded frame history. Do not send input to the agent. |

## Important design decision

Do not try to make the live terminal also be the complete history view.

The live terminal should mean "current interactive terminal screen".

History should be a separate surface, backed by the best available source:

1. Agent transcript files when available.
2. Cc Director turn summaries and briefings.
3. Existing terminal frame recordings from `TerminalSessionRecorder` as a visual fallback.
4. Cleaned raw terminal buffer only as a last resort, because full screen redraw output is noisy.

## Proposed implementation

### Phase 1: expose terminal mode to remote clients

Add enough state to the stream or a side endpoint so Cockpit knows whether the session is currently in the alternate screen.

Options:

- Send a text control frame on the existing terminal stream, such as:

```json
{ "type": "terminal-mode", "alternateScreen": true, "mouseReporting": true, "mouseSgrCoordinates": true }
```

- Or expose the same data through a session terminal state endpoint.

The existing parser already tracks:

- `AnsiParser.IsAlternateScreen`
- `AnsiParser.MouseReportingEnabled`
- `AnsiParser.MouseSgrCoordinates`

The Session-level parser used for snapshots should expose this state in a thread-safe way.

### Phase 2: update Cockpit terminal scrolling

Update `cockpit-terminal.js` so the terminal has two scroll behaviors:

- Normal buffer: let `xterm.js` use local scrollback.
- Alternate screen with mouse reporting: intercept wheel input and send mouse wheel report bytes back through the same input path used by keys.

The byte encoding should match the desktop implementation in `MouseReportEncoder`.

If practical, share the encoding rules in a small documented JavaScript helper that mirrors the server-side encoder.

### Phase 3: add clear user feedback

When alternate screen is active, show a small unobtrusive status line or badge:

> Live terminal: scrolling is controlled by the agent.

Also provide a separate History or Transcript button so the user can inspect prior output without fighting the live terminal.

### Phase 4: Android plan

The Android application should follow the same conceptual split:

- Live terminal is an advanced current-screen view.
- History is a separate transcript or summary view.
- The main mobile experience should prioritize status, needs-you state, voice controls, and summaries over raw terminal scrollback.

## Acceptance criteria

- Cockpit can tell whether a live session is in the normal buffer or alternate screen.
- In normal buffer mode, browser terminal scrollback still works normally.
- In alternate screen mode with mouse reporting, scrolling in the Cockpit terminal sends scroll input to the agent and causes the agent to redraw.
- In alternate screen mode, Cockpit does not present local browser scrollback as authoritative terminal history.
- The desktop terminal behavior remains unchanged.
- A separate History or Transcript affordance exists or is explicitly designed for a follow-up issue.
- The implementation is tested against at least:
  - Claude Code in alternate screen mode.
  - Pi in normal buffer mode.
  - One normal shell or raw command session.

## Testing notes

Manual test cases:

1. Start a Claude Code session in Cc Director, open it in Cockpit, scroll inside the terminal, and confirm the agent view scrolls or redraws rather than browser history moving independently.
2. Start a Pi session, open it in Cockpit, produce enough output to create scrollback, and confirm browser scrollback works locally.
3. Start a raw shell session, print many lines, and confirm normal local scrollback works.
4. Resize Cockpit and confirm the existing terminal size mirroring still prevents ghost frames.
5. Disconnect and reconnect the stream and confirm the first repaint still heals torn attach state.

Automated test ideas:

- Add parser-state tests for alternate screen and mouse reporting exposure.
- Add JavaScript unit tests for wheel report encoding if the encoder is implemented client-side.
- Add an endpoint or stream contract test proving `terminal-mode` frames are sent when the parser state changes.

## Estimated effort

Small version: two to three days.

- Expose terminal mode state.
- Add basic browser wheel forwarding for alternate screen with mouse reporting.
- Add a small badge or hint.
- Manual verification only.

Robust version: five to eight days.

- Full stream contract for terminal mode changes.
- Client-side scroll routing.
- Fallback handling when mouse reporting is not enabled.
- History or Transcript affordance.
- Unit and integration tests.
- Android design notes.

Full polished version: two weeks or more.

- Shared visual history surface using transcripts, summaries, and recorded frames.
- Mobile-specific terminal gestures.
- Strong reconnection behavior for alternate-screen sessions.
- Version-specific behavior documentation for every supported agent.

## Related documentation

- `docs/SupportedAgentsTerminalModes.md`
- `docs/HowTerminalsWork.md`
