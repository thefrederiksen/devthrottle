# Phase 2: the review, and what was done about each finding

The phase 2 review was written by a Reviewer seat running a different agent family, which re-read the
phase and re-ran what it chose to rather than taking this seat's own proof on trust. It returned **not
approved**: one finding had to be answered first, with four corrections to the record listed beside it.

**All five are accepted. None is declined.** This document answers each one, as the method requires.

## Finding 1, the blocking one: the fold could still be failed open - ACCEPTED, FIXED

**What the review found.** `RuleFold.BrokenReasonFor` refused a rule that reported no controls at all,
but accepted a rule whose every control was declared may-be-empty. Such a rule can never report
broken, because no count it reports can ever alarm. The Reviewer proved it with a scratch test folding
a rule with one control `files-examined: 0` declared may-be-empty, and the fold returned **verdict ok**,
with the finished lines reading `verdict: ok` and `items: 0`.

That is "nothing to remove", printed by a rule with nothing behind its answer - the exact failure this
phase exists to make impossible, and the exact failure the rest of the phase is built to catch. The
guard stopped a rule from failing open by not counting; it did not stop one from failing open by
counting only what it had already excused.

**What was done.** `BrokenReasonFor` now treats a rule that declares no control marked
must-not-be-empty the same way as one that declares no controls at all, immediately after that check
and for the same stated reason. Two tests hold it:

- `Fold_ARuleWhoseEveryControlMayBeEmpty_ReportsBrokenBecauseNoCountCouldEverAlarm` - the shape the
  Reviewer found, now refused, with the finished lines checked to not carry `verdict: ok`.
- `Fold_ARuleWithOneLoadBearingControlThatCounted_IsOkEvenWhenItOffersNothing` - the other half, so
  the fix cannot be satisfied by refusing everything. The fold asks for a control that COULD alarm,
  not for one that did.

Proven by revert: the check deleted outright, rebuilt, whole suite run, the named test red and alone;
restored, rebuilt, 193 green, `git diff HEAD` empty. `phase-2-proof.md` section 8 has it in full,
including why switching the check off with `if (false)` is not a revert proof in a repository that
treats warnings as errors.

**No rule that exists today changes its answer** - all six Windows rules already declare at least one
must-not-be-empty control. The finding matters for the rules that do not exist yet, which is what
makes it worth a blocking finding rather than a note: phase 5 turns rules into data refreshed from the
Gateway, and this fold is the single gate a refreshed rule passes through.

## Finding 2: the proof claimed evidence it had not committed - ACCEPTED, FIXED

`phase-2-proof.md` said "Both runs are committed in `evidence/`". Only the recommend run was. The scan
run had no committed evidence at all, and reading the finding closely makes it sharper than it first
appears: the file count, the folder count and the 245 refused folders in the proof's own table were
read off a console that was not kept, so several numbers in a proof document had nothing behind them.

Rather than soften the sentence, the evidence was committed:
`evidence/scan-c-2026-09-19.json` is the header of the index that scan run wrote - what it did, how
long it took, what it saw, and all 245 refused folders in full - with its three bulk lists reduced to
their lengths, taken unchanged from the index. Every number in that table now comes from it. The
proof says plainly what is committed, what is not, and why.

It also corrects the elapsed time. The proof said 1,409 seconds; the scan's own measure of itself is
1,406.2, and 1,409 was the whole process timed from outside. The rule's own number is now the one
quoted, with the difference explained rather than silently replaced.

## Finding 3: the third revert proof turned three tests red, not two - ACCEPTED, FIXED

`Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken` also failed and the proof did not list it.

Re-run unfiltered here before writing this correction, to confirm rather than accept: three red, the
two originally named plus that one.

**The cause is worth more than the correction.** The wrong number was not a wrong run, it was a
filtered one - the original proof ran that revert under `--filter` with two test names in it, so it
could only ever have reported those two. The third was invisible, not absent. A revert proof run
against a filter can only tell you about the tests you already thought of, which is the opposite of
what a revert proof is for. The proof now records this, and the rule it implies: **a revert proof runs
against the whole suite.**

## Finding 4: the test table said 8 where the file holds 9 - ACCEPTED, FIXED

`RecommendationBuilderTests` holds 9. Counted independently here before correcting. The phase total of
56 was right, which is why it survived - the table's own rows had never been added up against it. Both
numbers have moved since, to 9 and 58, because fix round one added two tests.

## Finding 5: an unsupported sentence about the temporary folder - ACCEPTED, FIXED

`phase-2-decisions.md` section 8 said the name `cc-director` "appears in the temporary folder". Nothing
was ever checked to establish that, and it was not the reason for the decision - the reason, in the
same sentence, is that product code creates the name, so it fails the proof. The unsupported clause is
removed and the real reason left standing.

## What the review re-ran, and one thing it found that this phase did not cause

The Reviewer independently reproduced all four original revert proofs, the suite, the local gate, and
the read-only `recommend "C:\" --json` check on this machine, and confirmed no removal code exists in
the phase, that everything is ASCII, and that nothing is signed.

It also reported one unrelated flake:
`ProcessRunnerTests.Run_ProcessExceedingTimeout_IsKilledAndReturnsTimeoutFailure` in
`cc-director-setup-engine.Tests`, which passed on re-run twice. This phase touches nothing that test
reaches. It is recorded here rather than fixed inside a phase it has nothing to do with.
