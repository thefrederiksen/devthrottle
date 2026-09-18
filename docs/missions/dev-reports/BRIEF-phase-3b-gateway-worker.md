# Worker brief - phase 3b, the Gateway: one link that lands where you are, and the way back named for a human

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-gateway`, cut from `mission/dev-reports-p3b`.
Your worktree is yours alone. Commit and push as you go. NEVER merge anything to main, and never touch
`mission/dev-reports-p3b` itself - your Manager merges your branch in.

Read first:
- `docs/missions/dev-reports/HANDOFF-phase-3b.md` - what the owner saw.
- `src/CcDirector.Gateway/Mobile/MobileRedirect.cs` - the device routing that already exists. Use it.
- `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` and `src/CcDirector.Gateway/DevReports/`.
- Repository rule 7 in `CLAUDE.md`: the client is dumb, the Gateway owns every display verdict.

## THE WHY

The owner has to be told a report exists and then find it himself, and when he is in one there is nothing
naming the session it came from except internal identifiers. He wants ONE address that lands him inside the
report on whatever device he opened it on, and a way back that reads like a human wrote it.

## YOUR FILES - and only these

- `src/CcDirector.Gateway/` (the whole Gateway is yours this phase: `DevReports/`, `Api/DevReportEndpoints.cs`,
  `GatewayHost.cs`, a new file for the link route)
- the Gateway test projects (`src/CcDirector.Gateway.Tests`, `src/CcDirector.Gateway.UnitTests` - put each test
  where its neighbours live)

Do NOT edit anything under `packages/`, `apps/` or `tools/`. Other Workers hold those and you will collide.

## THE WORK

### C. `/r/<report id>` - one address, routed by device, landing INSIDE the report

A new Gateway route, `GET /r/{reportId}`:

- It resolves the report to its session in the caller's account, then 302s to the app for this device:
  - a phone User-Agent (`MobileRedirect.IsPhoneUserAgent`) goes to the phone's report screen:
    `/mobile/session/{sessionId}/reports/{reportId}`
  - anything else goes to the Cockpit's Reports tab with that report open. The Cockpit Worker is building
    that route as `/session/{sessionId}?tab=reports&report={reportId}` - use exactly that, and if you have
    reason to change it, tell your Manager rather than diverging quietly.
- Both identifiers must be URL-escaped into the path/query.
- A report that does not exist for this account is a 404 with a sentence, never a redirect to a guess.
- **It must be registered BEFORE `MobileRedirect.UseMobileRedirect`.** That middleware sends every phone
  HTML navigation to `/mobile/` and would eat the report. Prove the ordering with a test, because a later
  reorder would silently send every phone link to the mobile home screen.
- **Signed out: sign in, then land on the report.** An unauthenticated HTML navigation is already sent to
  `/signin?next=<requested route>` by `AuthMiddleware`. Check that `/r/{id}` goes through that gate and that
  `next` carries `/r/{id}` itself, so the round trip comes back here and THEN routes by device. Do not build
  a second sign-in path. If the existing gate does not cover this route, make it cover it - and say so.

### D. The way back, named for a human - the Gateway supplies the words

The report record every client reads (`Summary` / `Detail` in `DevReportEndpoints.cs`) carries two new
finished strings, and the clients render them verbatim:

- `sessionLabel` - `"<three-digit session number> <session name>"`, e.g. `121 devthrottle - tool not working on linux`
- `backLabel` - `"back to <three-digit session number> <session name>"`, e.g. `back to 121 devthrottle - tool not working on linux`

Fold them ONCE, in one place, in the Gateway - a small class beside the other DevReports folds, unit-tested
on its own. Rules for the fold, and they are the whole job:

- The number comes from the live roster (`SessionDto.Number`) when the session is on it, otherwise from the
  session history row (`SessionHistoryEntity.SessionNumber` / the `WorkHistorySessionDto`). The name comes the
  same way (`SessionDto.Name`, else `SessionName`).
- **NO INTERNAL IDENTIFIER ANYWHERE THE OWNER CAN SEE.** If the number is unknown, do not fall back to the
  session id - say what you honestly know. If the name is unknown, the label is the number alone. If neither is
  known, decide one plain sentence that is true ("back to the session") and use it everywhere; never a
  ten-character hex string.
- Whitespace collapsed, one line, and a name longer than a sensible ceiling is cut there (see `DevReportTitle`
  for the existing convention - reuse it rather than inventing a second one).
- The number is three digits because the allocator's band is 100-999. Do not zero-pad a number outside it;
  render what the Gateway holds.

`GatewayHost.cs` supplies the lookup the fold needs, the same way it supplies `DevReportSessionLiveness`
today - roster first (`PushedSessions.TryLocate`), then `_sessionHistory.Get`. Keep the fold itself pure so it
can be tested with no host.

## PROOF - this is the point of the phase, the owner asked for it

Gateway tests, each named `Method_Scenario_ExpectedResult`:

1. `/r/{id}` with a phone User-Agent 302s to the phone report screen with both identifiers in the path.
2. `/r/{id}` with a desktop User-Agent 302s to the Cockpit route with the report open.
3. The route answers BEFORE the mobile redirect: a phone navigation to `/r/{id}` does not land on `/mobile/`.
4. An unknown report id is a 404, not a redirect.
5. A report of another account is a 404 (it must not leak that it exists).
6. Signed out, an HTML navigation to `/r/{id}` reaches the sign-in gate with `next=/r/{id}`.
7. The fold: number and name from the roster; number and name from history when the session is gone; number
   with no name; neither known - and in every case the label contains no session identifier. Assert that
   explicitly: the finished strings must not contain the session id.
8. The report detail and the report list both carry `sessionLabel` and `backLabel`.

**REVERT EACH FIX AND WATCH THE TEST GO RED**, with the reported symptom, then restore and watch it green.
Note the repository law: `git checkout -- <file>` restores HEAD, so COMMIT your fix before any revert proof, and
never run the restore leg with `--no-build` - the binary is what the runner tests.

Run: `.\scripts\test-local.ps1` and
`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"`. The Gateway suites are PARKED out
of the default run, so a green default run says nothing about them - run the Gateway one explicitly and say in
your report that you did.

## DONE MEANS

Everything committed and pushed on `mission/dev-reports-p3b-gateway`, and a file
`docs/missions/dev-reports/WORKER-phase-3b-gateway.md` in your worktree holding: what you changed, the exact
shape of the two new fields, the test list, the revert evidence per test (the line reverted, the red message),
and what you did NOT prove. Then tell your Manager in ONE line - fleet messages truncate at the first newline.

Do not guess. If something is genuinely undecidable, ask your Manager - do not invent a product decision.
