# Review 3 - issue #4009, the save fix and the warning removal (commit e4e05bb9e)

Reviewer: session 5b10905e, Claude Code on Fable (Codex is not installed on devlinux). Recorded verbatim.

REVIEW of commit e4e05bb9e (origin/fix/cockpit-filter-save, issue #4009). Verdict: APPROVE, no findings.

SCOPE: read only, this one commit. I read the diff from origin, ran the named Gateway and Cockpit tests on the branch, and separately built the PARENT commit (e4e05bb9e^) in a throwaway tree with the new test file copied in to confirm the test fails before the fix. I did not review the rest of the filter feature.

1. ROOT CAUSE - correct. MapPost has two overloads, one taking a RequestDelegate and one taking a Delegate. The old save lambda, async with one HttpContext parameter and an await expression body, converts to RequestDelegate (an await is a valid statement body for a Task-returning delegate), and C# prefers the RequestDelegate overload because it is the more specific target. ASP.NET then invokes the lambda directly and never writes the IResult, so the store was written and the answer was 200 with nothing in it. The commit's explanation matches the language rule.

2. ALL FOUR ROUTES - the fix holds. Only the save route was actually broken: the list and delete lambdas return IResult (not Task), so they could never bind as RequestDelegate, and the update lambda has two parameters, so it could not either. Giving all four an explicit return type is harmless for the three that were fine and makes the save route unable to bind the wrong way. Consistent and correct.

3. THE NEW TEST PROVES IT. SavedSessionFilterRoutesThroughThePipelineTests pulls the endpoint out of the route table the mapping produced and invokes its RequestDelegate, which is exactly the delegate the server would run, so it sees the discarded result. Proof: on the parent commit with the test copied in, all three tests FAIL, and the save test fails with the message 'the save answered 200 with an EMPTY body - the Cockpit cannot read it'. On the branch: dotnet test with filter SessionFilters = 35 passed, 0 failed. The test uses the existing throwaway SQLite harness and no server, port, clock or sleep, so it meets the gate standard.

4. THE REMOVAL IS COMPLETE AND CLEAN. outsideNeedingYou, the roster-filter-outside button, sessionLabel, the CSS block and the three client-core tests are all gone; the roster test now asserts the line is absent even when a session outside the filter needs you. A grep over the whole branch finds no remaining use of outsideNeedingYou, roster-filter-outside or 'outside this filter' except the historical review record docs/qa/cockpit-session-filter/review-1.md, which is a record of a past review and should stay as it is. No design document still requires the line. classify and machineCanBeActedOn imports were dropped from filter.ts correctly. The CSS variables the removed block used are still used elsewhere. Cockpit: vitest rosterFilter.test.tsx = 26 passed. client-core: filterAddress.test.ts = 33 passed.

OUTSIDE SCOPE, for a follow-up issue, not a finding against this commit: src/CcDirector.Gateway/Api/FactoryEmailEndpoints.cs line 43 maps PUT /gateway/factory-email with the identical shape, 'async (HttpContext ctx) => await PutSwitchAsync(...)', and PutSwitchAsync returns Task<IResult>. By the same rule its answers (the switch body, the 400 refusal and the no-account refusal) are discarded and the caller gets 200 with an empty body. I have not run it; it is the same defect by inspection and deserves the same fix and the same kind of test.

I edited nothing, committed nothing, pushed nothing. The temporary parent-commit build tree was removed.
