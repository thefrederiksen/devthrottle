# Mission record: API mode for agent wrappers, proven on Pi

**The mission document is GitHub issue #4070**, "Mission: API mode for agent wrappers, proven on
Pi" (16 October 2026). Its first comment holds the Architect's Pi proof script and wire log. This
folder is the mission's record: the design notes, the spend, and, as the phases land, their reviews
and proofs.

## Phases

| Phase | What | Status |
|---|---|---|
| 0. Event model | The session event model and the Pi mapping; real Pi wire logs recorded as test fixtures. | Merged |
| 1. Transport and Pi dialect | The API-mode transport, Pi's dialect, delivery from acknowledgement, Stop as abort, resume by session id. Unit tests replay the recorded wire logs through a fake process. | Merged |
| 2. Gateway ruling | Fold the events into the display verdict, store receipts, relay live events, the take-the-wheel and hand-back verb. | Merged |
| 3. Live Chat | client-core rendering of live events, receipts and tool cards; Cockpit and phone wiring. | Merged |
| 4. Take the wheel | Mode switch, ownership lock, the queue held while the user has the wheel, the Terminal tab buttons. | Merged |
| 5. Choosing it | The New Session Connection row (Cockpit, phone, desktop), the roster tag, `--connection` on spawn and in list. | Merged |
| 6. Proof and QA report | The mission's check (issue section 7), on Linux and Windows. | Merged |

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

## Status at close (20 October 2026)

Every phase is merged on main, and the work shipped in release v2.25.0. The hosted Gateway was deployed twice under the owner's grants: at 9f9ad80 on 20 October 2026 (outage 12.9 seconds), and at e02f5a8 the same day (outage 4.6 seconds) to put Pi's default back to Terminal, as the owner decided ("I don't think Pi should be defaulted to API mode until we've tested it more and used it more").

- The QA report: [proofs/phase-6/qa-report.html](proofs/phase-6/qa-report.html) - Linux 27 of 27 on Pi 1.2.0; Windows on Pi 1.3.0 across four runs, where every check passed in at least one run except the Stop check (Stop's effect shown in runs 6 and 8), because the Windows provider refused or delayed replies (429 engine_overloaded).
- The Delivery Lead's rulings, owner decisions and follow-ups: [delivery-lead-decisions.md](delivery-lead-decisions.md).
- Spend: 1.17 dollars recorded by Pi (3.61 dollars upper bound) of the 10 dollars granted.
