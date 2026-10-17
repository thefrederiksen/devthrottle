# Track C, Developer C1, pull request 2 - what the owner and Track A read

17 October 2026, by the Track C Developer for approver registration. Built on pull request 4109 (the contract and the
registration). This pull request turns an approver record's health into finished plain English, puts it on the
approver rows the Licenses page reads, and gives Track A one function to call.

## For Track A: the one function

```
CcDirector.Gateway.Licenses.LicenseApproverReadiness
    public string? WhyNotReady(TenantId tenant, string? accountEmail)
```

- **Where to get it:** `GatewayHost.LicenseApproverReadiness` (a public property), and the same instance is registered
  in the host's dependency injection as a singleton (`builder.Services.AddSingleton(LicenseApproverReadiness)`), so a
  service can take it in its constructor.
- **Returns null** when the account's approver can approve now: its last check said `can-approve`, no more than 26
  hours ago.
- **Otherwise returns one finished sentence**, to be shown as given: what is wrong, naming the account, the machine
  and the browser profile, followed by the one thing to do. With no approver at all it is
  "No machine is set to approve sign-ins for alice@example.com. Set an approver for alice@example.com on the Licenses page."
- **The account** is compared lower-cased and trimmed, as every Licenses route takes it. The tenant is the caller's;
  it never reads another tenant's approver.
- It reads the approver store and the Licenses read model and writes nothing; it logs one line per call with the
  account, the state and whether it was ready.
- It does not look at whether the approving Director is connected now: that is the relay's concern at the moment of a
  sign-in. It answers whether the profile itself can approve.

The pure pieces behind it, which the approvers route also uses, are `LicenseApproverReadiness.Verdict(approver,
label, nowUtc)` (the state, the health sentence and the thing to do) and `LicenseApproverReadiness.Sentence(...)`.

## What changed on the approvers route

`GET /gateway/licenses/approvers`, the `PUT` answer, and each account's `approver` in the overview gain `source`,
`state`, `checkedAtUtc`, `healthText` and `whatToDo`, and `browserProfileName` - the display name the last check saw, said in the sentences in place of the identifier `browserProfile` (`LicenseApproverDto`), folded on the Gateway in
`LicenseSignInFold.Approver` from `LicenseApproverReadiness.Verdict`. `missions/license-switching/cockpit-api.md`
section 7 shows the shape and every sentence.

**`status` and `canApproveNow` now also need the health.** Before, "Ready" meant only that the approving machine's
Director was connected. Now a connected Director whose profile cannot approve, or has not been checked within 26
hours, is `cannot-approve` with the label "Cannot approve" or "Not checked", the tone `warning`, and `text` is the
health sentence and the thing to do. A machine that cannot be reached is still said first (`never-reported`,
`not-connected`). The reason: "Ready" beside "needs a fresh claude.ai sign-in" would be a lie, and the design says an
absence is never read as healthy. The visible effect: an approver the owner has just set shows "Not checked" until
that machine's Director checks it (Developer C2's daily check).

`DELETE /gateway/licenses/approvers` on a registered record keeps the owner's removal record (the store change is in
4109; this pull request proves it through the route and over real HTTP). The guard is unchanged: the route stays
owner-only.

## The tests, in plain words

### `LicenseApproverReadinessTests` - every sentence, exactly

Each pins one sentence character for character, because the Cockpit and Track A show it verbatim.

| Test | What it proves |
|---|---|
| `Sentence_NoApprover_SaysNoMachineIsSet_AndWhereToSetOne` | The design's "No machine is set to approve sign-ins for ..." and where to set one. |
| `Sentence_CanApprove_IsNull` | A healthy approver is ready: no sentence. |
| `Sentence_SignedOut_SaysSoAndToSignInAsTheAccount` | Signed out: names the account to sign in as. |
| `Sentence_NeedsFreshSignIn_IsTheDesignsSentence` | The reauth page case, word for word as the design wrote it. |
| `Sentence_WrongAccount_NamesWhoItIsSignedInAs` | Names who the page showed, and to sign out and in as the account. |
| `Sentence_WrongAccount_WithNobodyShown_SaysAnotherAccount` | No shown account still makes a sentence, never a blank. |
| `Sentence_CheckFailed_GivesTheDirectorsDetail_AndWhatToCheck` (3 cases) | The Director's detail is quoted with exactly one stop, with or without one of its own, and no detail gives a clean sentence. |
| `Sentence_NeverChecked_SaysSo_AndThatTheMachinesDirectorMustBeRunning` | Never checked is not ready, and says the machine's Director must be running. |
| `Sentence_LastCheckedOver26HoursAgo_SaysWhen_AndThatTheMachinesDirectorMustBeRunning` | A once-healthy approver checked 26 hours and a minute ago is not ready, and says when it was last checked. |
| `Verdict_CanApprove_SaysWhenItWasChecked_AndHasNothingToDo` | The healthy row's sentence, and no thing to do. |
| `Verdict_SaysTheDisplayNameTheLastCheckSaw_InPlaceOfTheIdentifier` | The sentence says "Work Chrome", never the identifier "work-chrome", once a check has seen the name. |
| `Verdict_AStateThatIsNotAStateWord_IsAnError` | A word with no sentence is an error, never a plausible guess. |
| `WhyNotReady_ReadsTheStoredRecord_AndCallsTheMachineWhatThePageCallsIt` | Through the stores: the account is matched without case, and a machine sharing its name with another is called "machine-a (owner)", as on the page. |
| `WhyNotReady_ForAHealthyApprover_IsNull_UntilItsCheckIsOver26HoursOld` | Ready at exactly 26 hours, not ready one second later. |
| `WhyNotReady_NeverReadsAnotherTenantsApprover` | Tenant one's healthy approver does not make tenant two ready. |

### `LicenseSignInEndpointsTests` - the route (changed and new)

| Test | What it proves |
|---|---|
| `SetApprover_AnswersTheApproverInTheExactShapeThePageIsBuiltFrom_AndTheListShowsIt` (changed) | The row has exactly the seventeen fields in order; a just-set approver is `cannot-approve` "Not checked" with the not-checked sentence and thing to do, source `owner`, no check time. |
| `Overview_SaysPerAccountWhoApprovesIt_AsFinishedWords` (changed) | The overview's approver line is the same sentence as the route's row. |
| `TwoMachinesOfOneName_AreToldApartByTheirUser_InEverySentence` (changed) | The health sentence and the thing to do call the machine "the-mac (owner)" too. |
| `AnApproverWhoseLastCheckFoundItCanApprove_IsReady_AndSaysWhenItWasChecked` | A recent can-approve check on a connected machine is "Ready", with the check time and no thing to do. |
| `AnApproverThatNeedsAFreshSignIn_CannotApprove_AndTheRowSaysTheOneThingToDo` | A connected machine whose profile needs a fresh sign-in is never "Ready", and the row says what to do. |
| `TheSentences_SayTheProfilesDisplayName_WhenACheckHasSeenOne_AndTheRowKeepsTheIdentifier` | The row carries both the identifier (`browserProfile`) and the display name (`browserProfileName`), and the health sentence says the name. |
| `AnApproverWhoseDirectorIsNotConnected_SaysThatFirst_AndStillCarriesItsHealth` | A machine that cannot be reached is said first, and the health is still on the row. |
| `RemoveApprover_OfARegisteredRecord_KeepsTheOwnersRemoval` | Deleting a registered record through the route keeps the removal record. |
| `RemoveApprover_OfAnOwnerSetRecord_KeepsNoRemoval` | Deleting the owner's own record keeps none. |

### `LicenseSignInHostedTests` - over real HTTP and a real SignalR hub

| Test | What it proves |
|---|---|
| `ARegisteredApprover_ComesInOverTheDirectorsHub_ASessionKeyStillCannotRemoveIt_AndTheOwnersRemovalSticks` (new) | A fake Director registers its profile through the real `ReportLicenseApproverChecks` over the wire; the owner's list shows it `registered` and `ready`; the Director's assignments list it; a session key and a raised session key are both refused `PUT` and `DELETE` with `session_key_out_of_scope` and nothing changes; the owner's `DELETE` works, and the Director's next check is refused `owner-removed`. |
| `AliceSetsHerApprover` (helper, changed) | Setting an approver now answers `cannot-approve`; the approving machine's Director then reports a can-approve check over the hub, and the row turns `ready`. Every hosted test that uses the helper now runs through the real check path. |
| `TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead`, `TheFourWrites_AskedWithASessionKeyTheOwnerRaised_AreStillRefused_AndNothingIsStoredOrSent` (unchanged) | The existing proof that a session key is refused `PUT` and `DELETE`, raised or not. |

The guard tests in `LicenseGuardTests` (`TheGuard_RefusesSettingAnApproverAndStartingASignIn_EvenToARaisedSession`)
are unchanged and still pass: the guard was not touched.

## Gate

`.\scripts	est-local.ps1 -Parked` at commit `9f79abe41` (clean tree, on `origin/main` after 4109 merged), with the
temporary folder on D: as the Delivery Lead asked:

```
CcDirector.Core.UnitTests                outcome=Completed    total=2127   executed=2127
CcDirector.Avalonia.Tests                outcome=Completed    total=1063   executed=1063
CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
CcDirector.Launcher.Tests                outcome=Completed    total=214    executed=214
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=71     executed=71
CcDirector.Reclaim.Tests                 outcome=Failed       total=305    executed=305   (1 failed)
CcDirector.DeliveryQualification.Tests   outcome=Completed    total=17     executed=17
cc-director-setup.Tests                  outcome=Completed    total=39     executed=39
cc-director-setup-engine.Tests           outcome=Completed    total=884    executed=884
cc-director-setup-cli.Tests              outcome=Completed    total=74     executed=74
CcDirector.Gateway.Tests.shard1of4       outcome=Completed    total=412    executed=412
CcDirector.Gateway.Tests.shard2of4       outcome=Completed    total=480    executed=480
CcDirector.Gateway.Tests.shard3of4       outcome=Completed    total=404    executed=404
CcDirector.Gateway.Tests.shard4of4       outcome=Completed    total=469    executed=469
CcDirector.Core.Tests                    outcome=Completed    total=4538   executed=4538
CcDirector.Gateway.UnitTests             outcome=Completed    total=13958  executed=13958
```

The one failure is `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused`, which needs Windows
short names under TEMP and cannot run with TEMP on D:; the Tech Lead's own run of 4109 hit the same test for the same
reason. Nothing in this pull request touches Reclaim.
