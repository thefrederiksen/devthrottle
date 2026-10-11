# Pricing Change, B1 - proof

The per-tenant AI gate: the hosted Gateway checks each tenant's plan before every AI call it makes for that tenant.
Mission document: `devthrottle_internal`, `docs/missions/pricing-change-2026-10-11/MISSION.md`, section 5, Track B.

## The check

**Full `-Parked` gate:** run by the Tech Lead; the result is in the review record. The Developer's own run of
`.\scripts\test-local.ps1 -Parked` built cleanly (0 warnings, 0 errors). The session that ran it then stopped it, because the
machine was critically low on memory, before any test ran. It produced no test result and is not counted here.

**Targeted runs by the Developer, on this branch:**

```
dotnet build src/CcDirector.Gateway.UnitTests -c Debug
dotnet build src/CcDirector.Gateway.Tests -c Debug
```
Result: `Build succeeded.` (both projects, which also builds `CcDirector.Gateway`).

```
dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~TenantAiGateTests|FullyQualifiedName~TeamMentorWriterTests|FullyQualifiedName~TurnVerdictServiceTests|FullyQualifiedName~NarrationCallTests|FullyQualifiedName~SessionNamingServiceTests|FullyQualifiedName~TeamBillOnTheRequestPathTests|FullyQualifiedName~DictionarySuggestionDailySweepTests|FullyQualifiedName~HostedAiHttpTests"
```
Result: `Passed!  - Failed: 0, Passed: 212, Skipped: 0, Total: 212`

```
dotnet test src/CcDirector.Gateway.Tests --no-build --filter "FullyQualifiedName~HostedAiGateRouteTests|FullyQualifiedName~WingmanTtsStatusPassthroughTests|FullyQualifiedName~HostedTeamBillOverTheWireTests|FullyQualifiedName~HostedTeamSessionDirectorOneRuleTests|FullyQualifiedName~GatewayTranscriptionServiceTests|FullyQualifiedName~HostedWingmanTranscribeTenantRefusalTests"
```
Result: `Passed!  - Failed: 0, Passed: 45, Skipped: 0, Total: 45`

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

## What this proof does not cover

- The full parked suites: they will be run by the Tech Lead, as stated above.
- Production. The same matrix against production, with test accounts, is the mission's phase 5 and 6 work.
- The Free and Remote route tests use the plan read from a test database. The live website writes the entitlement rows; the `remote` tier string must match what Track A writes (`remote`).
