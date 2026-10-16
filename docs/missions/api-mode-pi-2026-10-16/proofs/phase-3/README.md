# Phase 3 proof: the live Chat, mid-turn and in its failure cases

Mission #4070, phase 3. These are the shared live Chat (`packages/client-core/src/apimode/ApiModeLiveChat.tsx`) as
the Cockpit and the phone mount it, each inside its own real stylesheet, in a real Chromium.

**What feeds it is a fake stream, not a Gateway.** The harness (`harness/main.tsx`) hands the component the same fake
network the tests use (`liveStream.testkit.ts`) and writes the scene into it frame by frame, as Server-Sent Events
frames of `ApiModeLiveItem`s. The words are copied from the Gateway's own ruling (`ApiModeLiveRuling`,
`ApiModeSessionFold`), so what is on screen is what the real Gateway would send. The component reads it through the
real stream reader (`liveStream.ts`), its real parser and its real item applier.

| File | What it shows |
|---|---|
| `cockpit-midturn.png`, `cockpit-midturn.aria.yml` | The Cockpit's Chat tab mid-turn: turn 1 finished (PONG), turn 2 running with one tool card done and one still running, the reply streaming in, a prompt queued behind the turn, and a prompt the agent refused with its reason. The status line: API mode, Working - turn 2, the queue, the agent connected. |
| `phone-midturn.png`, `phone-midturn.aria.yml` | The same conversation on the phone (412 by 1005, the owner's phone). |
| `cockpit-failure.png`, `cockpit-failure.aria.yml` | The failure cases: a turn stopped by the user ("Delivered - stopped"), a turn that failed when Pi exited ("Delivered - the turn failed: ..." beside the turn line), a tool card closed as stopped, and the connection line with its exit code. The status line: idle after turn 3, the last turn's failure, the agent disconnected. |
| `phone-failure.png`, `phone-failure.aria.yml` | The same on the phone. |

The `.aria.yml` files are Playwright's accessibility snapshot of the Chat region: the roles and names a screen reader
and an agent see.

**What this does not cover.** It does not prove the real Gateway sends these frames; the Gateway's own tests do that
(`SessionLiveEndpointTests`, `ApiModeLiveRulingTests`). Two fields shown here are not on main yet, by the Delivery
Lead's ruling of 16 October 2026: the prompt's own words on the receipt item (`promptText`, being added on the Director
and the Gateway), and a durable opening of the stream, so a replay from the start holds the whole conversation. The
queued receipt shows no position because the Gateway's words carry none yet ("Queued: a turn is running"); that is
raised with the Delivery Lead as a Gateway change, not composed here.

## Run it again

```
PLAYWRIGHT_CORE=<a playwright-core folder> CHROMIUM=<a Chromium binary> \
  node docs/missions/api-mode-pi-2026-10-16/proofs/phase-3/harness/capture.mjs
```

On devlinux: `PLAYWRIGHT_CORE=/home/soren/ReposFred/rea/node_modules/playwright-core` and
`CHROMIUM=/home/soren/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome`. It serves the harness with the
workspace's Vite on port 5199 and overwrites the eight files above.
