# Production deployment and live-browser proof

Date: 2026-09-12

## Deployment

Hosted deployment run
[34679499149](https://github.com/thefrederiksen/devthrottle/actions/runs/34679499149) deployed merge
commit `53b8abfc3167125b6d2b7ab5ef33a1e7014ced53` successfully. The deploy job and production watch
both passed. The workflow reported:

- production healthy;
- time to healthy: 35 seconds;
- total unavailable time: 8.9 seconds;
- longest external outage: 4.4 seconds;
- swap gap: 0.0 seconds;
- site state remained `Running`;
- rollback: no;
- serving commit at the end: `53b8abf`.

An independent request to production `/healthz` returned HTTP 200. The migration comparison was
unknown because the outgoing commit was absent from the workflow's shallow checkout; this is not
represented as a migration proof.

## Browser activation

The Director-owned `Center Consulting` browser profile was used for the account
`soren@centerconsulting.com`. The mobile app required enrollment. After the owner explicitly approved
the consent, `Connect this phone` registered one device named `Android phone` and returned the browser
to the production mobile roster. No credential is included in this record. The managed browser was
stopped cleanly after verification, with its login retained.

## Roster-card proof

The production page was rendered at a 390 by 844 viewport. The evidence enumerated the card population
from `li.row`, then checked each derived row for `.row-chip-agent`; it did not count only already-stamped
elements. The result was 23 roster cards and 23 agent-tool chips. The observed tool values included
`Claude Code`, `Pi`, and `Codex`.

Two rendered screenshots are part of this record:

- `mobile-roster-production-390x844.png` shows the live narrow roster and the compact metadata row.
- `mobile-roster-pi-production-390x844.png` centers session 117, `cc-consult - agent wrappers`; its
  independent chips read `Pi`, `SOREN_NORTH`, and `cc-consult`.

This proves the deployed contract reaches the rendered narrow-browser roster. It does not prove a
physical phone's platform-specific rendering.

## Wingman live-check blocker

The supplied session was `testing pi`, id `bae4eea3-b416-4dbc-9790-6bea9aaffa2d`. Production's
authenticated history endpoint returned these positive facts:

- agent kind: `Pi`;
- model: `gpt-5.6-terra`;
- ending kind: `closed`;
- ended at: `2026-09-12T00:45:31.690641Z`.

The cached Wingman voice endpoint still returned the narration generated at
`2026-09-11T23:19:28.2835875Z`, which describes the older successful rename/model answer. The live
390 by 844 Voice route instead rendered `Reconnecting to this session's computer...` and had no live
terminal to read; `testing-pi-closed-production-390x844.png` preserves that state.

The changed production behavior chooses a later, positively classified terminal failure only after
reading the live terminal. Because the route to the closed session is gone, neither replaying the old
cached clip nor regenerating from the stored conversation exercises that behavior. The live
terminal-failure narration therefore remains unproven. A replacement session must already be in the
same failure shape; the verification may navigate and invoke Wingman explain/play, but must not send a
prompt, change a credential, or substitute a model.
