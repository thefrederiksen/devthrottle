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
| A | The rail tells the truth about colour (items 1, 3, 6) | BUILT, pushed at 9c707d2a4. Under inspection. |
| A2 | The legend gets words for the two rendering sentinels | NOT STARTED - added after Phase A, see ruling below |
| B | Everything that comes off the cards (9, 7, 13, 2, 5, 15, then 8) | NOT STARTED |
| C | Every card says what the session is (10, 11, 14, 4) | NOT STARTED |
| D | The agreement test over the card field list | NOT STARTED |
| E | The prompt queue count on the roster feed (12) | NOT STARTED |

## Next Worker task

**Phase A2**, once the Phase A inspection is cleared. Small, and it closes a hole Phase A found
rather than created.

The desktop now paints two pixels the Gateway's legend has no words for: the NEUTRAL (a colour name
this build never learned) and desktop MAGENTA (which since Phase A means only that the Gateway
stamped nothing, while the Gateway's own legend note still describes magenta as "a colour this app
does not understand"). The Phase A seat correctly refused to show that now-wrong note and left it
out, so an unstamped session's hover is empty.

Build: a legend note per RENDERING SENTINEL on the Gateway, and the desktop rendering them. The
words are the Gateway's, as every word about a session is.

**The acceptance it proves:** every pixel a person can see on the rail can be explained, in the
Gateway's words, from the rail.

Then Phase B, unchanged in the mission document.

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

## Architect rulings made during the run

- **The neutral is SETTLED: gray-200 `#E5E7EB`.** Measured, not chosen - 3.90:1 against the palette
  grey `#6B7280`, where the Architect's original guess of `#D1D5DB` reached only 3.28:1. Both would
  have passed; the shipped one buys margin for nothing. Do not "simplify" it back.
- **"Both themes" is one theme, and that is a finding, not an oversight.** `App.axaml` pins the dark
  variant and nothing changes it. Recorded so a later reader does not think the light theme was
  skipped.
- **The legend gap is accepted and scheduled as Phase A2** (above), not waved through. A visible pixel
  nobody can get an explanation for is against the owner's standing ruling that the words are always
  the Gateway's - but it is not a regression, since the desktop had no legend at all before.
- **The word on the surviving cumulative number: "waited", not "idle".** Unchanged, Phase B.

## The test gate - how Phase A is allowed to land

**The owner's standing ruling: zero test failures on any platform, and NEVER quote a "pre-existing
failures" baseline as a pass.** The Phase A seat reported the macOS Avalonia suite at 567 passed and
7 failed, and verified the same 7 fail at v2.8.0. That verification was honest work and it is NOT
acceptance - it is precisely the baseline quote the owner banned.

So: **Phase A does not land on a Mac result.** Before the pull request, the default gate runs on
Windows and must be green. At the time of writing the only live Windows Director is on the owner's
main machine; the other Windows machine has been off the tunnel for two hours. Deferred until the
inspection is cleared so the gate runs once, on final code.

Two pre-existing findings for the owner's report, NOT mission work and not to be fixed here: the
macOS suite is red, and `CcDirector.Gateway.UnitTests` is parked (issue #2824), so a proof that lives
only there is a proof nobody sees at commit time.

## Where the owner's answers live

The design was settled with him over four versions of a dev report, "Session cards - what I intend to
build". His rulings are transcribed into the mission document with his own words where they exist,
and the Architect's inferences are marked as inferred there. The report is the record; the mission
document is the working copy.

## Reporting

He is bothered ONCE, at the end, with the QA report - unless something genuinely undecidable turns
up, in which case he is told immediately rather than guessed at.
