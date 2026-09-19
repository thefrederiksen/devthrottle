# Session Cards - Architect state

The compact handoff note. A fresh Manager needs THIS file, the mission document
(`missions/session-cards.html`), and the centrally held conduct
(`cc-devthrottle workflow instructions mission`). Nothing else - not the transcript, not the history.

Kept current by the Architect. If this file and a session's memory disagree, this file is right.

---

## Where the work lives

- Branch: `mission/session-cards`, cut from `origin/main` at v2.8.0 (`74485174f`).
- Worktree: one per mission, cut from `origin/main`. Never the shared checkout.
- Mission document: `missions/session-cards.html` - the why, the rulings, the phases, the scope.

## Phase board

| Phase | What it is | State |
|---|---|---|
| A | The rail tells the truth about colour (items 1, 3, 6) | NOT STARTED |
| B | Everything that comes off the cards (9, 7, 13, 2, 5, 15, then 8) | NOT STARTED |
| C | Every card says what the session is (10, 11, 14, 4) | NOT STARTED |
| D | The agreement test over the card field list | NOT STARTED |
| E | The prompt queue count on the roster feed (12) | NOT STARTED |

## Next Worker task

Phase A, and it is three items in one area. The desktop only; it needs nothing from the push seam,
so it blocks on nothing.

1. An unrecognised colour name paints a neutral instead of the magenta broken sentinel. The sentinel
   keeps its real and separate job - the Gateway stamped nothing while the tunnel is up and settled.
2. A colour legend on the desktop, reading the Gateway's existing legend route and rendering it
   verbatim.
3. The colour hover reads the legend title plus the Gateway's stamped label; the Director's own
   locally written reason stops being rendered.

**The acceptance it proves:** a Director too old to know a colour says so honestly, in a neutral that
cannot be mistaken for a state; and no user-visible word about a session's state is written by the
desktop.

## Facts verified at v2.8.0, so nobody re-derives them

Re-checked at `74485174f` after `origin/main` moved during the design round. All four still hold.

- The desktop's colour table sends an unrecognised name to the broken sentinel - the `_` arm in both
  `BrushFor` and `HexFor` in `src/CcDirector.Avalonia/StatusPalette.cs`.
- The display-state push carries SEVEN fields and the pin is not among them:
  `ApplyGatewayDisplayState` in `src/CcDirector.Core/Sessions/Session.cs` takes effectiveColor,
  stateLabel, triageBucket, needsYouSince, snoozeUntil, snoozeExpired, inboxLine.
- `SessionDto` carries no queue count. The only queue wording on it belongs to an unrelated hosted
  job status.
- The Cockpit roster card still renders the Wingman narration line, which Phase B removes.

## Standing constraints the phases must not break

- **The neutral is not the palette grey.** `#6B7280` already means snoozed or exited. The unknown
  neutral must be obviously a different grey, settled against `docs/VisualStyle.md` in both themes.
  That collision is why this was magenta in the first place.
- **Nothing changes the fold.** No colour arm, name or label. The mission changes how an
  unrecognised name is rendered, never how a colour is decided.
- **Removals before additions.** Phase B frees the width Phase C spends. Within B, the name wrap is
  last.
- **A new field on the wire must be proved harmless to a build that does not get it.** The pin
  (Phase C) and the queue count (Phase E) both cross the wire, and this mission exists because a new
  value reached an old build and became an alarm. Prove the old-build path, not just the new one.
- **Do not reintroduce pushing the colour hex.** The owner rejected it explicitly.

## Open, and the Architect's to answer - not the owner's

- The exact neutral value. Settle against the visual style guide; do not ship the Architect's guess.
- The word on the surviving cumulative number: "waited", not "idle". Recorded as an Architect call in
  the mission document.

## Where the owner's answers live

The design was settled with him over four versions of a dev report, "Session cards - what I intend to
build". His rulings are transcribed into the mission document with his own words where they exist,
and the Architect's inferences are marked as inferred there. The report is the record; the mission
document is the working copy.

## Reporting

He is bothered ONCE, at the end, with the QA report - unless something genuinely undecidable turns
up, in which case he is told immediately rather than guessed at.
