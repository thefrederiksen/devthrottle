# Phase 6 mandate: the screen

From the Delivery Lead, 19 September 2026. The last phase. The Director and the Cockpit get a page
that renders the saved report, and **that is all it does** - it renders. It scans nothing, it decides
nothing, and it must not be able to remove anything.

## Read these first

1. `mission.md`, and all of phases 1 to 5 - you are showing what they produce.
2. **`CLAUDE.md` critical rule 7, the client is dumb and the Gateway owns all ruling.** This phase is
   where that rule is either honoured or quietly broken, so read the reasoning and not just the rule.
3. `CLAUDE.md` critical rule 8, settings is one page on two surfaces, for the shared-component
   pattern in `packages/client-core` - two surfaces showing one thing is exactly this phase's shape.
4. `docs/VisualStyle.md` and `docs/CockpitVisualStyle.md`. All UI changes must comply.
5. `cc-devthrottle skill get devthrottle-method`.

## The rule that governs this phase

**The engine writes the finished sentences; the screen prints them.** Phases 2 to 5 built a report
whose lines are already written - the verdict, the reason a rule is broken, what is lost, how to get
it back. The screen does not re-derive any of it.

If you find yourself writing a conditional in a view that decides **what a state means** - as opposed
to how to lay out what the engine already decided - stop: it belongs in the engine. A screen that
rules for itself will, the first time it meets something it did not expect, render something
plausible instead of something true. That is precisely how the Voice screen came to offer a button
that could never work.

**In particular: a broken rule must read as broken on the screen.** The whole mission exists so that
"could not do its work" never renders as "nothing to remove", and the last place that can be thrown
away is here. Make it a test with a real broken report, and make the broken state louder than the
clean one rather than a grey footnote.

## What you build

- One page, on both surfaces, showing the saved report: what fills the disk, what is recommended,
  what is reported-but-never-offered, and the report's honesty lines - **the unseen gap, the folders
  that refused a listing, and the age of the scan**. A report whose reach is not shown is a report
  that overclaims, and phase 1 built those lines as first-class data precisely so a screen could not
  drop them.
- The three scan states from phase 5 - never run, running, failed - each visibly different. **"No
  report yet" must never be what a failed scan looks like.**
- A stale report says how old it is. A report from before the last rule refresh says that too.

## What phase 6 must NOT contain

- **No remove button, and no way to start a removal from any screen.** Removal is the command line
  tool, run by a person, and the mission forbids removal from any other surface. This is not a
  layout decision to revisit; it is the mission's boundary.
- No scanning from the Director or the Cockpit. They render what the Launcher saved.
- No verdict, colour, label or sentence computed in a view.
- No number the engine did not produce, including anything totalled up in the client.

## The proof you owe

1. `.\scripts\test-local.ps1` green in its own worktree. This phase touches the desktop and the web
   surfaces, so read the COVERAGE GAP honestly - and note that the default gate runs **no web tests
   at all**, so if you touched the browser shells you must run those yourself and say you did.
2. A test that a broken report renders as broken, with the reason shown.
3. A test that the reach lines - unseen gap, refused folders, scan age - reach the screen.
4. Both surfaces shown, not described. A screenshot or an accessibility dump of each, committed, per
   `docs/checking-docs-against-screen-dumps.md`: the dump says what the screen DID show, the markup
   only says what it CAN.
5. **What the proof does not cover, stated plainly.**

## How it ends

Local gate green, a Reviewer from a different agent family, findings answered in
`review-phase-6-answers.md`, merge. Then tell me, and I write the mission's report: the QA report on
issue 3120 covering the flow **and the failure cases**, and one page for the owner. Nothing is
released and no Gateway is deployed - both are his decisions, and the report says so.
