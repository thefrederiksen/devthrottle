# Phase 4 live proof plan (the Manager's own run)

Written before the run so the claims are fixed in advance and cannot be shaped to fit what happened.

The rig is the phase 3 one - `packages/client-core/browser-tests/dev-report-viewer-proof/rig.ps1` - which already
builds and starts an isolated Gateway, launcher and a slot 5 Director in their own root
(`%LOCALAPPDATA%\dev-report-proof-rig`), with its own port and its own named signals. Nothing outside that root is
started, stopped or touched, and no other Director is ever signalled.

The Director is a desktop application, so it is driven by Windows user interface automation
(`scripts\ui-drive.ps1`, and `tools\cc-click` built from this tree if named controls are not enough), and every
claim is recorded with a screenshot and a line in `evidence/director-<date>.json`.

## Claims

**The pane**

- D1 - Opening the pane on a session shows "Loading..." at once and then that session's reports.
- D2 - A session with no reports says so; it does not sit blank.
- D3 - The report renders in the pane: the fixture report's table and its question are visible.

**The owner's answer reaches the agent**

- D4 - A note on a table cell is queued, names the row and the column, and is listed as queued.
- D5 - An answer to the question is queued with the chosen option.
- D6 - Send moves both to the Gateway, which states where each one is, in the Gateway's own words.
- D7 - The prompt the session received, read back from the RIG GATEWAY (not from the screen), names the cell by
  row and column and carries the chosen option.
- D8 - The agent's reply, published with `cc-dev-reports reply`, appears in the pane.

**The report cannot reach the Director**

- D9 - The hostile report that passes the shape check (`fixtures/hostile-published.html`) reaches no other origin:
  the recording server records nothing from the pane, and nothing it draws can send as the owner.
- D10 - The scripted report the shape check refuses (`fixtures/hostile-direct.html`), handed to the pane anyway,
  runs no script: its beacons never arrive and its forged send never reaches the Gateway.
- D11 - The self-navigating report (`fixtures/hostile-refresh.html`) takes its frame to another origin and the
  pane's connection to it ends; the Director's own page never navigates away.
- D12 - The Gateway key the Director hands the page appears in no log line, no screenshot and no file the run
  writes. Checked by searching the rig logs and the evidence directory for the key.

**What this proof does NOT cover, stated before it runs**

- A hosted Gateway. The rig Gateway is local.
- Any Director but the rig's own slot 5 build.
- Anything about how the pane behaves when the Gateway is unreachable mid-read, unless the run happens to hit it.

## Failing on purpose

Every claim that a guard protects (D9 to D12) is run once more with that guard removed in the served build, and
must go red with the symptom it names. A guard whose removal leaves the claim green is not proving anything, and
that is reported as a hole rather than repaired quietly.
