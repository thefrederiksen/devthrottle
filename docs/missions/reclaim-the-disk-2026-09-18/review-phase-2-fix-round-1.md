# Phase 2 fix round one: the review

Written by the fix-round Reviewer, 19 September 2026, a different seat from the one that reviewed
the phase and a different agent family from the one that built it. This document answers one
question and only one: do the two commits of fix round one honestly and completely answer the five
findings in `review-phase-2.md`? It is not a second review of the phase.

**Verdict: approved.** All five findings are answered, the blocking one in code and by my own
reproduction, the four corrections in the files themselves and checked against the numbers. The
phase 2 branch at `2d81d7b96` can merge on this verdict.

## The delta

Two commits on `reclaim/phase2-rules-and-recommendations`, read against the reviewed commit
`eef8fee04`: `7670cef13` (the fold refuses a rule with no load-bearing control, thirteen lines of
code and two tests) and `2d81d7b96` (the record corrections and the answers document). The file
list of the delta is exactly what the answers document says it is: two Reclaim source files, one
test file, three mission documents, one answers document and one evidence file. Nothing else.

## Finding 1, the blocking one: FIXED, proven by running it

The original review proved its finding with a scratch reproduction - a rule reporting one control,
`files-examined: 0`, declared may-be-empty, folding to `verdict: ok, items: 0`. **I re-ran that
reproduction myself, written by me, run, then deleted; never committed.** I wrote it both ways
round on purpose: a test asserting the fold now reports Broken, and a test asserting the old
behaviour of verdict ok with items 0. The first passed. The second failed with `Expected: Ok,
Actual: Broken` - which is the finding answered, demonstrated by my own run and not only by the
committed tests. The fold refuses the shape the review found.

The fix cannot be passed by refusing everything, and I checked that by running rather than by
reading: the committed second test, a rule with one load-bearing control that counted, folds to ok,
and it is green in every run below.

**The revert proof, re-run by me.** I deleted the new check from `RuleFold.BrokenReasonFor` by
hand, ran the WHOLE suite: exactly one test red,
`Fold_ARuleWhoseEveryControlMayBeEmpty_ReportsBrokenBecauseNoCountCouldEverAlarm`, and the other
half stayed green. Restored, tree clean, whole suite green at 193.

On the question the original brief raised about a deleted-check revert: deleting the check is a
faithful revert, and more honest than switching it off with `if (false)`. A revert proof exists to
show the named test fails when the fix is absent; deleting the fix makes it absent in the plainest
possible way. The `if (false)` route is the one that fails here, and for the reason the proof
gives: this repository treats warnings as errors, so the unreachable code became a build failure,
and a build that does not run has told me nothing. The reasoning in section 8 of the proof is
correct.

**No rule that exists today changes its answer.** I counted the rules rather than taking the
claim: there are six (the installer rule, four package cache rules, the test scratch folders rule),
and I read every answer path in each `Examine` - every one of them declares at least one control
marked must-not-be-empty, including the path where the cache folder is absent. The claim in the
answers document is true of all six.

## Finding 2: FIXED, every number now stands on committed evidence

`evidence/scan-c-2026-09-19.json` is committed, and I checked it three ways:

- **Against the proof's table, number by number.** Elapsed 1,406.2 seconds; 3,531,656 files seen;
  1,168,877 folders seen; 695,943,581,377 bytes seen, which is the 648.1 gigabytes the table
  prints; 245 refused folders, every one named in the file; and the unseen figure is the volume's
  own used bytes minus the bytes seen, 77,178,528,063, which is the 71.9 gigabytes the table
  prints. Every number in section 3 that describes the scan now traces to the committed file.
- **Against the index still on this machine**, read-only. The file is the header of
  `c-28f709d79730.json` taken unchanged, exactly as its note says: the refused folder list is
  identical entry for entry, the volume block is identical, and the three bulk lists appear as
  their lengths (7,547 links, 1,799 folder totals, 245 refused). The committed evidence is
  genuine, not reconstructed.
- **Against the committed recommend evidence**, which names that same index as the one it read,
  at the same written moment. The two committed files corroborate each other.

The elapsed-time correction is made honestly: 1,406.2 is quoted as the scan's own measure of
itself and the earlier 1,409 is explained as the whole process timed from outside, rather than
silently replaced. The proof also now says plainly what is committed and what is not, which is what
the finding asked for.

## Finding 3: FIXED, re-run unfiltered by me

I changed `Candidates = broken ? [] : answer.Candidates` to `Candidates = answer.Candidates` by
hand and ran the WHOLE suite: **exactly three red**, the three the corrected proof names -
`Fold_ABrokenRule_OffersNothingEvenThoughItCollectedCandidates`,
`Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing`, and
`Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken`. Restored, tree clean, 193 green. The
correction is right, and the cause it records - the original number came from running the revert
under a two-name filter, so the third test was invisible rather than absent - is worth the rule it
now states: a revert proof is run against the whole suite.

## Findings 4 and 5: FIXED

I counted `RecommendationBuilderTests` myself: 9, as the corrected table says. I also counted
`RuleFoldTests`: 9. The phase total holds: 135 plus 58 new is the 193 the suite runs, and I saw
193 in every green run I made. The decisions document's unsupported clause about the temporary
folder is gone from section 8, and the sentence that remains says only the reason that was ever
backed by anything: product code creates the name, so it fails the proof.

## The local gate and the parked suites

I ran the default local gate myself at the phase tip: **2,466 tests, all nine suites
outcome=Completed**, matching the Delivery Lead's claim exactly. The COVERAGE GAP line names the
three parked suites. I checked the Delivery Lead's position against the diff:
`git diff --name-only origin/main...2d81d7b96` touches no Core source and no Gateway source - it
is the Reclaim projects, the command line tool, the tool registration, the mission documents and
the solution file. The gap fires because the Reclaim projects reference Core's logger, not
because any covered code changed, and the parked suites would prove nothing about a change that
touches none of them. I agree with the decline, on the condition the answers document already
accepts: the reasoning goes in the pull request, where the gate told the writer to put it.

## What else I checked on the delta

- **No removal code.** The delta adds a fold check, two tests, documents and evidence. No delete,
  move, recycle, process start, elevation or registry write appears anywhere in it. Nothing on
  this machine was removed, moved or changed by this review; the one thing I pointed at the real
  machine was a read-only comparison of the committed evidence against the index file.
- **Every touched file is pure ASCII**, checked character by character.
- **Nothing is signed.** No attribution trailer, no generated-with footer, no mention of any assistant,
  model or vendor in the two commit messages, the documents, the code comments or the answers
  document.
- **The answers document accepts all five findings and declines none**, and I verified that
  "accepted" means fixed in the code and the files rather than agreed with in prose: each fix is
  in the delta and I re-ran or re-counted every one.

## One observation, recorded and not a finding against this delta

The package cache rules satisfy the new requirement with a control whose count is a constant -
`cache-folders-looked-for: 1` - which meets the fold's letter but can never itself reach nought and
alarm. That is deliberate and was part of the phase body the first review held: a cache folder that
is absent is a real answer, not a failure, and the fold is strictly stronger with this check than
without it. I raise it only because the answers document says the fold asks for a control that
COULD alarm, and a constant count is a control that cannot. When phase 5 turns rules into data
refreshed from the Gateway, it may be worth asking whether a must-not-be-empty control whose
count cannot fail by construction still earns a refreshed rule its verdict. Nothing in this delta
needs to change for it.

## What this review did not cover

- I did not run the scan or the recommend command on the real machine. The committed evidence,
  the index on the machine and the first review's own reproduction of the recommend check already
  agree with each other, and re-running a twenty minute scan would prove the same thing a fourth
  time.
- I did not run the parked suites. The diff touches nothing they cover, as above.
- I did not re-review the rest of the phase. The first review's findings were the question, and
  they are answered.

**Approved.** The blocking finding is fixed and proven by my own reproduction and my own re-run of
both revert proofs, and all four record corrections are in the files and check out against the
committed evidence and my own counts.
