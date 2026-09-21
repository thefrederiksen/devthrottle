# Tech Lead's check - the trigger's live check and its two fixes (pull request 3281)

Run by the Tech Lead (product track), 21 September 2026, before merge. Code checked at `0503248c6`; the only later
commit, `bf52daa54`, adds `RUNBOOK-END-TO-END.md` and nothing else. Review: `REVIEW-LIVE-FIX.md` beside this file
(Pi, GLM-5.3), one finding (adoption re-derived the session name from facts the owner can change), accepted and
fixed in `f0ccfeb82` with a rename test that fails without the fix.

## What the live check found

Two paths by which one trigger could have two live sessions, both invisible to the unit tests, both only on a real
Director: (1) a start slower than the Director's 10-second report timeout was cancelled mid-start, recorded as
failed, and the lock never taken; (2) a start slower than the Gateway's own 30-second spawn wait had an UNKNOWN
outcome and released the lock. Fixed: the report answers at once, the lock is taken before the spawn, the spawn runs
on the Gateway's lifetime token, an unknown outcome keeps the lock and adopts the session by its stored start name,
a definite failure releases the lock and goes RED.

## Read live by the Tech Lead, from the isolated stack (Gateway `127.0.0.1:7898`, Director slot 21)

`cc-devthrottle trigger list` and `trigger runs "Website mail - stub count 1"` against that Gateway, 21 September
around 20:55 UTC:

- "Website mail - empty record": OK, checking every minute, last check `nothing-to-do`.
- "Website mail - broken check": RED "check failed: exit code 1: The system cannot find the path specified."
- "Website mail - stub count 1": 20:50:30 `failed` "the session start outcome is not known ... The lock is held
  until a session named '...' shows up, or for 5 minutes"; 20:52:00 `started` session `f66d3fee` (the record's
  row: "Found session f66d3fee..., named ..."); 20:52:00 and 20:53:30 `skipped-running` on that session. One
  session. Before it, ten `paused` checks in a row while the trigger was paused.

## Tests

| What | Result |
|---|---|
| `.\scripts\test-local.ps1` at `0503248c6` | Every suite Completed except the 2 Launcher restart-signal tests known red on clean main (issue 3242). |
| `dotnet test src\CcDirector.Gateway.UnitTests` at `0503248c6` | 7168 passed, 0 failed, 8 skipped (PostgreSQL, next row). |
| Gateway.Tests, `Trigger`, `Factory`, `Spawner` | 14 passed, 0 failed. |
| Gateway.Tests, `Postgres` and `Migration`, on a private throwaway PostgreSQL (`pg-stats-proof-rig.ps1 -Instance tl3281`, taken down) | 47 passed, 0 failed, 4 not run (the live-database proofs every gate run skips). Covers the new `AddTriggerStartName` migration. |

## Not covered

The end-to-end reply (a real reply starting a real Front Desk session that leaves a draft) was NOT run: no test
address has a business thread, and making one needs a real Site Checker verdict, which needs Codex. The Delivery
Lead runs it from `RUNBOOK-END-TO-END.md` once Codex is back.
