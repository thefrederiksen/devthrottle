# Review: the mission workflow reconciliation

Written 18 September 2026 by the review session for the workflow reconciliation mission. This file
is the whole output of the review; nothing else was changed, and nothing was committed.

## Scope

Read, in full:

- The diff on branch `mission/workflow-reconciliation` of `thefrederiksen/devthrottle`, worktree
  `D:\ReposFred\devthrottle-wf-reconcile`: all four files.
- `docs/method/method.html` sections 1 (the vocabulary), 5, 6, 7 and 8, in
  `devthrottle_internal`.
- `docs/method/BRIEF-workflow-reconciliation.md`, `MISSION-workflow-reconciliation.html`,
  `workflow-mission-reconciled.md` and `workflow-mission-reconciled.json`, and the summary of
  `docs/method/research/review-fable.md` (the six contradictions the mission exists to end).
- Both changed bodies in full, side by side with the method.

Ran, from the worktree:

- `dotnet test src/CcDirector.Gateway.UnitTests --filter WorkflowStoreTests`: 13 passed, 0 failed.
  This builds the Gateway and runs the fidelity test in its real harness.
- A byte-level simulation of the fidelity test's logic against both files on disk (it agrees with
  the real test run).
- `grep` for the word "Inspector" across both bodies and the whole Workflows folder, and for
  leftover old phrasings across the test projects.

Could not reach, and did not verify:

- The default local gate (`scripts/test-local.ps1`) and the parked suites beyond the
  `WorkflowStoreTests` class. The Workflow Store tests that the change touches are green; the rest
  of the Gateway unit test suite is the author's run to make, not this review's.
- The three other built-in workflows (standalone, standalone-with-review, survey): the mission
  document rules them out of scope (its question 2), and this diff does not touch them.
- What a deployed Gateway serves: no deploy has happened; this review covers the text in the
  worktree only.

## Verdict

The change is sound. It lands the seven intended edits, keeps everything the mission document says
must not change, and the fidelity test is honest. One minor finding below, one word, not blocking.

## The five checks

### 1. Does the new text say what the method says?

Yes. Checked section by section against method sections 5 to 8:

- The order of authority (mission document, method, workflow, repository files), the two override
  conditions (explicit and names what it replaces; the owner's words and dies with the mission),
  and "silence is never an override" all appear in the new preamble, matching section 5.
- The seat definitions match section 1 and the laws: the Manager as site manager who runs the
  mission's check itself and does not normally build (Build step description and the house); the
  Reviewer reading work it did not write (law 19); "whoever is being judged never arranges the
  review" (law 12); a review may return nothing and a finding must prove the harm (law 9); a review
  states its scope (law 10); the builder answers every finding, and if that seat is gone the seat
  that opened it answers (law 11).
- Law 4 of the workflow now matches the method's "where it ends" row: pull request, merged (the
  default), or in production only when the mission document says so in the owner's words, with
  proof committed beside the code in every case (laws 13 and 18).
- "Who may interrupt the owner" matches the method's Architect row: the Architect is the only seat
  that talks to the owner inside a mission; the Manager surfaces to its Architect; a standalone
  session speaks for itself.
- The Report step belongs to the Architect, matching the method's "produces the quality report",
  and the Build step says the Manager runs the check and sends work to a different-family Reviewer,
  matching the Manager row exactly.

One deliberate divergence is correct, not a defect: the old line "The Architect does not inspect
his own building" is gone, because the method's Architect does a final code review - the old
workflow line contradicted the method.

### 2. Did anything get lost?

No. I listed every removed line of both bodies and mapped each to one of the seven intended edits;
nothing was dropped outside them. Specifically confirmed present in the new text: the four laws'
substance; the pull request 1598 incident with its "check the branch history" caveat; the parked
Architect incident of September 2026; the fourteen-commits crash behind law 2; the 2026-08-01
`docs/missions/` measurement behind the record rule; the seat lifecycle (kill the Manager at each
boundary, the Reviewer seated fresh per review); the one-worktree rule; the messaging limits (six
an hour, queue, report); the record-landing rule and its "not yours to sweep" guard; and the
quality report guidance. The one thing retired on purpose, "tell the inspector to be adversarial",
is the method's own law 9 replacing it, and the sharp questions survive.

### 3. Is the fidelity test still honest?

Yes. Verified three ways: by reading the test, by simulating its logic byte for byte against both
files, and by running it. Both listed-edit source phrases occur exactly once in the skill body, both
target phrases occur exactly once in the workflow body, and the two bodies are equal modulo exactly
those two edits - the run of 13 tests includes the fidelity test, green. The test still fails on
drift in either direction: the final comparison is whole-body equality, and each listed edit
asserts its source phrase is present and unique before replacing it, so a reworded skill body fails
loudly instead of silently passing. The doc comment describing the two edits is still accurate.
The new second edit's target string hard-codes one line wrap; a reflow of that line in the
workflow file fails the test, which is the test doing its byte-fidelity job, not a defect.

### 4. Word check: "Inspector"

Confirmed by grep, not by eye: the word appears exactly once in each body, in the one deliberate
sentence saying the word is not used ("Inspector" is not a word this fleet uses). Nothing else in
either body uses it, in any case or derived form. One related residue exists outside the bodies -
see the finding.

### 5. Anything a reader would take as a standing grant?

No. The only grant language in the new text is the method's own rule, restated with its fences
intact: a grant exists only in the mission document, only in the owner's words, only for that
mission, and it ends when the mission does. The "in production" option in law 4 is fenced the same
way. Nothing in the diff grants the reader anything, and the preamble's own incident record (the
sentence that came to rest in a permanent document) survives to explain why the fences matter.

## Findings

### Finding 1 (minor): "inspections" survives in the Land-the-record step description

`src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs`, the "Land the record" step, still says
"brief, rulings, state note, inspections, fix reports, evidence". The same list, in the instruction
body this change edits, now says "the reviews" - the rename was applied to the body and missed here.

The harm: the step list is served conduct, shown by `workflow show mission` to the same audience
the reconciliation exists for, and the owner's decision recorded in the mission document is "We
should only use reviewer. I don't think we should use inspector on anything." The word "Inspector"
itself does not appear, so the mission's own stated check (grep the instruction body) still passes -
but the retired vocabulary survives on a second served surface, in a file this diff touches, in the
one list that was renamed everywhere else.

To be honest about provenance: the owner-checked `workflow-mission-reconciled.json` carries the
same word in the same step, so the author landed what was checked rather than missing it. The
inconsistency originates in the checked artifacts. It is still worth one word - "inspections" to
"reviews" - in `BuiltInWorkflows.cs` (and in the JSON if that file is kept as the record), either in
this pull request or as a one-word follow-up. It does not block the merge.

## Notes, no change required

- The workflow body calls itself "this file" in a few places ("Where this file and the method
  disagree"). The fidelity test's listed edits deliberately cover only the self-references that
  would be a lie; "this file" in the workflow body is imprecise but not false - the body is a file
  in the repository - and the wording comes from the owner-checked text. Not a defect.
- The workflow keeps a "Writing a mission brief" checklist while the method's section 7 defines
  the mission document template with required sections (the check, where it ends, questions). These
  are two artifacts in the method's model, and the workflow now defers to the method for the rules
  rather than restating them, so an Architect reading both writes the mission document the method
  requires. No disagreement survives that the deferral does not settle.

## Summary of the run

Change reviewed: the four files on `mission/workflow-reconciliation`. Tests run: the Workflow Store
tests, green, including the fidelity test. Findings: one, minor, one word in a step description,
not blocking. Nothing else found within the scope stated above.
