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
| A | The rail tells the truth about colour (items 1, 3, 6) | BUILT, INSPECTED, DEFECTS FIXED at af4671a0e. Second inspection and the Windows gate are running. Then it lands. |
| A2 | The legend gets words for the two rendering sentinels | NOT STARTED - added after Phase A, see ruling below |
| B | Everything that comes off the cards (9, 7, 13, 2, 5, 15, then 8) | NOT STARTED |
| C | Every card says what the session is (10, 11, 14, 4) | NOT STARTED |
| D | The agreement test over the card field list | NOT STARTED |
| E | The prompt queue count on the roster feed (12) | NOT STARTED |

## Next Worker task - PHASE A FIXES, before A2 or anything else

The independent inspection (`missions/session-cards/inspection-a.md`, Codex, a different agent
family) found two runtime defects and one coverage defect. The builder's own suite was green for all
three. Fix these, then A2.

**FIX 1 - the legend's words run off the window (the worst one).** In
`ColourLegendDialog.axaml.cs:157` the dot and the words column sit in a horizontal `StackPanel`,
which measures its children with unbounded width - so `TextWrapping.Wrap` at `:149` never has a
finite width to wrap at. Measured headless: nine of ten explanations exceed the 580-pixel viewport,
the widest at 1570. The window exists to show the Gateway's explanations and it cuts them off.
The existing test enumerates strings on an unmounted row, so it cannot see this.
*Proof required:* mount the dialog and assert every explanation's measured width fits the rows
viewport. A string-equality test does not cover this and must not be offered as if it did.

**FIX 2 - the hover can contradict itself: "Working: Snoozed".** `SessionViewModel.ColourHover:483`
passes `EffectiveColor` and `ActivityLabel` together. When the tunnel is down, `RailColor` returns a
LOCAL colour (blue for a locally working session) while `ActivityLabel` still returns the LAST
GATEWAY label ("Snoozed"). `SessionDotHover.For` then concatenates them. The inspector executed the
real functions and got the string "Working: Snoozed". `SessionDotHover.cs:19` claims the hover
"cannot disagree with the dot it is attached to"; the code does not support that claim.
This is a row contradicting itself, which is the exact defect class this mission exists to remove -
and Phase A introduced it by combining the two. Both halves pre-date the diff; the combination does
not. *Do not "fix" it by changing the offline floor* - that is a separate ruling nobody has made.

**FIX 3 - the legend-read test cannot tell the Gateway's words from this build's own.** The
inspector replaced the deserialized response with `SessionColourLegend.Build()` - the vocabulary
compiled into this Director - and **all 100 targeted tests still passed**. The test
(`SessionColourLegendReadTests.cs:58`) builds its fake response from the same `Build()` and asserts
only count, presence of cyan, and non-empty strings. An old build serving its own old words passes.
The whole mission is that the words are the GATEWAY'S; this is the one test that should prove it and
it does not. *Proof required:* wire-only wording that exists in no compiled constant, asserted to
reach the screen exactly.

**FIX 4 - two claims in the Phase A note are not true, and must be corrected rather than left.**
(a) "a failed read says what went wrong and draws nothing" holds only before a first successful
read: the cache deliberately keeps the previous legend (`SessionColourLegendCache.cs:150`) and the
dialog renders it without checking `Error`, so a failed refresh is invisible on an open window.
(b) the agreement check's "answered once" is true of the palette portion only, not of the whole
findings list. An unproven claim in a comment is worse than no comment.

**Also worth fixing while in there, both found by the inspection and neither counted as a defect:**
the comment at `ColourLegendDialog.axaml.cs:117` says the legend swatch and the session dot match
"by construction", which is FALSE in precisely this mission's version-gap case - the swatch uses the
wire hex and the dot uses the neutral. And opening a cold dialog can start two reads, because
`Current` begins a refresh and `LoadAsync` then calls `RefreshAsync` directly, bypassing the
in-flight guard.

**What the inspection cleared, so nobody re-does it:** sentinel reachability is sound (no known
colour reaches the neutral; no connected-settled unstamped session reaches it); no colour DECISION
changed; no log or guard was lost; the offline floor's ordinary blue/red/grey pixels are unchanged.
One real pixel change it did find: an offline HELD session with an unrecognised frozen stamp now
paints neutral where it painted magenta. That follows from the ruling and is correct - but the claim
"the floor is universally unchanged" would be false, so do not write it.

---

## Then: Phase A2, once the Phase A inspection is cleared. Small, and it closes a hole Phase A found
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

## THE WINDOWS GATE IS MEASURED - and the repository's gate is red for every agent

Run on Windows at `af4671a0e`: **the solution builds whole**, all nine test projects ran,
**2451 of 2453 passed**. Detail in `missions/session-cards/windows-gate.md`.

The two failures are `LauncherDeclaredCapabilitiesTests`, and they are NOT this mission's:

- **Positively verified:** this branch's diff touches nothing under `Launcher` - checked by file
  list, not assumed.
- **The cause was found, not excused.** Inside a Director session `CC_DIRECTOR_ROOT` names the live
  instance whose real launcher IS listening, so the kernel truthfully answers "armed" and a test
  asserting an UNARMED launcher fails. Remove the variable and the Launcher suite is **197 of 197**.

**That clean-environment result is the pass condition, and the baseline is NOT.** The worker also
measured `origin/main` failing the same two, and that fact is recorded but is explicitly not the
justification - the owner has banned a pre-existing-failures baseline as a pass, and rightly, because
it certifies nothing. What licenses the landing is the positive 197 of 197.

**A finding for the owner, bigger than this mission.** The default gate cannot pass when run from
inside a Director session - which is how every agent runs it. The test reads a real machine-wide
signal instead of isolating one. Not fixed here; it is not this mission's work.

**Still owed before the pull request:** the branch is behind `main`. Verified it merges cleanly
(`git merge-tree`, no conflicts), so the joined result is untested only in the sense that no suite has
run on the merge itself.

## The old landing note, superseded by the section above

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

## Architect ruling: a stale legend on an OPEN window - folded into A2

The Phase A fix seat raised one question rather than deciding it, correctly. When the legend window
is already open and a REFRESH fails, the window goes on showing the last-known words and says
nothing. That is the deliberate last-known-answer behaviour, so it is not a defect - but a surface
showing words that may no longer be true, without saying so, is a quiet version of the very thing
this mission is about.

**Ruling: it goes into Phase A2, not into Phase A.** A2 is already "the legend gets words for what
it cannot explain", and "these words may be stale" is the same kind of sentence. It is not a
regression and it does not hold up the landing.

## Inspecting when an agent family runs out of credit

The second inspection was seated on Codex and wedged on a usage-limit prompt before it cut a
worktree: it produced nothing, and it could NOT be rescued, because a fleet message is queued and
never typed into a live session - there is no way to answer a prompt inside another seat's terminal.

Do not wait out the reset if another family is installed. Check what is actually on the machine
before choosing (`command -v codex gemini grok copilot opencode pi`) - on this machine Gemini is NOT
installed and the spawn fails with a clear message, while Codex and Pi are. The requirement is a
family different from the builder's, not one particular vendor.

## A SEAT THAT FINISHES MAY NOT REACH YOU - GO AND LOOK

Both Phase A seats finished their work, pushed it, and **failed to get a report back to the
Architect**. `session report` is being refused while any machine in the fleet is off the tunnel,
because the roster cannot be read in full. The first seat worked around it with a message; the
second did not, and its finished work sat unnoticed.

So do not treat silence as "still working". A seat that has stopped shows as **snoozed** in the
session list - that is the supervised-and-stopped fold, not a snooze anybody set. Check the branch
for new commits and the session list for a snoozed seat, on your own initiative, rather than waiting
for a doorbell that may never ring.

## If the fleet tools disappear mid-run

They did, on 19 September, about an hour into the run: `cc-devthrottle`, `cc-dev-reports` and
`cc-ship` all vanished from the Director's `bin` directory, leaving only the pty shims. Do not
restart the Director to fix it - your own session runs under it.

The fix is a wrapper, because from v2.8.0 **every fleet command goes through the Gateway, not the
Director**. It needs only `CC_GATEWAY_URL` and `CC_GATEWAY_SESSION_KEY`, which a session already
has. Build a virtual environment, install `typer`, `rich`, `requests` and `certifi`, put the tool
sources on `PYTHONPATH` under their package names (`cc_devthrottle`, `cc_dev_reports`, `cc_shared`,
`cc_storage`), and run `tools/<tool>/main.py`.

Two traps. A fresh virtual environment has no certificate bundle, so every Gateway call fails with
`CERTIFICATE_VERIFY_FAILED` - set `SSL_CERT_FILE` and `REQUESTS_CA_BUNDLE` to `certifi.where()`. And
take the tool sources from a CURRENT worktree: the shared checkout is hundreds of commits behind and
does not even contain `tools/cc-dev-reports`.

A diagnostic worth keeping: this Director listens on NO loopback port - it is tunnel-only, with just
outbound connections to the Gateway. So `CC_DIRECTOR_API` is meaningless here, and any tool that
still wants it is being read from a stale checkout.

## Where the owner's answers live

The design was settled with him over four versions of a dev report, "Session cards - what I intend to
build". His rulings are transcribed into the mission document with his own words where they exist,
and the Architect's inferences are marked as inferred there. The report is the record; the mission
document is the working copy.

## Reporting

He is bothered ONCE, at the end, with the QA report - unless something genuinely undecidable turns
up, in which case he is told immediately rather than guessed at.
