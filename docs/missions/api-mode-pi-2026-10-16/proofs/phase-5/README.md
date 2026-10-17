# Phase 5 proof: choosing API mode

Mission #4070, phase 5: the New Session **Connection: Terminal | API mode** row on the Cockpit, the phone and the
desktop Director, the roster's **API mode** tag, and `--connection` on `session spawn` and `session list`.

**The choice is hidden on every surface until phase 4 (take the wheel) lands.** The one switch is
`ConnectionModes.ApiModeOffered` in `src/CcDirector.Gateway.Contracts/SessionDto.cs`, `false` today. Turning API mode
on is that one line. These pictures were taken with the switch's ON position shown without editing it: the web screens
against a stub that answers the agent list the way the Gateway does when it is on, and the desktop dialog handed the
ON position by its test.

## What each file shows

| File | Surface | What it proves |
|---|---|---|
| `cockpit-new-session-pi-terminal-default.png` | Cockpit | Pi is offered Terminal and API mode; Terminal is chosen when the dialog opens. |
| `cockpit-new-session-pi-api-mode.png`, `.aria.txt` | Cockpit | API mode chosen: the button is pressed, the Gateway's sentence saying what API mode means is under the row, and the summary line says "... . API mode". |
| `cockpit-new-session-claude-code-no-row.png` | Cockpit | An agent offered the terminal alone (Claude Code) has no Connection row. |
| `cockpit-roster-api-mode-tag.png`, `.aria.txt` | Cockpit | The roster card of an API-mode session carries the Gateway's "API mode" tag; the terminal session beside it carries none. |
| `phone-new-session-pi-api-mode.png`, `.aria.txt` | Phone | The same shared row in the phone's review step, API mode chosen, with the same sentence under it. |
| `phone-roster-api-mode-tag.png`, `.aria.txt` | Phone | The "API mode" chip beside the agent chip on the API-mode session only. |
| `desktop-new-session-pi-api-mode.png` | Desktop Director | `NewSessionDialog.axaml` with Pi chosen: "Connection: Terminal / API mode", API mode checked, and the same sentence under the row. Taken by `NewSessionConnectionTests.Dialog_PiInApiMode_IsPhotographedForTheProof` (headless, real drawing). |
| `command-line.txt` | Command line | An unknown `--connection` value exits 2 on spawn and on list; `session list --connection api` narrows the plain and the `--json` output (count 0 of 45 on the live fleet today, as nothing can start in API mode yet); `--fields ...,connection` shows the field. |
| `shoot-web.mjs` | - | The script that took the web pictures and dumps, with its stub Gateway. |

## How they were made

- **Web:** the Cockpit and phone dev servers (`vite`) from this branch, driven by headless Chrome through
  `shoot-web.mjs`. Every Gateway call is answered by the script's stub; nothing reached a real Gateway. The script also
  checks two things on screen and printed `PASS` for both: Claude Code shows no Connection row, and API mode is pressed
  on the phone after it is clicked.
- **Desktop:** `dotnet test src/CcDirector.Avalonia.Tests --filter NewSessionConnectionTests` with
  `CC_API_MODE_PHASE5_SHOOT` set to a folder; the test checks the picture changed when API mode was chosen, so the
  camera is live.
- **Command line:** this branch's `cc-devthrottle`, run against the live Gateway for the read-only list commands. No
  session was spawned.

## What this does not show

- A session actually started in API mode from these screens: the switch is off, and starting one is phase 6's proof.
- The real Gateway serving the switch's ON position: that is covered by the Gateway unit tests
  (`ConnectionChoiceTests`), not by these pictures.
