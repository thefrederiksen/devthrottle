# Mission record: API mode for agent wrappers, proven on Pi

**The mission document is GitHub issue #4070**, "Mission: API mode for agent wrappers, proven on
Pi" (16 October 2026). Its first comment holds the Architect's Pi proof script and wire log. This
folder is the mission's record: the design notes, the spend, and, as the phases land, their reviews
and proofs.

## Phases

| Phase | What | Status |
|---|---|---|
| 0. Event model | The session event model and the Pi mapping; real Pi wire logs recorded as test fixtures. | In review |
| 1. Transport and Pi dialect | The API-mode transport, Pi's dialect, delivery from acknowledgement, Stop as abort, resume by session id. Unit tests replay the recorded wire logs through a fake process. | Not started |
| 2. Gateway ruling | Fold the events into the display verdict, store receipts, relay live events, the take-the-wheel and hand-back verb. | Not started |
| 3. Live Chat | client-core rendering of live events, receipts and tool cards; Cockpit and phone wiring. | Not started |
| 4. Take the wheel | Mode switch, ownership lock, the queue held while the user has the wheel, the Terminal tab buttons. | Not started |
| 5. Choosing it | The New Session Connection row (Cockpit, phone, desktop), the roster tag, `--connection` on spawn and in list. | Not started |
| 6. Proof and QA report | The mission's check (issue section 7), on Linux and Windows. | Not started |

## What is here

| File | What it is |
|---|---|
| [event-model.md](event-model.md) | Phase 0: the agent-neutral session event model, the Pi mapping, and the framing rule. |
| [spend.md](spend.md) | Every real Pi run's tokens and estimated dollars against the owner's 10 dollar grant. Every seat that runs Pi adds its rows. |

Outside this folder:

| Path | What it is |
|---|---|
| `scripts/record-pi-rpc-fixture.py` | Records real Pi wire logs as fixtures (replaces the Architect's script in the issue comment). |
| `src/CcDirector.Core.Tests/TestData/pi-rpc/` | The recorded wire logs, with their own README. Copied into the `CcDirector.Core.UnitTests` output, which runs in the default gate. |
