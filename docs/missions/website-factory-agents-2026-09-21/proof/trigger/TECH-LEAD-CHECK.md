# Tech Lead's check - the trigger (pull request 3273)

Run by the Tech Lead (product track), 21 September 2026, on head `f9383dfe6`, in a worktree of its own
(never the Developer's), before merge. The review is `REVIEW.md` beside this file (Pi, GLM-5.3 - Codex was out
of usage): three findings, answered by the Developer at its foot.

- Finding 1 (medium) accepted: a `skipped-running` check behind a session started more than six hours ago is
  RED, naming the session. The Tech Lead then ruled the way out: resuming a paused trigger clears the lock and
  records a row saying who released it and which session it had waited on (`f9383dfe6`), so the owner never has
  to delete a trigger - and lose its history - to unstick it.
- Finding 2 (low) declined, accepted by the Tech Lead: the start-to-record window is shared with schedules and
  is to be decided once for both. Worst case is one duplicate session doing the same work.
- Finding 3 (low) accepted: `--count` / `-n`.

| What | Command | Result |
|---|---|---|
| Default gate | `.\scripts\test-local.ps1` | Every suite Completed except Launcher.Tests, 195 passed, 2 failed: the two restart-signal tests known red on clean main (issue 3242). No Launcher change here. |
| Gateway unit tests, whole project (parked) | `dotnet test src\CcDirector.Gateway.UnitTests` | 7097 passed, 0 failed, 8 skipped (PostgreSQL proofs, next rows). |
| Gateway suite, the trigger's and the record's tests (parked) | `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~Trigger\|FullyQualifiedName~FactoryActivity"` | 8 passed, 0 failed. |
| Gateway suite, every PostgreSQL proof, including both regenerated migrations | `dotnet test src\CcDirector.Gateway.Tests --filter FullyQualifiedName~Postgres` against a private throwaway PostgreSQL from `scripts\pg-stats-proof-rig.ps1 -Instance tl3273` (run directly because another run held the machine-wide Gateway lock; the rig was taken down afterwards) | 46 passed, 0 failed, 4 not run - `GatewayDatabaseLivePostgresProofTests`, which need a configured real database and are skipped by every gate run. |
| Command-line tool | `pytest tests/` in `tools/cc-devthrottle`, pinned packages from `ci.yml` | 3528 passed, 3 skipped. Shipped-tools contract 52 passed. |

Not covered: an end-to-end run on a live test Director (the Director's 30-second pull and its check runner are
proven by unit tests with fakes, not on a running Director). `Core.Tests` not run (the default gate's build
compiles the Core change).
