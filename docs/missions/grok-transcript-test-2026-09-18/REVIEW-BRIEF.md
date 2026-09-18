# Review brief - issue 3029, the Grok transcript test

Written by the Delivery Lead of the mission "A test that does not depend on what this machine has
ever run". You are the Reviewer. You run a different agent family from the Developer who wrote this
work, which is the entire mechanism - do not soften it by agreeing with them.

## What to read

The change is two commits on the branch `mission/grok-transcript-test-3029`, and this worktree is
already checked out at its tip:

```
git log --oneline 3921392ab..HEAD
git diff 3921392ab..HEAD -- src/
```

The only code file touched is `src/CcDirector.Gateway.UnitTests/TurnsVerbUnresolvedTranscriptTests.cs`.
The mission document is `docs/missions/grok-transcript-test-2026-09-18/MISSION.md`; the Developer's
own account is `FIX-REPORT.md` beside it, with six run logs in `evidence/`.

**Do not trust the fix report.** It is self-testimony: written by the seat that did the work, about
its own work.

## What the change is meant to do

`Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk` built its session on
`Path.GetTempPath()` and asserted that no locator can resolve a transcript for it. On this machine
that was false - somebody had once run Grok from the temporary directory, so
`%USERPROFILE%\.grok\sessions\C%3A%5CUsers%5Csoren%5CAppData%5CLocal%5CTemp` exists, the Grok locator
resolved a real transcript, and the verb correctly answered `ok`. The product was right; the test's
assumption about the machine was wrong.

The fix is meant to give the test a working directory **no locator can ever resolve on any machine**.

## The sharp questions

1. **Is the new directory genuinely unresolvable by every locator, or only unlikely to be
   resolvable?** Read the locators in the product, not the test's comment about them. Name the ones
   you checked and the ones you could not reach. A directory that is merely fresh today is the same
   defect in a new coat.
2. **Could a constant be substituted, or the product be broken, and this test stay green?** The test
   guards issue 2561 - a session that sat silent for 48 minutes because this verb reported an
   unresolved transcript as a successful read of an empty conversation, and voice narration recorded
   "nothing to narrate", which is never retried. If the assertion has been weakened, or the Grok case
   quietly skipped, or the failure mode moved somewhere the test no longer looks, say so.
3. **Does anything the change claims go beyond what the code supports?** Comments, names, the fix
   report - a claim next to code that does not do it is worse than no claim.
4. **Is any cleanup able to delete something it did not create?** The fixture removes a directory it
   made. Check what it would remove if creation failed part way, or if the path were somehow the
   shared temporary directory itself.
5. **Was any product code changed?** The mission forbids it. If product code moved, that is a
   finding and the mission's premise is in question.

## What you owe

- A review written into `docs/missions/grok-transcript-test-2026-09-18/REVIEW.md` in this worktree,
  committed and pushed to `mission/grok-transcript-test-3029`. This worktree is on a detached head,
  so push with `git push origin HEAD:mission/grok-transcript-test-3029`. Commit as the owner - no
  assistant, model or vendor named anywhere, no "Co-authored-by", no "Generated with".
- **State your scope as well as your verdict**: what you read, what you ran, what you could not
  reach. "Nothing found" means nothing found within that scope - without the scope, an empty review
  and a review that never ran look identical.
- **You may return nothing.** A reviewer told to find gaps will find them whether or not they exist.
  A finding must prove the harm: what breaks, and why it must change.
- **You never fix anything.** Do not touch the test, the product, or the fix report. Findings go
  back to the Developer through the Delivery Lead.
- One `cc-devthrottle session report` line when the review is pushed, saying where it is and your
  verdict in a sentence.

You may run tests. If you do, say so and give the numbers.
