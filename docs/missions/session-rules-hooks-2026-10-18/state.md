# Session Rules part 2 - running state (Delivery Lead)

Updated 2026-10-18.

- Delivery Lead: session 147, "Session Rules - Delivery Lead - hook triggers, scopes, rules page", devlinux.
- Mission branch: `mission/session-rules-hooks` (holds the record). One worktree per parallel phase:
  - Phase 1: MERGED to main 2026-10-20 as #4268 (0eb3a64bc). Whole-phase review found the shell-form hook registration could block on Windows without Git Bash; fixed to exec form. Hosted Windows build found a publish defect; fixed. Windows rows and the September row follow as a follow-on pull request (Tech Lead 150, SOREN_NORTH session 117f19f9).
  - Phase 3: `../devthrottle-session-rules-p3`, branch `mission/session-rules-hooks-p3`, from main at 0eb3a64bc - Tech Lead seated (74796db6).
  - Phase 2: MERGED to main 2026-10-20 as #4267 (46ab20e2e). Two reviews (findings, then fixes: merge). Worktree and seats closed.
- .NET SDK 10.0.301 IS on devlinux, at `~/.dotnet/dotnet` (not on PATH; `/usr/bin/dotnet` is 8 and fails).
  So the unit and contract checks in section 7 run here.
- Each phase: one pull request to main, reviewed by Codex (then Claude Code on Fable, then Opus, per the
  repository rule), then merged.

## Open questions carried (section 10)
1. The why in the owner's own sentence - ask at the first natural point.
2. Style guide: ANSWERED by the repository - docs/CodingStyle.md, docs/VisualStyle.md, docs/CockpitVisualStyle.md exist on main.
3. Ask-handler timeout default - before phase 5 (recommend deny).

## Log
- 2026-10-19: a licence outage removed the SOREN_NORTH seats (Windows Developer, terminals 115 and 122); their work was already merged into the phase 1 branch.
- 2026-10-20: inotify limit raised to 1024 (owner). Phase 2 merged (#4267); CI started on main for the Python toolbelt change.
- Waiting on the owner: terminal 101 (C++ build tools on SOREN_NORTH, Yes at the PC) and terminal 102 (inference key for the September screen rule's live row).
- Codex is not installed on devlinux; Reviewers run on Claude Code with Fable, per the repository order.
- 2026-10-20: main went red from unrelated macOS changes (issue #4272, opened by the Delivery Lead; closed 13:01). Phase 1 merged (#4268); CI started on main.
- Owner unblocks replaced: the inference key is used where it already is (SOREN_NORTH cc-secrets); the Windows native build runs on GitHub's windows-latest runner instead of local C++ tools (the administrator prompt cannot be answered remotely).
- 2026-10-20: the owner stopped all sessions and asked for a status report (`status-report-2026-10-20.html`). Answers: fix the red main now; stop here and park phase 3 (D10); an unanswered Ask is denied (D9); the why confirmed (section 2). Resume issue: #4295.
- 2026-10-20: phase 1 was already in production (Gateway deploy run 39431533659 of 5d671bbb7, 13:34 UTC, not started by this mission), so the migration id was kept. Main's red from our migration fixed in #4297 (review: `reviews/migration-order-fix-review.md`). The unused alternative branch `fix/session-rules-migration-order-rename` renames the migration and is NOT safe to deploy; it is left in place, not merged.
- Mission PAUSED. No session seated.
