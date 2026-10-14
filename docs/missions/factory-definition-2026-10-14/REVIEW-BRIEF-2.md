# Review brief 2 - checks and factory skills

You are a Reviewer. You read; you never build and never fix. You may return nothing: a finding must prove the harm.
State your scope.

The owner added two things to `docs/missions/factory-definition-2026-10-14/MISSION.md` after the first review
(`REVIEW-1.md`): decisions F5 (factory skills) and F6 (a Python check decides whether a seat's agent runs), and the
Architect wrote them in as A2, A9, A10, sections 5.2, 5.5 step 3, 5.5a, 5.5b, 5.9, goal lines 15 and 16, phase 0(c)
and (g), phase 5b and their QA. The diagram `diagram-where-it-is-saved.svg` changed to match.

Review ONLY those additions, against the code on origin/main in this worktree, in particular:

1. The claims about today's trigger checks (`src/CcDirector.ControlApi/Triggers/DirectorTriggerRunner.cs`, the trigger
   section of `docs/cli-reference.md`, the Gateway's trigger service and cron fire path) are true.
2. "On a schedule, the Gateway asks the seat's machine to run the check first, over the Director command channel":
   is that buildable from what exists, and does anything in the cron fire path make it wrong?
3. Secrets through `cc-secrets run`, the `context` and `state` keys, `factory_check_state`, and the one-writer claim.
4. Factory skills reaching ONE session only, outside the checkout and the user's home: does anything in how the
   Director installs and links skills today (`src/CcDirector.Core/Skills/`) make that harder than the document says?
5. Goal lines 15 and 16 are each delivered by a phase and proven by a QA line.

Write the review into `docs/missions/factory-definition-2026-10-14/REVIEW-2.md`, the same shape as REVIEW-1.md. Edit no
other file, do not commit, do not push, do not run `session done`. When finished, print "REVIEW WRITTEN" and stop.
