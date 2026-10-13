# Review: pull request 3878, the fixes made after the first review (Pricing change B1)

Reviewed at commit b902883ea, the head of pull request 3878, in a worktree detached at that commit. The range reviewed
is 3a7912fc7..b902883ea only: the three commits made after the first review (the menu check asks the plan first, the
dictionary sweep logs a plan skip once, and the proof file). Nothing before the merge of main was re-reviewed.

## Scope

What I read:

- The whole production diff in the range: GatewayEndpoints.cs (the prompt route's menu guard and the verdict answer
  handler), TunnelTurnVerdictAnswerChannel.cs, WaitingScreenReader.cs, TenantAiGate.cs and
  DictionarySuggestionDailySweep.cs.
- The whole test diff: the new HostedAiGateMenuCheckRouteTests.cs, the test added to
  DictionarySuggestionDailySweepTests.cs, and the signature change in TurnVerdictAnswerRouteExitTests.cs.
- The proof file docs/missions/pricing-change/b1-proof.md as changed in the range.
- Around the diff, for the questions asked: every remaining caller of WaitingScreenReader.ConfirmedMenuAsync and
  the plan check in front of each (GatewayWingmanVoiceEndpoint.cs); TurnVerdictAnswerService.AnswerAsync, to see
  where the thrown refusal goes and what the lock does; HostedAiHttp.TenantAiRefusal; GatewayRequestErrors.RecordRefusal;
  the construction of the sweep in GatewayHost.cs; the TenantId type; and the two client callers in client-core
  (sendVoicePrompt and answerTurnVerdict) with GatewayError.from.
- The specification: section 5 Track B item B1 and section 7 Gateway of the mission document, read from origin/main of
  devthrottle_internal.
- The first review, read from the comments on pull request 3878.

What I ran, each building first:

- dotnet test src/CcDirector.Gateway.Tests --filter HostedAiGateMenuCheckRouteTests: 7 passed, 0 failed.
- dotnet test src/CcDirector.Gateway.UnitTests --filter DictionarySuggestionDailySweepTests, TurnVerdictAnswerRouteExitTests
  and TenantAiGateTests: 32 passed, 0 failed.
- git status after both runs: clean. No tracked file was changed by this review.

What I could not reach:

- I did not run the fix-removed proof myself: it needs the three product files put back to the merge commit, which is an
  edit to tracked files and was out of bounds for this review. The claim that each new test goes red without its fix
  is the Developer's, taken from the proof file, and I checked it by reading instead (see question 3).
- I did not run the full Gateway unit test project or scripts/test-local.ps1, by instruction.
- I did not drive the phone or the Cockpit. What they show on the new answers is read from the client code.

## Question 1: finding 1, the two menu-check routes

**Answer: yes, both routes now answer the plan honestly, nothing is typed, and the self-hosted path is unchanged.**

How the fix works. WaitingScreenReader gained a second entry point, ConfirmedMenuOnThePlanAsync, which takes the plan
question as a function. The order inside it is: read the screen, run the free classifier, and only when the classifier
says "menu" ask the plan. A refused or unreadable plan comes back as a MenuConfirmation with IsMenu false and the
decision attached, before the verdict cache, before the translator null check, and before the model call and its
catch-all. So the catch-all that answered "a menu" for an unreachable model can no longer see a plan refusal on these two
routes: the refusal never reaches the model factory's backstop, because the plan was asked first.

POST /sessions/{sid}/prompt with menuGuard (GatewayEndpoints.cs, the menu guard block): on a plan refusal the route
writes a log line, records the claim as stopped before the Director with the new ClaimStopPlan reason, settles a typed
claim back as not pressed, and returns HostedAiHttp.TenantAiRefusal(decision). That helper answers 402 with the shared
body (state SubscriptionRequired, code subscription_required) for Refused, and the EntitlementUnknownResult (503,
Retry-After 10, code entitlement_unknown) for Unknown. The return happens before any of the three deliver calls, so
nothing is sent to the Director. The test confirms it: zero prompt commands reach the fake Director in the Free, Remote
and unreadable-plan cases.

POST /sessions/{sid}/turn-verdict/answer: the channel's MenuOwnsScreenAsync now calls the same entry point and throws
TenantAiRefusedException on a refusal. I checked where that exception goes. TurnVerdictAnswerService.AnswerAsync calls
MenuOwnsScreenAsync inside its per-session lock, after the screen hash check and before WriteAsync; it has no catch, and
its finally releases the lock. The route handler has a new catch for exactly this exception, placed before the general
catch, which records one ledger line through RefuseBeforeLookup with cause not-on-plan or plan-unknown and the matching
status, and returns the same TenantAiRefusal answer. Because the exception is thrown before WriteAsync, nothing was sent,
so recording it as a refusal (not as unconfirmed) is correct. The lock is released by the finally, so a later answer on
the same session is not blocked.

A Free member whose screen is plainly not a menu: the plan is asked only after the classifier says "menu". The test
Free_PromptMenuGuard_ScreenThatIsPlainlyNotAMenu_SendsThePrompt_WithNoModelAndNoRefusal drives a composer screen through
the real route for the Free tenant and sees 200, accepted, one prompt command sent, zero model calls. Answer: yes, the
prompt is still sent.

The other callers of ConfirmedMenuAsync. Two remain, both in GatewayWingmanVoiceEndpoint.cs. The waiting-screen route
calls PlanRefusal for the Wingman scope when the classifier says "menu", before it calls ConfirmedMenuAsync. The
voice-turn route calls PlanRefusal for the Wingman scope at its top, before ClassifyLiveScreenAtAsync, which is the only
caller of the second ConfirmedMenuAsync. So on both, a refused or unknown plan is answered 402 or 503 before the check
runs, and the catch-all in the old entry point can only swallow a refusal if the plan changes between two reads inside
one request. That window is milliseconds, it fails in the safe direction (nothing typed), and the first review did not
count these two routes as findings either. No change needed.

The self-hosted path. TenantAiGate.SelfHost has no entitlement registry, so ForRequest answers Allowed, and
ConfirmMenuAsync then runs exactly the old sequence (cache, translator, model, fail closed). The ConfirmedMenuAsync
wrapper passes askPlan null, which skips the plan branch entirely. The three exit tests on the self-host boundary were
updated only to pass SelfHost and still pass. Unchanged.

One observation, not a finding. The plan is now asked before the per-screen verdict cache is consulted. A tenant whose
plan lapsed after a cached "not a menu" verdict for the same screen used to have the prompt sent from the cache with no
model call, and now gets 402. That is consistent with the rule the code states, that the model's verdict is what is on
the plan, and a cached verdict is a model verdict. I note it so nobody reads it as a regression.

A second observation, not a finding. Both client callers (sendVoicePrompt and answerTurnVerdict) build a GatewayError
from a non-OK answer rather than parsing it with creditsErrorFrom, so the member sees the 402 body's error sentence and
its code, not the shared "View plans" notice that dictation shows. The answer is honest and the code is the one the
specification names; rendering it as the shared notice would be a client change outside this fix.

## Question 2: finding 3, the dictionary sweep

**Answer: yes, one line on the way into the refused state, one on the way out, the scan still runs on the first tick
after the plan allows it, and ForTenantOnATimer does not weaken the gate.**

DictionarySuggestionDailySweep keeps an in-process ConcurrentDictionary from TenantId (a record struct, so value
equality) to the last logged verdict. On a refused or unknown plan it writes the SKIPPED line only when the tenant is not
in the map or the verdict differs from the one stored, so Refused to Unknown and back is logged as a change, and a steady
state is logged once. The stored scan marker is still not moved, so the tenant stays due. On an allowed plan the tenant
is removed from the map, and if it was there a RESUMED line is written; the scan then runs on that tick. The sweep is one
instance for the Gateway's life (GatewayHost.cs field _suggestionDailySweep), so the map lives across ticks. After a
restart each refused tenant is logged once more, which the code says on purpose.

ForTenantOnATimer is ForTenant with logTheAnswer false. It reaches the same Decide method with the same arguments, so
the verdict, tier and reason are identical. The only thing skipped is the gate's own "answer" line. The "Decide FAILED"
line for a read that throws still uses the injected log and is still written on every tick during a fault, which is
right: a fault should stay loud. No scope, tenant or caller check was removed, and no other caller uses the new method.

## Question 3: do the new tests watch the production caller

**Answer: yes, both.**

HostedAiGateMenuCheckRouteTests maps the real routes with GatewayEndpoints.Map on a real WebApplication listening on a
loopback port, with the hosted tenant boundary, a real database holding entitlement rows for Remote and Pro and none for
Free, the real EntitlementRegistry and TenantRegistry behind the real TenantAiGate, the real TurnVerdictAnswerService,
and a WingmanTranslator whose model function counts calls and throws. The only fake is the Director command sender,
which is the tunnel seam: it answers screen-grid with a real menu-shaped grid (the picker's selection marker, cursor
hidden) and records every command. Requests go over HTTP as JSON. The reply verdict carries the hash of the same rows,
so the answer service passes its screen-changed check and reaches the menu check, which is the production path.

With the fix removed, the Free and Remote prompt test would get 200 with blockedByMenu true (the translator throws, the
catch-all answers "a menu"), the unreadable-plan prompt test the same, and the two answer tests 409 menu-owns-screen.
The proof file reports exactly those four failures from the Developer's fix-removed run. The Pro tests assert one model
call and the route's own answer, so a gate that refused everyone would also go red. The composer test asserts the prompt
was sent with zero model calls, so a fix that asked the plan before the classifier would go red. None of these hand-builds
the route's answer.

The sweep test constructs the real DictionarySuggestionDailySweep with the real gate, registry and stores on a real
database, calls SweepAsync three times ten minutes apart, counts every log line mentioning the tenant from both the gate
and the sweep (the log is injected into both), then inserts a Pro entitlement row and sweeps once more. It asserts one
SKIPPED line, zero model calls, then one RESUMED line, one model call and a stored scan marker. With the old code the
count over three ticks is six (one gate line and one sweep line per tick), which is what the proof file reports. The
test does not hand-build the decision; it reads it through the sweep.

## Question 4: device key revocation, leases, tombstones

**Answer: nothing in the diff reaches them.** The range touches nine files, none in device, lease or tombstone code,
and a search of the production diff for revoke, lease and tombstone finds no hit. The prompt route's new exit calls the
same claim settlement helpers the existing menu exit calls; the answer route's new exit writes one ledger record. The
gate change is a logging flag. HostedAccessLeaseService and the device registry are not in the range.

## Findings

None. I looked for a way the plan refusal still reaches the model, a path where something is typed before the refusal, a
caller that still swallows the refusal, a weakening in the gate, a test that would stay green with the fix removed, and a
touch on keys or leases, and found none.

## Verdict

The three fixes do what the first review asked. Finding 1 is closed on both routes with the honest 402 or 503 and nothing
typed, finding 3 is closed with transition-only logging that keeps the first-tick resume, the tests exercise the real
routes and the real sweep, and nothing in the range reaches device keys, leases or tombstones. Finding 2 is deferred to
issue 3947 by the Developer and was not part of this range.
