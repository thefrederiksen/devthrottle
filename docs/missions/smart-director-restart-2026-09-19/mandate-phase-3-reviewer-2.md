# Mandate - Smart Director Restart, phase 3, Reviewer 2: the ANSWERS to review 1

You are a Reviewer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

You did not write this code and you are running a different agent from the one that did.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-review2`, detached at commit
`238d815f6`. Work only there. **You change no product code.** Your output is one file.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). Finish inside ONE turn and WRITE YOUR REVIEW FILE before your turn ends. If
   you stop half way to ask a question, nothing will ever answer you. Put the question in the file.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## This is a SHORT, NARROW review - do not re-review the whole engine

A first Reviewer, on a different agent again, already reviewed the way up engine in full at commit
`0cda45788` and found four things. All four were accepted by the Tech Lead and answered. **You are
reviewing the ANSWERS ONLY**: what changed between `0cda45788` and `238d815f6`.

    git diff 0cda45788..238d815f6

Read, in this order:

1. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1.md` - the four findings, and
   its section 5, which lists what that Reviewer attacked and FAILED to break. **A fix that undid
   any of those is your most important possible finding.**
2. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-engine-findings.md` -
   the Tech Lead's ruling on each finding, including what it ruled OUT of scope.
3. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1-answers.md` - what the fixing
   Developer claims. **Every claim is a claim to disprove.**
4. The diff.

## The four questions, and nothing else

1. **Is each finding actually fixed, or only made harder to see?** In particular: the reopen must
   refuse a seat that may still be running, and must not start two agents in one saved conversation.
2. **Did a fix break something the first review had proved sound?** The start-up presence check must
   still be unable to ask what is running - the first Reviewer's structural proof was that the
   Gateway seam had no such question, and the seam has now GAINED a roster method. Is that rule
   still held, and is the thing holding it strong enough?
3. **`AgentPluginLaunchMetadata` gained a required part, in `CcDirector.Core`.** Is every one of the
   eight values right, judged against what that agent's own launch path really does with an id - not
   against the table in the answers file? Does the walking test really walk every agent, and can it
   pass vacuously? Would a ninth agent added tomorrow be safe?
4. **What is left open.** The answers admit that across a Director restart the same seat can still be
   reopened twice, and that a reopen whose start FAILS keeps its claim and cannot be retried until
   the Director restarts. Are those the only two, are they stated where a reader will find them, and
   is the trade the right way round?

If you see something outside those four that would hurt a person using the product, say it - but
label it as outside your scope and rank it below the four.

## Run the checks yourself

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin|FullyQualifiedName~Agent"

Read the COUNT, never the colour: a filter that matches nothing exits green, and an ABORTED run can
print "Passed" for the tests that finished before it died - so check the run reached its end. The
Tech Lead's own runs on this exact commit were **580 total, 580 passed, 0 failed** and **247 total,
247 passed, 0 failed**, and the whole solution built with 0 warnings and 0 errors. If you see
anything else, that is your first finding.

Then break ONE thing yourself - your own choice, not one of the Developer's three - show the red
counts, put it back with `git checkout --`, and show green again on a FULL build. Never a no-build
run on the restore run: a no-build run certifies the binary, not the source. If your mutation
changes nothing, say so loudly.

Note: `SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state` fails
intermittently on untouched main with a SQLite error and passes when run alone. It matches the filter
only because "restarted" is in its name. Not this phase's; do not report it, but say if you saw it.

## What you owe

ONE file at `docs/missions/smart-director-restart-2026-09-19/review-phase-3-2.md`, in your own
worktree, committed and pushed on your own branch (`smart-restart/p3-review-2`), before your turn
ends. It must say: your scope and what you did NOT look at; your own counts and your own revert proof
with its numbers; each finding, ranked, with file and line and what it would do to a person; and what
you could not check. **"No findings" is a real answer** - then say what you attacked and failed to
break.

Do not fix anything, do not open a pull request against the code, do not merge anything.

**Sign nothing**: no "Co-authored-by", no "Generated with", no agent or vendor name, anywhere in the
file or the commit. ASCII only - no Unicode, no emoji, no arrows, no tick marks.
