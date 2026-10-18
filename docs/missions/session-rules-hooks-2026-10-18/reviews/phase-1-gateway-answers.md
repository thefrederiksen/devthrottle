# Phase 1 Gateway review - the Developer's answers

Developer A (Gateway), 2026-10-18, answering `reviews/phase-1-gateway-review.md` on
`mission/session-rules-hooks-p1` and the Tech Lead's calls at its end. Every test named here was written
first and seen to fail, except where it says the behaviour already held.

Check after the fixes: `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests --filter
"FullyQualifiedName~Rules|FullyQualifiedName~SessionRuleKinds"` - 544 passed, 0 failed, 0 skipped.

## Findings

**Finding 1 - a database refusal of one decision fails the whole post. ACCEPTED, fixed.**
`SessionRuleRecordRules.CheckHookFiring` now checks every free-text value against its column before the
save, naming the field and the limit: `decisionId` 64, `sessionId` 64, `machine` 256, `repo` 512, `agent`
64, `agentEvent` 64, `agentTool` 64, `subject` 4000. The values the event, tool and decision columns hold are
already limited to fixed words, so they cannot be too long. A too-long value is now a `RuleRejectedException`,
refused by id like any other, on both providers. Tests, one per length, at the limit and one past it:
`CheckFiring_HookValueOneLongerThanItsColumn_IsRefusedNamingTheField` and
`CheckFiring_HookValueExactlyItsColumnLength_IsAccepted` (eight cases each). The second half matters: a gate
that refused every length would pass the first test alone.

**Finding 2 - a hook body's cooldown and daily cap were dropped silently. ACCEPTED, fixed.**
`RefuseScreenFieldsOnAHookRule` refuses `cooldownSeconds` and `dailyCap` when they are present, with a reason
naming the field. Test: added to `CreateFromBody_HookBodyWithScreenOnlyFields_IsRefused`.

## Notes

**N1 - no Gateway-side cap on a post. ACCEPTED, fixed as ruling P17 decided.**
My first fix refused, by id, each decision past 100, and these answers pointed out the cost. By W5 the
Director deletes a decision refused by id, so a decision past the cap would be lost. The Tech Lead's ruling
P17 settled it. The limit is now ONE constant, `HookFiringLimits.MaxDecisionsPerPost` (100), in
`CcDirector.Gateway.Contracts`. The Director's forwarder reads it for `MaxBatch`, and the Gateway reads it for
its limit. `SessionRuleWire.ReadHookDecisions` refuses a post over the limit WHOLE, as a 400 that states the
limit and the count, and records nothing from it. The Director's forwarder already treats any non-success
answer as a failed post: it keeps every drop and backs off. So nothing is lost to the limit. Tests:
`ReadHookDecisions_PostOverTheSharedLimit_IsRefusedWholeNamingTheLimit`,
`ReadHookDecisions_PostOfExactlyTheSharedLimit_IsReadWhole`, and on the Director side
`HookDecisionForwarderTests.MaxBatch_IsTheOneSharedLimitTheGatewayHolds`.

**N2 - tenant separation of the rule lookup had no presence test. ACCEPTED, tests added.**
`EffectiveHookRulesAndRecordHookFirings_AnotherAccountsRule_IsNeitherListedNorFiredOn`. Account A first sees
its own rule, so an empty answer for B is not just a broken query. Then W1 from account B does not list A's
rule, and W5 from B is refused for it, naming the rule id, with no firing written. The behaviour already held
through the global filter, so this test passed when first run: it pins existing behaviour rather than
showing a fix.

**N3 - the unused `nowUtc` parameter. ACCEPTED, removed.** `RecordHookFirings(decisions)` takes no time. A
firing's time is the hook's `occurredUtc`, as W5 says. The route and the tests are updated.

**N4 - `GitRemoteIdentity` on local paths, and lower case for every host. KEPT AS BUILT,** per the Tech
Lead's call. P6 chose lower case for every host. Local paths come out the same however often they are
normalised, so a rule written with one still matches the Director's computation.

**N5 - a blank `kind` is refused. KEPT AS BUILT,** per the Tech Lead's call. A body sends a kind or leaves
it out.

**N6 - a decision whose rule was deleted is lost. KEPT AS BUILT for phase 1,** per the Tech Lead's call, and
recorded for phase 3.

**N7 - the Gateway does not cross-check a decision against its rule. KEPT AS BUILT for phase 1,** per the
Tech Lead's call, and recorded for phase 3.

## Item 8, from the hook review (its note N4)

**The write gate refuses a match text that is empty or only whitespace. ACCEPTED - this already held, and the
test now covers the gate itself.** `CheckHookRule` already refused a blank or whitespace-only entry and an
empty list. `CreateHook_NoMatchTextOrABlankMatch_IsRefused` already proved it through the store. Three cases
are now added to `SaveChanges_HookRuleWrittenRoundTheStore_MeetsTheSameKindAwareGate`: an empty match text, a
match text of spaces and a tab, and no match text at all. Each writes straight through the data context and
is refused by the gate with a reason. These passed when first run, because the behaviour already held.
