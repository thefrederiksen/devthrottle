# Session Rules part 2 - running state (Delivery Lead)

Updated 2026-10-18.

- Delivery Lead: session 147, "Session Rules - Delivery Lead - hook triggers, scopes, rules page", devlinux.
- Mission branch: `mission/session-rules-hooks` (holds the record). One worktree per parallel phase:
  - Phase 1: `../devthrottle-session-rules-p1`, branch `mission/session-rules-hooks-p1` - Tech Lead seated.
  - Phase 2: `../devthrottle-session-rules-p2`, branch `mission/session-rules-hooks-p2` - Developer seated.
- .NET SDK 10.0.301 IS on devlinux, at `~/.dotnet/dotnet` (not on PATH; `/usr/bin/dotnet` is 8 and fails).
  So the unit and contract checks in section 7 run here.
- Each phase: one pull request to main, reviewed by Codex (then Claude Code on Fable, then Opus, per the
  repository rule), then merged.

## Open questions carried (section 10)
1. The why in the owner's own sentence - ask at the first natural point.
2. Style guide outside the repository - ask before phase 3.
3. Ask-handler timeout default - before phase 5 (recommend deny).
