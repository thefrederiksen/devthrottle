# Tech Lead's check - the Cockpit Factory Agents area (pull request 3274)

Run by the Tech Lead (product track), 21 September 2026, on head `fe49728a8` (code head `0d608f9d3`), in a
worktree of its own, before merge. The review is `REVIEW.md` beside this file (Pi, GLM-5.3): three findings, all
accepted and fixed by the Developer (the index migration, the corrections truncation flag, the gate re-run at
the head), answered at the foot of each.

| What | Command | Result |
|---|---|---|
| Default gate | `.\scripts\test-local.ps1` | Every suite Completed except Launcher.Tests, 195 passed, 2 failed: the two restart-signal tests known red on clean main (issue 3242). |
| Gateway unit tests, whole project (parked) | `dotnet test src\CcDirector.Gateway.UnitTests` | 7150 passed, 0 failed, 8 skipped (PostgreSQL proofs, next rows). |
| Gateway suite, Factory and Trigger tests (parked) | `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~Trigger\|FullyQualifiedName~Factory"` | 13 passed, 0 failed. |
| Gateway suite, every PostgreSQL proof, including the new index migration | `dotnet test ... --filter FullyQualifiedName~Postgres` against a private throwaway PostgreSQL (`scripts\pg-stats-proof-rig.ps1 -Instance tl3274`, taken down afterwards) | 46 passed, 0 failed, 4 not run (the live-database proofs every gate run skips). |
| Cockpit | `npm test`, `npm run build` in `apps/cockpit` | 536 passed of 536; build clean. |
| Client library | `npm test` in `packages/client-core` | 1492 passed of 1492. |
| Mobile app (shares the session contract that changed) | `npm test`, `tsc --noEmit` in `apps/mobile` | 109 passed of 109; type check clean. |
| Screenshot QA | Read by the Tech Lead: screens 01, 03, 09, 13, 15, 17, 18 and 19 of the Developer's 20, against the design report and the failure cases | They show what the README says: both factories RED with the trigger's own sentence; empty checks collapsed into grey lines beside a BLOCKED row; the escalation gone after "I have handled it" and the asked item kept; the six-hour lock RED and its release by resume as an ALLOWED row; the chip on the two trigger-started sessions only; switch off, no rail item and a plain "Page not found". |

What this check does NOT cover: the Tech Lead did not re-shoot the screenshots (the Developer's rig is committed
in `rig/` and was torn down); the full Gateway integration suite and `Core.Tests` were not run; nothing ran against
a live Director starting a real trigger session - the six-hour lock's start was written into the rig's own row.

Noted for later, not blocking: on the rig the release row reads "owner (unknown)" because the rig had no sign-in; the
"Factory agent" chip's second line is cut short in a narrow session list; and the Developer found an older defect,
the Sessions page crashing the browser tab on an empty roster, red on origin/main too.
