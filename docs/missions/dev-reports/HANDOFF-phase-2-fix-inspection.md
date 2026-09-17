# Handoff - phase 2 inspection fixes (pull request #3006)

Read `STATE.md` and `INSPECTION-phase-2.md`. Fix on `mission/dev-reports`. Small, tight changes; no new features.

## Architect rulings

1. **Medium 1 - FIX.** A client that disconnects must not strand a delivery. The prompt send must not run on the
   HTTP request's cancellation token: a drain that has claimed items runs to its end on a Gateway-lifetime token.
   If a send is cancelled or fails before the prompt left the Gateway, the claim is finished and the items go back
   to `held` (the existing `NeverLeft` outcome), never left in `sending`. Test: a drain whose caller cancels
   mid-send leaves no item in `sending`; revert and watch it red.
2. **Medium 2 - stays ACCEPTED** (already written in `PHASE-2-REPORT.md`).
3. **Low 1 - FIX the publish race.** A unique-index violation on publish is retried once as a new version of the
   existing report, never a raw 500. The duplicate item sequence number during a deploy swap is accepted; say so.
4. **Low 2 - ACCEPTED for version one.** Write in `PHASE-2-REPORT.md` that report versions have no retention rule yet.
5. **Low 3 - ACCEPTED.** The route needs a session key or the owner's credentials. Note it in the report.
6. **Low 4 - FIX.** Add `X-Content-Type-Options: nosniff` to the HTML route. Test the header.
7. **Low 5** - already fixed in the mission documents by the Architect.

Revert each code fix and watch its test fail, then restore. Focused dev-report tests, `.\scripts\test-local.ps1`,
and `Gateway.UnitTests` in full must be green. Then run the dev-report Gateway integration tests on the final commit:
`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"` (it queues for the machine-wide lock -
wait in the foreground). Do not attempt the whole of Gateway.Tests; the machine is short of memory. Update
`PHASE-2-REPORT.md`, commit and push, then ONE line to the Architect (session 184d1571). Sending that line is required.
