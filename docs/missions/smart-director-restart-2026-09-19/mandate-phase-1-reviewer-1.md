# Mandate - Smart Director Restart - Reviewer, phase 1, task 1: the contract types, the record marks, the session verbs, the request text

You are a Reviewer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 5a54dc21) and you report to it only. You read; you never build and never fix what you find.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-gate2`, a detached checkout of the commit
under review, `4d0ec0e1e` (branch `smart-restart/p1-contracts`). Change NO tracked file in it.

## What you review

    git diff origin/main...HEAD

The Developer's mandate is
`D:/ReposFred/devthrottle-smart-restart-p1/docs/missions/smart-director-restart-2026-09-19/mandate-phase-1-developer-1-contracts.md`.
The mission document is `mission.md` in the same folder, and `phase-1-interface.md` beside it is a
contract another phase is already building against. The Developer's own proof is
`docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-1.md` in your worktree. Do not trust
it: it is self-testimony. Coding standards: `docs/CodingStyle.md` and the repository's `CLAUDE.md`.

Look hardest at:

- do the types in `src/CcDirector.ControlApi/SmartRestart/` match `phase-1-interface.md` name for name
  and shape for shape; anything that differs is a finding, because phase 2 compiles against the
  document;
- the record marks: a Director on THIS build saving to a Gateway on an OLDER build, and an older
  Director reading a record written by this build - what is refused, what is silently lost. The
  Developer added two validation rules beyond its mandate (an authored record may not carry the marks;
  cancelled requires a smart shutdown): can either refuse a record the engine will legitimately write,
  for example an ignore-all or an operating system shutdown record;
- `InterruptAsync` and `EndAsync` on the real `SessionManagerDrainControl`: do they go through the
  Director's existing interrupt and stop paths, can either throw into a drain, and is it PROVED (not
  only said) that the old drain path never calls them;
- the request texts: is "you will not be killed" gone from the new text and still in the old one, and
  is the closing block asked for exactly as `DrainReportBlock` parses it;
- the change to `tools/harnesses/drain-index-diff/Program.cs`: was it needed;
- tests that hand-build their input and would stay green if the change were reverted.

You may run the check: `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
(the Tech Lead's own run on this commit: 475 passed, 0 failed). Foreground only, no sub-agents.

## What you write

One file, `docs/missions/smart-director-restart-2026-09-19/review-phase-1-1.md`, in your worktree, NOT
committed. It states your SCOPE first - what you read, what you ran, what you could not reach - and
then numbered findings. A finding must prove the harm: what breaks, for whom, and why it must change,
with the file and line. You may return nothing; "nothing found" within a stated scope is a valid
review. Do not list style preferences.

Never sign it: no agent or vendor name anywhere. ASCII only. Plain English, no abbreviations. Never
restart, drain, stop or message a session that is not yours. When the file is written:
`cc-devthrottle session report "<one paragraph: how many findings, the worst one>"`.
