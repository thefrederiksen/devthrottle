# Session Rules part 2, phase 3 - review: the Gateway fold and the three Rules page routes

Reviewer seat "Session Rules - Reviewer - phase 3 Gateway", 2026-10-20, on devlinux. Reviewed
`git diff 3fbccbdea..6c136491b` on branch `mission/session-rules-hooks-p3-gateway` (one commit, six files, 1,448 lines
added, nothing removed). The reviewer wrote none of this and fixed nothing. The Developer's report ("822 passed") was
treated as testimony: the suite was built and run here, and every claim below was checked against the code or by
running it.

## Scope

**Read, in full:** `phase-3-plan.md` (rulings T1-T10, wire P1-P3); `phase-1-plan.md` P1-P6, P13, P18 and W1-W2;
`src/CcDirector.Gateway/Rules/RulesPageFold.cs` (679 lines); the route and wire additions in
`SessionRuleEndpoints.cs` and `SessionRuleWire.cs`; the project file change; both new test files;
`src/CcDirector.RuleHook/RuleResolver.cs` and `RulesFile.cs`; the hook's `HookRunner.cs` where it calls the
resolver; `SessionRuleStore` (`All`, `EffectiveHookRules`, `FiringsFor`, `History`, `Delete`, `RecordHookFirings`);
`RuleEvaluator` where a screen firing is completed; `SessionKeyGuard.Check` and `IsRuleRoute`;
`GatewayDbContext.ApplyTenantScope`; the Director's `SessionRulesMaintainer` and `SessionRulesFile`.

**Ran:** `dotnet build src/CcDirector.Gateway.UnitTests` (clean, 0 warnings);
`dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` - **822 passed, 0 failed, 0
skipped, 47 seconds**, which reproduces the Developer's number and includes `RulesTypeNothingGuardTests`,
`RulesPromotionBoundaryGuardTests` and the phase 2 guards; `dotnet test src/CcDirector.RuleHook.Tests` - 70 passed.

**Could not reach:** no real Gateway was driven and no browser client exists yet (seat B builds it), so the JSON
was checked at the `TestServer` boundary only. Tenancy was read from the code and the existing phase 2 proof; it was
not tried live with two accounts. `origin/main` moved two commits past the branch base (Factory Boxes #4280 and
#4287) while this review ran; neither touches the rules, the guard or the hook, so the branch needs a rebase but
nothing in it is contradicted.

## Verdict

**One finding, low.** A label on a rule's page reads as a dry run for a firing that was live. It is one line to
fix and one test to add, and it does not block the phase: the outcome text beside it still tells the truth. Everything
the sharp questions asked about - the shared resolver, the standing override, the viewer's day, the wire shapes,
tenancy, session keys - holds up, with the gaps named under "Observations".

## The sharp questions, answered

| Question | Answer |
|---|---|
| 1. Does the preview with a command go through the hook's own `RuleResolver.Decide` (T6)? Could the two drift? | **Yes, and not that I can find.** `RulesPageFold.Decide` builds a `RulesFile` and calls `RuleResolver.Validate` then `RuleResolver.Decide` - the hook's own source, linked into the Gateway by the project file; `HookRunner.cs` lines 117-120 call exactly the same two methods on the same shapes. The rules go in **in the Director's file order**: `All()` is newest first, then a stable `OrderByDescending(Rank)` - the same two sorts `EffectiveHookRules` applies for W1, and the Director writes the W1 list as received. The `state` is the same W1 wire name. The test `Preview_SampleCommand_DecidesExactlyAsTheHooksOwnResolverDoes` builds the file from the real `EffectiveHookRules` answer and compares every id, which is the right comparison. One untested corner is in observation 2. |
| 2. Is T7 exactly as written? | **Yes.** `Overrides(a, b)`: both hook rules, same event and tool (ordinal), results differ, `a.Rank > b.Rank` or same rank with a `deny` and b `allow`, and `textA.Contains(textB)` - A's text contains B's, so every command A matches on that text B matches too. The direction is right and is pinned by `Overrides_MoreSpecificRuleWhoseTextDoesNotContainTheOthers_IsNotAStandingOverride`: with the direction reversed, `"rm -rf build".Contains("rm")` would be true and that test would fail. Same-rank allow over deny is asserted false; same result at any rank is asserted false. Screen rules are excluded before the comparison and get their own verdict text. |
| 3. Fired today in the viewer's zone (T3, T4)? | **Right, including the boundary.** `DayOf` forces `DateTimeKind.Utc` and converts with the sent zone; `ReadZone` refuses null, blank and unknown with a stated reason and never falls back. `ReadZone` runs before the store is read, so a bad zone never costs a query. The test with a firing at 23:30 UTC on the 19th shows it on the 20th in Copenhagen and not in UTC, so a silent UTC fallback would go red. "Fired today" counts screen and hook firings alike (`FiringsFor` does not filter by kind). "Changed by agents" is any history row with a session id. A deleted rule is a hard delete, so `All()` no longer returns it and it is on no tab; its firings and history survive for `rules history`. |
| 4. Does every wire label arrive, in the P1-P3 shape? Must a client decide a meaning? | **Every field arrives, in the wire's names.** The three records match P1, P2 and P3 field for field and in order; `fields` has the seven labelled rows in the example order; the 400 and 404 bodies are `{ "error": ... }` like the other rule routes. `Results.Json` uses the web defaults (camelCase) and the host sets no other naming policy, so the empty test host and production serialise the same way. A client decides only layout: `changedLabel` and a screen firing's `overrodeLabel` are blank by contract, and `decision` is null when no command was sent. Two example strings differ in case from the wire text (observation 5); they are finished strings either way. |
| 5. Where could a constant be substituted and the suite stay green? Which test would not notice a wrong answer? | `ScreenSubjectMaxLength` (1000) and `BottomOf`: no test - 10, or cutting the TOP of the screen, passes. The whole screen-firing branch of `Firing` and `DecisionOf` (five of six decision labels, the "Read from the session's screen" place, the cut subject) has no test at all - finding 1 lives there. The `d MMMM yyyy` branch of `WhenLabel`, the `Live` mode texts and `LiveTone` have no test. The file ORDER fed to the resolver is not exercised: no test has two matching rules of the same rank and result, so feeding the rules in any order would stay green (observation 2). |
| 6. Tenancy; off session keys (T2)? | **Same scoping as every rule route; refused to session keys.** The three routes read through the same `SessionRuleStore` and the same `CreateContext()`, whose global query filter is `TenantId == ActiveTenant` on rules, firings and changes alike (`ApplyTenantScope`). `RulePageFor` looks the id up inside the tenant's `All()` list, so a foreign id is a 404 without a query by id; an overridden id from another account can only ever print as "(deleted rule ...)". `IsRuleRoute` was not changed and refuses all three shapes - `page` and `preview` are not Guids in the third segment, and `/{id}/page` is neither `firings` nor `promote` - and `SessionKeyGuard_TheRulesPageRoutes_AreNotOpenToASessionKey` pins it. |
| 7. House rules? | Entry and exit are logged on `ReadZone`, `Page`, `RulePageFor`, `Preview`, `ReadPageSource` and each route; no fallbacks - every unknown kind, place, state, action or decision throws; the only try-catch is in the three route handlers, and it catches only `RuleRejectedException`. The command text is never logged, only "given"/"none" - good. `Overrides` is public and does not log (observation 4). |

## Findings

### 1. A live screen rule whose keystroke was not sent is labelled "Would have acted" - the dry-run words (low)

**Where.** `RulesPageFold.DecisionOf`, `RulesPageFold.cs` line 486: for a firing whose decision is `act`, the label
is "Would have acted" when `TypedText` is empty and "Typed into the session" otherwise. The fold reads "nothing was
typed" as "this was a dry run".

**The input that breaks it.** A screen rule that has been promoted to live (screen rules can be; only hook promotion
is refused), fires, and the send does not land. `RuleEvaluator.cs` lines 419-432: for `RuleSendOutcomes.NotSent` the
firing is completed with `typed = ""` and the outcome "nothing was typed: ..."; for an unconfirmed send, with
`typed = ""` and the unconfirmed outcome; and a firing whose intent row was written but never completed (the process
died between the record and the keystroke, which the evaluator's own comment describes) also has an empty
`TypedText`. In all three the rule was live and the Gateway tried to act.

**What breaks.** The firing's `decisionLabel` says "Would have acted" with the neutral tone - the phrase every other
part of this page uses for a dry run - while its `outcomeLabel` on the same row says "Nothing was typed: ..." or
"About to type into the session: ...". A person reading the record of a live rule is told, in the verdict column, that
it was a rehearsal. This is the shape ruling T1 exists to prevent: the Gateway computed a verdict that is plausible
rather than true, and the client will render it faithfully. The dry-run case is distinguishable - its outcome begins
"dry run:" (`RuleEvaluator.cs` line 371) and a hook firing's begins "dry run: recorded" - so the label can be made
true without a schema change, though the cleaner fix is to record the rule's state on the firing.

**Proof.** Read from the source as cited; no test covers any screen firing on the rule page, so none was there to
go red. Not reproduced against a running evaluator (that path needs a model call).

## Observations (not findings - no harm proven)

1. **The page reads every firing of every rule, screen text included, on every load.** `ReadPageSource` calls
   `FiringsFor` once per rule with no limit and `History(null, null, null, int.MaxValue)`. The list page needs only a
   count of today's firings and the newest time, but it materialises every row - a screen firing carries the whole
   screen it read. A hook rule fires on every matched shell command, so the record grows by the day; the cost of the
   page grows with it and nothing bounds it. Not a wrong answer today, and ruling T2 asked for "a fold over the
   existing store", but it will be the first thing to slow down. Worth a count query or a per-rule limit before the
   firing table is large.

2. **The deciding rule between two rules created in the same tick is not proven to match the hook.** The preview and
   W1 both sort newest first and then by rank with stable sorts, so they agree whenever `CreatedUtc` differs. Two
   rules with the same `CreatedUtc`, the same rank and the same result that both match a command are ordered by the
   database, through two differently shaped queries (`All()` has no `Kind` filter; `EffectiveHookRules` does). The
   decision and the overridden set are the same either way; only `decidingRuleId` could differ. Nothing creates two
   rules in one tick outside a test, and no test exercises order at all (question 5). Stated so it is known, not
   because it needs work now.

3. **Ruling T7 is pairwise; a third rule can still take the decision.** The code implements the ruling exactly. The
   ruling's "always true" holds between the two rules named: a rank 4 rule that also matches decides instead of A,
   and then A has not "overridden" B in the P13 sense for that command. This is the Tech Lead's statement to keep or
   sharpen, not a defect in the code.

4. **`Overrides` is public and does not log.** It is a pure predicate called in a square loop over the reaching
   rules; logging it would flood the file, which is presumably why it does not. If it is public only so the tests can
   reach it, `internal` with the existing `InternalsVisibleTo` would meet the rule without the noise.

5. **Two labels differ in case from the wire examples.** `changedLabel` reads "Changed by Session 8a1e... (claude_code)
   at 09:15" where P1 shows "session" in lower case; a firing's `sessionLabel` likewise. And the `changedLabel` says
   "Changed by" when the agent's newest row was a `create` or a `delete`. All are finished strings a client renders
   verbatim, so nothing breaks; noted for seat B's fakes and for the Tech Lead's taste.

6. **`ReadZone` accepts Windows zone names and any file the runtime can parse as a zone**, because
   `TryFindSystemTimeZoneById` does. A browser sends an IANA name, and a zone that resolves is still the viewer's
   zone, so this is not a fallback. It would only matter if the route were ever read by something that guesses.

7. **Test gaps, listed so they can be filled:** no screen firing on the rule page (all six `DecisionOf` branches but
   the two hook ones, `BottomOf`, the screen `whereLabel`); no `Live` rule anywhere (`LiveTone`, both live
   `ModeDescription` texts); no date outside the current year; no rule with two match texts through `Overrides` or
   `HandlerLabel`'s "or" join; nothing holds `ScreenSubjectMaxLength`.

## What the phase 3 check asked for, as run here

- Gateway: `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` - green, 822 tests,
  `RulesTypeNothingGuardTests` and the phase 2 guards included.
- `src/CcDirector.RuleHook.Tests` - green, 70 tests.
- Web and the QA run are seats B and C, not this review.
