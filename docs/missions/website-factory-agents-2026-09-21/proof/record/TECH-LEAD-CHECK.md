# Tech Lead's check - the activity record (pull request 3272)

Run by the Tech Lead (product track), 21 September 2026, on head `f47a925a7`, in a worktree of its own
(never the Developer's), before merge. The review is `REVIEW.md` beside this file (Pi, GLM-5.3 - Codex was out
of usage), one finding, accepted and fixed by the Developer in `f47a925a7`.

| What | Command | Result |
|---|---|---|
| Default gate | `.\scripts\test-local.ps1` | 8 of 9 suites Completed. Launcher.Tests 195 passed, 2 failed: the two restart-signal tests, which read a machine-wide signal a live launcher on this machine has armed. They are red on clean main too (issue 3242); this pull request changes nothing in the Launcher. |
| Gateway unit tests, whole project (parked) | `dotnet test src\CcDirector.Gateway.UnitTests` | 6998 passed, 0 failed, 8 skipped (PostgreSQL proofs, run in the next row). |
| Gateway suite, the record's routes (parked) | `dotnet test src\CcDirector.Gateway.Tests --filter FullyQualifiedName~FactoryActivity` | 3 passed, 0 failed. |
| Gateway suite, every PostgreSQL proof, on a throwaway PostgreSQL | `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres"` | 46 passed, 0 failed, 4 not run. The two migration-order tests this pull request moved both ran and passed. The 4 not run are `GatewayDatabaseLivePostgresProofTests`, which need a configured real database and are skipped by every gate run. |
| Command-line tool | `pytest tests/` in `tools/cc-devthrottle`, in a virtual environment with the pins from `ci.yml` | 3483 passed, 3 skipped. Shipped-tools contract 52 passed. |

Not run: `Core.Tests` (the change adds one configuration class to Core; the default gate's build compiles it).
An earlier run of the Gateway unit tests had one failure in a Wingman voice test (a disposed SQLite handle); it
passed three runs out of three on its own and in the full run above, and this change does not touch it.
