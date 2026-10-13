# Pricing Change, B1 - proof

The per-tenant AI gate: the hosted Gateway checks each tenant's plan before every AI call it makes for that tenant.
Mission document: `devthrottle_internal`, `docs/missions/pricing-change-2026-10-11/MISSION.md`, section 5, Track B.

## The check

Everything below was run on 2026-10-13 in the worktree `pricing-b1-fixes`, on the branch after `origin/main` was
merged into it (merge commit `3a7912fc7`) and the review fixes were committed on top (`c849d4e3f`, `a79f0cbbe`). Each
command built before it ran; none used `--no-build`.

**The full release gate was not run by the Developer.** `.\scripts\test-local.ps1 -Parked` runs one at a time on this
machine and belongs to another seat. What is counted here is only what is listed here.

**The whole Gateway unit test project:**

```
dotnet test src/CcDirector.Gateway.UnitTests -c Debug
```
Result: `Passed!  - Failed:     0, Passed: 11283, Skipped:     0, Total: 11283, Duration: 6 m 6 s`

**In the host-bound Gateway test project, the classes this change added or touched:**

```
dotnet test src/CcDirector.Gateway.Tests -c Debug --filter "FullyQualifiedName~HostedAiGateRouteTests|FullyQualifiedName~HostedAiGateMenuCheckRouteTests|FullyQualifiedName~WingmanMenuGuardProofTests|FullyQualifiedName~WingmanTtsStatusPassthroughTests|FullyQualifiedName~HostedTeamBillOverTheWireTests|FullyQualifiedName~HostedTeamSessionDirectorOneRuleTests|FullyQualifiedName~GatewayTranscriptionServiceTests|FullyQualifiedName~HostedWingmanTranscribeTenantRefusalTests"
```
Result: `Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59, Duration: 1 m 3 s`

That is the six classes of the first proof (45 tests), the new `HostedAiGateMenuCheckRouteTests` (7), and
`WingmanMenuGuardProofTests` (7), the existing self-host proof of the menu guard, run because the guard's code changed.

No test here needs Postgres or Docker. Continuous integration was not started and not waited for.

### Each new test fails without its fix

**Review finding 1.** With the fix committed, the three product files were put back to the merge commit
(`git checkout 3a7912fc7 -- src/CcDirector.Gateway/Wingman/WaitingScreenReader.cs src/CcDirector.Gateway/Api/TunnelTurnVerdictAnswerChannel.cs src/CcDirector.Gateway/Api/GatewayEndpoints.cs`),
the test project was rebuilt, and `HostedAiGateMenuCheckRouteTests` was run:

```
Failed FreeAndRemote_PromptMenuGuard_MenuShapedScreen_Is402_AndTheModelIsNeverAsked
  expected 402, got 200: {"accepted":false, ... "error":"This session is waiting on a menu. ...","blockedByMenu":true, ...
Failed UnreadablePlan_PromptMenuGuard_MenuShapedScreen_Is503_AndNeverTheMenuClaim
  expected 503, got 200: {"accepted":false, ... "blockedByMenu":true, ...
Failed FreeAndRemote_VerdictAnswer_MenuShapedScreen_Is402_AndTheModelIsNeverAsked
  expected 402, got 409: {"accepted":false,"code":"menu-owns-screen","reason":"A menu owns the session's screen, ...
Failed UnreadablePlan_VerdictAnswer_MenuShapedScreen_Is503_AndNeverTheMenuClaim
  expected 503, got 409: {"accepted":false,"code":"menu-owns-screen", ...
Failed!  - Failed:     4, Passed:     3, Skipped:     0, Total:     7
```

That is the false menu claim the review described, on both routes. The three that stayed green are the Pro controls
and the composer test, which the old code also satisfied. The files were restored (`git checkout HEAD -- ...`), the
project rebuilt, and the same run answered `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7`.

**Review finding 3.** With the fix committed, `DictionarySuggestionDailySweep.cs` was changed back to the old
behaviour (the gate's ordinary logged question, and the skip line written on every tick), the unit test project was
rebuilt, and `DictionarySuggestionDailySweepTests` was run:

```
Failed Sweep_ATenantWithoutDictation_LogsTheSkipOnce_AndScansOnTheFirstTickAfterThePlanAllowsIt
  Assert.Single() Failure: The collection contained 6 items
Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6
```

Six lines for three ticks: one from the gate and one from the sweep on each. The file was restored, the project
rebuilt, and the same run answered `Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6`.

## What each new test proves

`src/CcDirector.Gateway.UnitTests/Tenancy/TenantAiGateTests.cs` (a real database, the real entitlement registry, the real trial ledger):
- `EntitlementScopes_Remote_GrantsTheHostedGatewayAndNoAiScope` - the `remote` tier grants hosted_gateway and none of dictation, text to speech or the Wingman.
- `ForTenant_TheFreePlan_IsRefusedEveryAiScope` - an account with no paid row and no trial (the free tier) is refused dictation, text to speech and the Wingman.
- `ForTenant_TheRemotePlan_IsRefusedEveryAiScope` - a live Remote subscription is refused all three AI scopes.
- `ForTenant_ProHostedALegacyNullTierAndARunningTrial_AreAllowedEveryAiScope` - Pro, legacy `hosted`, a legacy row with no tier, and a running trial are allowed all three.
- `ForRequest_AndForSession_InAPersonalTenant_AnswerTheAccountsPlan` - the request and session forms of the question give the account's own plan in a personal tenant.
- `ForTenant_ATenantWithNoAccount_AndTheSelfHostTenant_AreRefusedOnTheHostedGateway` - on the hosted Gateway, a call that names no account is not made.
- `ForTenant_AnUnreadableEntitlementTable_IsUnknown_NeverARefusalOrAGrant` - a failed plan read is Unknown: not a refusal and not a grant.
- `EnsureAllowedForTenant_Refused_ThrowsWithTheDecision_AndAllowedDoesNot` - the model-factory backstop refuses a Free tenant and lets a Pro tenant through.
- `SelfHost_NoEntitlementRegistry_AllowsEveryScope_ForAnyTenant` - with no entitlement registry (self-host, decision D5), every scope is allowed for any tenant.
- `FreeTenant_RefusedEveryAiScope_KeepsItsDeviceKeyAndItsHostedAccess` - after every AI refusal, the Free tenant's device key is still bound, the access lease still allows it, and nothing was revoked.
- `TenantAiRefusal_Refused_IsThe402SubscriptionRequiredContract_TheDirectorAndThePhoneBothRead` - the refusal is HTTP 402. Its body runs through `HostedAiErrorMapper.Map402` to `SubscriptionRequired`, and it carries the state, text and pricing page the phone reads.
- `TenantAiRefusal_Unknown_IsATemporary503WithRetryAfter_NeverA402` - a plan that could not be read answers 503 with Retry-After 10, not 402.
- `TenantAiRefusal_AnAllowedCall_HasNoRefusalToAnswer` - asking for the refusal of an allowed call is a programming error and throws.
- `TranscribeAsync_FreeTenant_AnswersSubscriptionRequired_AndNeverCallsTheProvider` - dictation for a Free tenant returns `subscription_required` and the speech provider receives no request.
- `TranscribeAsync_ProTenant_IsNotRefusedByThePlan` - the same dictation for a Pro tenant passes the gate.
- `TranscribeSegmentUncorrectedAsync_FreeTenant_RefusesWithoutCallingTheProvider` - a phone Notes segment for a Free tenant is refused before any provider call.

`src/CcDirector.Gateway.Tests/HostedAiGateRouteTests.cs` (hosted boundary, an authenticated device key bound to a real tenant, a real database):
- `FreeAndRemote_TextToSpeech_Are402_AndTheSpeechProviderIsNeverCalled` - `POST /wingman/tts` answers 402 for Free and Remote, and the speech provider is never called.
- `FreeAndRemote_TheWingman_Is402_AndTheModelIsNeverAsked` - `ask-direct` and `ask-devthrottle` answer 402 for Free and Remote, and the model is never asked.
- `FreeAndRemote_Dictation_Is402` - `POST /wingman/transcribe` and `POST /transcription/cleanup` answer 402 for Free and Remote.
- `Pro_TextToSpeech_ReachesTheProviderAndAnswers200` - for Pro, text to speech passes the gate and reaches the provider.
- `Pro_TheWingman_ReachesTheModel` - for Pro, the Wingman route passes the gate and reaches the model.

`src/CcDirector.Gateway.UnitTests/Teams/TeamBillOnTheRequestPathTests.cs` (the team path):
- `TenantAiGate_ATeamSeat_IsTheSeatsAnswerInThatTeam` - a paid seat on a paid team is allowed. The same person on an unpaid team, and a Collaborator, are refused. A stranger or an unnamed person is Unknown.
- `TenantAiGate_ForSession_InATeam_IsTheSessionOwnersSeat` - a team session is judged on its owner's seat, and a session that is nobody's is Unknown.
- `TenantAiGate_ForTenant_InATeam_IsTheTeamsBill` - a call that names no person in a team is judged on the team's bill.

Background jobs:
- `TeamMentorWriterTests.WriteWeekAsync_ATeamWhoseBillGrantsNothing_SkipsEveryMember_AndCallsNoModel` - the Mentor's weekly job skips a member whose seat has no Wingman, records why, and never calls the model.
- `TeamMentorWriterTests.WriteWeekAsync_ATeamWhoseBillGrants_WritesTheMembersBlock` - the control: on a paid team bill the Mentor asks the model and writes the block.
- `TurnVerdictServiceTests.WingmanNotOnThePlan_SkipsTheStopBeforeAnyReadOrModelCall_EvenForAVoiceSession` - the Wingman's judgement skips a stop not on the plan, or whose plan could not be read, before reading the screen or asking the judge.
- `NarrationCallTests.AnAccountWhosePlanLacksTheWingman_IsNotJudgedOrNarrated_AndNoModelIsCalled` - a Free stop gets no judgement, no narration and no model call (replaces the old rule, which still judged it).
- `NarrationCallTests.AnAccountWhosePlanCouldNotBeRead_IsNotJudged_AndIsNeverToldToUpgrade` - an unreadable plan makes no call and says nothing about the plan.
- `SessionNamingServiceTests.NameIfUnnamedAsync_WingmanNotOnThePlan_MakesNoNamingCall` - session naming makes no model call when the Wingman is not on the plan or the plan could not be read.

Review fixes (2026-10-13):

`src/CcDirector.Gateway.Tests/HostedAiGateMenuCheckRouteTests.cs` (hosted boundary, an authenticated device key bound to a real tenant, a real database, a Director that answers the screen read with a real menu-shaped screen and records every command):
- `FreeAndRemote_PromptMenuGuard_MenuShapedScreen_Is402_AndTheModelIsNeverAsked` - a prompt sent with the menu guard to a menu-shaped screen answers 402 `subscription_required` for Free and Remote, the model is never asked, and nothing is typed.
- `UnreadablePlan_PromptMenuGuard_MenuShapedScreen_Is503_AndNeverTheMenuClaim` - when the plan cannot be read, the same prompt answers 503 with Retry-After 10 and `entitlement_unknown`, and the body says nothing about a menu.
- `Pro_PromptMenuGuard_MenuShapedScreen_ReachesTheModel` - for Pro the guard asks the model, and the guard's own answer comes back, not the plan's.
- `Free_PromptMenuGuard_ScreenThatIsPlainlyNotAMenu_SendsThePrompt_WithNoModelAndNoRefusal` - a Free member whose screen is plainly not a menu has the prompt sent: the free classifier answered, no model was asked, and the plan refused nothing.
- `FreeAndRemote_VerdictAnswer_MenuShapedScreen_Is402_AndTheModelIsNeverAsked` - answering a reply verdict on a menu-shaped screen answers 402 for Free and Remote, the model is never asked, nothing is typed, and each refusal leaves one ledger line with the cause `not-on-plan`.
- `UnreadablePlan_VerdictAnswer_MenuShapedScreen_Is503_AndNeverTheMenuClaim` - when the plan cannot be read, the same answer is 503 with Retry-After, never the menu claim, and its ledger line says `plan-unknown`.
- `Pro_VerdictAnswer_MenuShapedScreen_ReachesTheModel` - for Pro the answer route asks the model, and the route's own menu refusal comes back, not the plan's.

`src/CcDirector.Gateway.UnitTests/DictionarySuggestionDailySweepTests.cs`:
- `Sweep_ATenantWithoutDictation_LogsTheSkipOnce_AndScansOnTheFirstTickAfterThePlanAllowsIt` - over three ticks on the free plan the sweep and the gate together write exactly one line about the tenant and call no model; on the first tick after the tenant has a Pro row the scan runs and one line says the skip is over.

## Review findings

The review is the comment of 2026-10-11 on pull request 3878. Each finding, and its answer:

**Finding 1 (medium): two menu-check call sites reached the model check without asking the plan, and a plan refusal
was answered as "a menu owns the screen". ACCEPTED and fixed.** The model-confirmed menu check
(`WaitingScreenReader`) now takes the plan question and asks it at the one point a model would be asked: after the
screen read and the pure classifier, before the verdict cache and the model. Both routes pass `ForRequest` for the
Wingman scope and answer a refusal with `HostedAiHttp.TenantAiRefusal`, so Free and Remote get 402
`subscription_required` and an unreadable plan gets 503 with Retry-After. This is where the `waiting-screen` route
already asks, and for the same reason: the classifier is free, so a Free member whose screen is plainly not a menu is
not refused a prompt they are allowed to send. On the answer route the refusal is recorded as one ledger line
(`not-on-plan` or `plan-unknown`). Nothing is typed in any of these cases, so a real menu is still never pressed
blind. Self-host is unchanged: with no entitlement registry the gate allows, and the check runs as before.

**Finding 2 (minor): a plan refusal spends the phone Notes job's five attempts, and the phone shows the exception's
text. ACCEPTED, NOT FIXED HERE; issue 3947.** A proper fix is a waiting state in the Core job state machine, a way
back into the queue when the plan changes, and the shared refusal notice on the phone and the Cockpit. That is three
layers and more than the hour the finding was given, and the server half alone would leave a recording that either
retries forever or waits with no way back. The pull request description no longer claims the member gets the
transcript by subscribing later; it states what happens. Issue 3947 says exactly what is left.

**Finding 3 (minor): the dictionary sweep logged a skip every ten minutes for every tenant without dictation.
ACCEPTED and fixed.** The sweep writes one line when a tenant's skip starts or its reason changes, and one when the
plan allows the scan again. The gate's own per-answer line was the other half of the noise, so the sweep asks through
`ForTenantOnATimer`, which gives the same answer without that line; a failed read is still logged. The plan is still
asked on every tick, because that is what makes the scan run on the first tick after the plan allows it. The memory
of what was logged is in the process, so after a Gateway restart each such tenant is logged once more.

## What this proof does not cover

- The full release gate (`.\scripts\test-local.ps1 -Parked`). The Developer did not run it. Of the parked suites, the whole Gateway unit test project was run; the host-bound Gateway test project was run only for the classes listed above; `Core.Tests` was not run.
- The web and Python tests. This change touches neither.
- The phone and the Cockpit were not driven. What they show on the new 402 and 503 from the two routes is read from the client code, not seen on a device.
- Production. The same matrix against production, with test accounts, is the mission's phase 5 and 6 work.
- The Free and Remote route tests use the plan read from a test database. The live website writes the entitlement rows; the `remote` tier string must match what Track A writes (`remote`).
