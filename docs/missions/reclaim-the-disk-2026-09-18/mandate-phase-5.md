# Phase 5 mandate: the background scan, and rules as data

From the Delivery Lead, 19 September 2026. Phase 5 does two separable things, and **they should be
two pull requests**: the Launcher hosts the scan so it happens without anybody asking, and rules
become versioned data refreshed from the Gateway so a new rule reaches every machine without a
release.

**This phase ends at MERGED.** The Gateway deploy that would actually push a rule to the fleet is the
owner's decision, taken through the `deploy-hosted-gateway` skill, and it is not yours to make or to
ask for. Build it, merge it, say in the report that the deploy is his call, and stop.

## Read these first

1. `mission.md`, especially "The pieces" in section 5 and the phase 5 line in section 6.
2. All of phases 1 to 4 - their decisions, their proofs, and their code. You are wiring what exists.
3. `CLAUDE.md` critical rule 7, **the client is dumb and the engine owns all ruling**, which this
   phase is the biggest test of so far, and the section on built-in skills having ONE source, which
   is the model the rule refresh copies.
4. `cc-devthrottle skill get devthrottle-method`.

## Part one: the Launcher hosts the scan

- **The Launcher, not the Director.** There is one Launcher per machine and several Directors; a scan
  per Director is the same disk read several times over, and two scans writing one index is a defect
  waiting to happen. Guard against a second scan starting while one runs, and make the guard a test.
- Speed is explicitly out of scope for this mission ("we can do this in the background, offline, and
  slowly"), so **do not optimise the scanner**. Design for the measured 1,406 seconds a full C: scan
  took in phase 2, not for the design probe's 181 - the number to build around is in
  `phase-2-proof.md`.
- A scan that is running, a scan that failed, and a scan that has never run are **three different
  states and must be told apart**. "No report yet" that silently means "the scan crashed" is the same
  failure this whole mission exists to prevent, one level up.
- It must be interruptible and must not leave a half-written index that a later read believes. Write
  the index somewhere temporary and move it into place, and **test the interrupted case**.
- No removal, ever, from the Launcher or from anything it schedules. The mission forbids unattended
  removal outright. The background job scans and recommends; a person runs `reclaim`.

## Part two: rules as data

- A rule file is data with a **version**, shipped inside the product and refreshed from the Gateway
  the way built-in skills are. Read how the skill seeder does it before inventing a second mechanism;
  the one-source rule in `CLAUDE.md` is there because two copies of a shipped thing drift in both
  directions, and it has already happened once in this repository.
- **A rule file can only select among the three proof kinds the engine implements. It cannot
  introduce a new way to delete.** This is the hard boundary of the phase. A rule file that names an
  unknown proof kind is refused, not defaulted, and that refusal is a test.
- **A refreshed rule passes through `RuleFold` exactly like a built-in one.** Phase 2's fix round
  added a check that refuses a rule declaring no control that must not be empty, written precisely
  for the rules that arrive this way - a rule from the Gateway is the least trusted rule in the
  system, and it gets the same gate rather than a looser one. Prove it with a test that folds a rule
  loaded from a file.
- A rule file that will not parse, a version that cannot be read, or a refresh that fails must leave
  the machine on the rules it already had and **say so**. It must never leave the machine with no
  rules, because no rules produces an empty recommendation that reads exactly like a clean disk.
  A test holds that.
- Never let a rule file widen what the tool may touch beyond what the refusal list allows. The
  refusals are the engine's, not the rule's, and a rule cannot turn one off.

## What phase 5 must NOT contain

- No Gateway deploy, and no request for one. Merged is where this stops.
- No unattended removal, no scheduled removal. Scanning and recommending only.
- No second copy of a rule that is authored in two places.
- No scanner optimisation dressed as a requirement.
- Nothing that lets a downloaded file decide what may be deleted.

## The proof you owe

1. `.\scripts\test-local.ps1` green in its own worktree. This phase touches the Launcher, so
   `CcDirector.Launcher.Tests` matters here in a way it has not in earlier phases - and if the
   COVERAGE GAP names parked suites this time, **check whether it is right**: unlike phases 2 to 4,
   this phase plausibly does touch code those suites cover, and the honest answer may be to run
   `-Parked` rather than to explain it away.
2. The three scan states told apart, each by a named test.
3. The interrupted scan leaving no index a later read believes.
4. A rule loaded from a file going through the same fold, including one that declares no
   load-bearing control and is refused.
5. A failed refresh leaving the machine on its previous rules, with the machine never left with none.
6. **What the proof does not cover, stated plainly.**

## How it ends

Local gate green, a Reviewer from a different agent family, every finding answered in
`review-phase-5-answers.md`, merge. Then tell me; the deploy is the owner's and I will put it in the
report rather than ask him mid-mission.
