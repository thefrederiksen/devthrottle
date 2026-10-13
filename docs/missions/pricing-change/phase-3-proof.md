# Pricing change, phase 3 (Gateway): proof

Branch `pricing-change/phase-3`. Everything below was run on this machine, with a build, on the code at
commit `23996f7e8` (this document is the only thing committed after it). Origin/main was merged in at
`901f6396c`, which brought the fix for the real clock guard (`319cf5adc`).

## What was built

- **B2, the split.** A new scope, `remote_access`, in `EntitlementScopes`. Remote, Pro, team, the retired
  `hosted` plan and the pre-column row carry it. Free and `pro_selfhost` do not. `hosted_gateway` now
  means a Director only.
- **One rule, asked in two places.** `RemoteAccessRule.Decide` answers "may this account use the Cockpit
  and the phone app" both where a Cockpit or phone key is minted (`HostedEnrollmentEndpoint`) and when
  the access lease is read for a request (`HostedAccessLeaseService`, asked by `AuthMiddleware`). It has
  three answers: granted, not granted, not known.
- **A refusal refuses the request and nothing else.** No device key is revoked, no tombstone is written,
  and the positive lease is kept, so the same account's Director is served on the same tenant.
- **The Director door refuses the device types `phone` and `browser`** with 400 and the code
  `device_type_not_allowed`. Moving a key (`Move`) refuses a browser or phone key the same way.
- **The refusal body** is 402 with `{ "error", "code": "remote_plan_required", "message", "subscribeUrl" }`,
  the same at the mint and on a request. Field names and the code are unchanged from the mandate.
- **B3, no new trials.** Enrolment no longer grants a trial. A running trial is still honoured. Nothing
  is deleted: the `account_trials` table, its rows and the admin trial routes are untouched.
- **B4, the pre-launch grace.** Two settings, read once at startup and only by a hosted Gateway.
- **Teams** (Tech Lead ruling of 13 October 2026, confirmed by the Delivery Lead). See its own heading.

## The two settings

Both are `ENV` lines in the repository's `Dockerfile`, beside `CC_GATEWAY_HOSTED=1`, because the deploy
writes no application setting of its own (Tech Lead note of 13 October 2026, which replaced the mandate's
"no date in committed configuration" for the launch instant only).

| Setting | Committed value | Meaning when absent |
|---|---|---|
| `CC_GATEWAY_PRICING_LAUNCH_UTC` | `2026-10-13T20:32:00Z` (given by the Delivery Lead: the moment the new prices went live) | every account counts as created before launch |
| `CC_GATEWAY_REMOTE_ACCESS_GRACE_CUTOFF_UTC` | not stated | the grace does not end |

Form: one instant in Coordinated Universal Time, `yyyy-MM-ddTHH:mm:ssZ`. Example of the line the owner
adds to end the grace: `ENV CC_GATEWAY_REMOTE_ACCESS_GRACE_CUTOFF_UTC=2027-01-31T00:00:00Z` (an example
of the form only, not a proposed date). A value that is present and cannot be read stops the Gateway at
startup with an error naming the setting. A self-hosted Gateway reads neither.

## Commands and results

| Command | Result line |
|---|---|
| `dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj --nologo -v q` (the whole project) | `Passed!  - Failed: 0, Passed: 11575, Skipped: 0, Total: 11575, Duration: 7 m 57 s` |
| `dotnet test src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Enroll\|FullyQualifiedName~Lease\|FullyQualifiedName~AuthMiddleware\|FullyQualifiedName~HostedAiGateRouteTests\|FullyQualifiedName~HostedProTrialTests"` | `Passed!  - Failed: 0, Passed: 165, Skipped: 0, Total: 165, Duration: 3 m 36 s` |
| `.\scripts\test-local.ps1` (the default run, no `-Parked`, no `-Gateway`) | `RESULT: GREEN at commit 23996f7e87491135895f52c5161e30276d5557ec`; ten suites, every one `outcome=Completed`: 1686, 1079, 86, 89, 218, 82, 305, 39, 870 and 74 tests. The tree is reported dirty only for the two untracked mandate notes. |

The second command covers `HostedAiGateRouteTests`, `HostedMobileAccountEnrollTests`, `HostedProTrialTests`
and every class in that project whose name holds `Enroll`, `Lease` or `AuthMiddleware`. A wider run of
neighbouring classes there (names holding `HostedTeam`, `Trial`, `Entitlement`, `Cockpit`, `Browser`,
`Mobile`, and three that post to the Director door) passed 447 of 447 one commit earlier, before the
Dockerfile change, and was not repeated.

The default run covers the guard `GateTestsDoNotDependOnTheMachine`. Every new test uses fixed dates.
One baseline line was removed, as the guard asks, because `HostedMobileAccountEnrollTests` no longer
reads the clock.

Not run, as the mandate says: `.\scripts\test-local.ps1 -Parked` and `-Gateway`. No continuous
integration run was started or waited for.

## The matrix

One theory, `RemoteAccessMatrixTests.TheMatrix_EveryAccount_ThroughTheRealRoutesAndTheRealMiddleware`,
proves every cell of the first seven rows. It uses the real registries on the test database, the real
routes on a host bound to port 0, and the real `AuthMiddleware`. The launch instant in these tests is the
tests' own, never the committed one.

| Account | Director enrol | Phone enrol | Cockpit sign-in and request | Dictation |
|---|---|---|---|---|
| Free, created after launch | 200 | 402 | 402 | 402 |
| Remote | 200 | 200 | 200 | 402 |
| Pro | 200 | 200 | 200 | 200 |
| New account (and no `account_trials` row is written) | 200 | 402 | 402 | 402 |
| Running trial | 200 | 200 | 200 | 200 |
| Free, created before launch, no cut-off | 200 | 200 | 200 | 402 |
| Free, created before launch, cut-off passed | 200 | 402 | 402 | 402 |
| Self-hosted Gateway (no entitlement registry) | unchanged, nothing gated: `TheMatrix_SelfHost_NoEntitlementRegistry_IsUnchanged_NothingIsGated` | | | |

## The revert proofs

Both were made after the work was committed. Each one changed one line of product code, rebuilt, ran
`RemoteAccessMatrixTests`, restored the file with `git checkout`, rebuilt and ran again. Neither run used
`--no-build`.

| Proof | The line reverted | With the fix reverted | Restored |
|---|---|---|---|
| Request path | `AuthMiddleware` tells the lease the request is never from a Cockpit or a phone (`cockpitOrPhone: false`) | `Failed: 13, Passed: 18, Total: 31` | `Failed: 0, Passed: 31, Total: 31` |
| Mint | `HostedEnrollmentEndpoint` no longer refuses when the rule answers "not granted" | `Failed: 10, Passed: 21, Total: 31` | `Failed: 0, Passed: 31, Total: 31` |

Red in the request-path proof included the three 402 rows of the matrix, the Director-on-the-same-tenant
test, the no-revocation test, the re-read test and the removed-team-member test. Red in the mint proof
included the same three matrix rows, the new-account-at-the-phone-door test and the test that a Free
account gets no browser key from the Director door.

## Each new test, in one sentence

`RemoteAccessMatrixTests` (unit tests project):

- `TheMatrix_EveryAccount_ThroughTheRealRoutesAndTheRealMiddleware`: the seven account rows above.
- `TheMatrix_SelfHost_NoEntitlementRegistry_IsUnchanged_NothingIsGated`: a Gateway with no entitlement registry serves everything as before.
- `AfterEveryFreeRefusal_NoDeviceKeyIsRevoked_TheLeaseStillAllowsTheDirector_AndTheRevokerWasNeverCalled`: after each Free refusal no key is revoked, the lease still allows the Director, and the revoker was never called.
- `AFreeAccountsDirectorRequest_IsServed_OnTheSameTenantWhoseBrowserKeyWasJustRefused`: the Director is served on the tenant whose browser key was refused a moment before.
- `ARefusedBrowserKey_IsServedAgain_WithNoNewSignIn_OnceTheAccountHasARemoteRow_AndTheLeaseIsReRead`: the same refused key works again once the account has a Remote row and the lease is read again.
- `ARemoteAccountThatStopsPaying_LosesTheCockpitOnTheNextLeaseRead_AndKeepsItsDirector`: the other direction of the same re-read.
- `AnUnreadablePlan_Is503WithRetryAfter_Never402_AtTheMint_AndOnARequest`: a plan that cannot be read is 503 with `Retry-After`, never 402, in both places.
- `TheDirectorDoor_RefusesARequestThatAsksForTheDeviceTypePhoneOrBrowser`: the Director door answers 400 to either type.
- `TheDirectorDoor_AFreeAccountAskingForABrowserKey_GetsNoKeyToTakeToTheCockpit`: the way round the Cockpit door is closed.
- `TheCockpitAndPhoneDoor_ANewAccountThatArrivesThereFirst_Is402_AndNothingIsMinted`: an account that has never been seen is refused at the phone door and no key, tenant key or trial is made.
- `WithNeitherGraceSettingSet_AFreeAccount_KeepsTheCockpitAndThePhoneApp_AndStillHasNoDictation`: with nothing set, nothing is taken from anyone, and the grace still gives no model call.
- `AFreeAccount_ThatIsAMemberOfATeamWithARunningBill_HasTheCockpitAndThePhoneApp_AndStillNoDictation`: a Collaborator, Developer or Manager of a team whose bill is active or past due is let in, and stays on the free tier.
- `AFreeAccount_WhoseOnlyTeamHasNoRunningBill_Is402_EvenAsThatTeamsOwner`: a team with no bill, or a canceled one, gives nothing, even to its Owner.
- `WhereTeamsIsNotReleased_ATeamSeatGivesNothing_BecauseTheRuleHasNoTeamHalf`: the control for the team tests; the same billed team gives nothing where Teams is dark.
- `ATeamMembershipThatCannotBeRead_Is503WithRetryAfter_AtTheMintAndOnARequest_AndTheDirectorIsStillServed`: a failed membership read is "ask again", and the Director is unaffected.
- `ATeamsBillThatCannotBeRead_Is503_NeverARefusal`: a failed bill read is "ask again" too.
- `APersonRemovedFromTheirTeam_IsRefusedOnTheNextLeaseRead_AndNothingIsRevoked`: removal from the team takes effect at the next lease read and revokes nothing.
- `Enroll_TheCockpitAndPhoneDoor_GivenADirectorsDeviceType_Throws_ItNeverTakesATypeFromACaller` and `Enroll_TheCockpitAndPhoneDoor_OnAGatedGatewayWithNoRemoteAccessRule_Throws_RatherThanIssueAKeyUnasked`: the mint fails loudly when it is wired wrongly, instead of issuing a key.

`RemoteAccessRuleTests` (unit tests project, no database):

- Three scope table tests: which tiers carry `remote_access`, which do not, and that Remote is Free plus that one scope.
- Four `Covers` tests: nothing set, a launch instant, the cut-off to the tick, and a cut-off with no launch instant.
- `Parse_BlankValuesAreNotSet_AndAnInstantIsReadAsCoordinatedUniversalTime` and `Parse_AValueThatIsNotAnInstantInTheOneForm_Throws_AndNamesTheSetting`: how a setting value is read, and that an unreadable one throws.
- `GatewayHostOptions_ByDefault_HaveNeitherInstantSet`: no date lives in code.
- `TheCommittedDockerfile_StatesALaunchInstantTheGatewayCanRead_AndNoCutOff`: the one test that reads the committed values. It is expected to change when the owner sets the cut-off.
- Three `Decide` tests and `IsCockpitOrPhoneDevice_...`: the rule's order (plan, then grace), that the grace grants no model scope, that a rule with no team half is never "not known", and which device types count.

Other files:

- `TeamBillOnTheRequestPathTests.AuthorizeAsync_InATeam_EveryMemberHasRemoteAccess_WhileTheSamePersonsOwnFreeAccountDoesNot`: the team tenant's lease is unchanged.
- `HostedTeamEnrollmentTests.Move_ABrowsersKey_IsRefused_AndSoIsSettingABrowserOrAPhoneUpForATeam`: a browser or phone key cannot be moved into, or set up for, a team through the Director door.
- `HostedProTrialTests` was rewritten, not deleted: its first test is now `A_brand_new_account_with_no_payment_enrolls_its_Director_on_FREE_and_is_granted_NO_trial`, and the tests that need a running trial seed one through the ledger's own writer.
- `TenantAiGateTests.EntitlementScopes_Remote_GrantsTheHostedGatewayAndRemoteAccessAndNoAiScope`: renamed and updated for the new scope.

## Teams

The rule: a person has remote access when their own plan grants it, or the pre-launch grace applies, or
they are a member, in any role, of at least one team whose bill is running (active or past due). A
Collaborator counts. A team with no running bill gives nothing. A failed membership or bill read is "not
known" (503), never a refusal and never a grant. It grants remote access only: no model scope, and the
account's tier does not change. A request in a team's own tenant is unchanged: every member has remote
access there, by one stated line in the lease.

Why it lives on the personal read: a person's Cockpit and phone key is always bound to their own account,
never to a team's tenant, so the team lease read never sees a Cockpit request.

The team half exists only where Teams is released (`CC_GATEWAY_TEAMS=1`). I cannot see from the code
whether production has that setting on today.

**Open consequence, for a decision:** the Owner of a team that has never started its bill, whose own
account is Free and created after launch, cannot open the Cockpit. A team's bill is started from the
Cockpit's Team page (`POST /teams/{teamId}/bill/start`), so such an Owner cannot reach the page that starts it
without first taking Remote or Pro, or being in grace.

## What this proof does not cover

- **An open stream is not closed.** A Cockpit or phone stream that is already open when its account
  loses remote access stays open until it reconnects; the next request, and the reconnect, are refused.
  The lease monitor has no index of open streams by tenant today (its own comment says so for
  revocation too), so this was not a small edit and was left out.
- **A browser that navigates to the Cockpit page with a refused key** is shown the raw 402 body, as the
  existing payment refusal already is. The friendly screen is the clients' work on the other branch.
- **Nothing was run against the hosted Gateway, a real PostgreSQL, a phone or a browser.** The tests use
  the test database in SQLite. `.\scripts\test-database.ps1` was not run.
- **The whole `CcDirector.Gateway.Tests` project and `CcDirector.Core.Tests` were not run**, only the
  classes listed above.
- **The Dockerfile line is read by a test as text.** No image was built, so that the running container
  sees the value is not proven here.
- **Effect at deploy.** With the launch instant committed, deploying this takes the Cockpit and the phone
  app from every Free account whose tenant row was made at or after 2026-10-13T20:32:00Z. That is the
  intent, and nobody has checked the production rows for how many accounts that is.
- **`TrialRegistry.GrantIfFirstArrival` now has no caller in the product.** It is kept as the ledger's
  one writer and is used by tests to seed a running trial.
