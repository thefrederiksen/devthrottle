# Review of pull request 3943 - the Gateway relays sign-in approvals and starts sign-ins from the Cockpit

Reviewed at head `4808b48f0`, against its merge base with origin/main, `5bd850478`. 13 October 2026.

## Verdict

**Changes needed.** One must fix (finding 1), five should fix, four notes.

The centre of the change holds. The relay looks the approver up in the caller's own tenant, taken from the
connection; it seals to the key of the caller's own Hello; every command is sent inside an explicit tenant scope; no
store, no row of the Licenses log and no line this change writes carries the address or the sealed approval; the four
writes are refused to everything but the owner's own device; the names, verbs and fields match the Director on main;
and no migration is needed on either provider. What is wrong is at the edges: the limit that is meant to bound the
relay does not bound it, the description's own 15 minute rule can record a wrong outcome, and a relay that throws
leaves the asking Director with no answer.

## Scope - what was read, what was run, what was not reached

**Read in full:** `LicenseApprovalRelay.cs`, `LicenseApproverStore.cs`, `LicenseCommandSender.cs`,
`LicenseSignInService.cs`, `LicenseSignInLog.cs`, `LicenseSignInFold.cs`, `LicenseSignInEndpoints.cs`,
`FleetManagerOwnerDevice.cs`, `LicenseSignInKeepsNothingTests.cs`, `LicenseSignInTestKit.cs`; the diff of
`DirectorHub.cs`, `GatewayHost.cs`, `SessionKeyGuard.cs`, `TenantSettingKeys.cs`, `LicenseDisplayFold.cs`,
`LicenseEndpoints.cs`, `LicenseEntities.cs`, `LicenseCockpitDtos.cs` and `LicenseGuardTests.cs`. Contract sections 1,
2.4, 3, 6, 7 and 8. `cockpit-api.md` section 7. The pull request description.

**Read in part:** `DirectorCommandRouter.TrySendAsync` and `GatewayHost.SendCommandAsync` (what they log);
`AsyncLocalTenantContext.Enter`; every reader of the switch log table in `LicenseStore`, `LicenseSnapshotSource` and
`LicenseDisplayFold`; on the Director side `GatewayStreamClient` (the Command boundary, `RequestLicenseApprovalAsync`,
`CanRequestLicenseApproval`) and `LicenseSignInExecutor`; the tenant and limit tests of `LicenseApprovalRelayTests`,
the settle tests of `LicenseSignInServiceTests`, and the first 350 lines of `LicenseSignInHostedTests`; the first 175
lines of the proof file.

**Run, in the foreground, on `4808b48f0`:**

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 643 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 34 passed, 0 failed |
| `dotnet ef migrations has-pending-model-changes` (SQLite, `src/CcDirector.Gateway`) | "No changes have been made to the model since the last migration." |
| The same with `CC_GATEWAY_EF_PROVIDER=postgres` and the Postgres migrations project | "No changes have been made to the model since the last migration." |

**Not reached, said plainly:**

- No mutation and no reproduction was run out of tree. Every finding below is from reading the code and the tests; the
  file and line are given so each can be checked in a minute. The classes under review are internal, so an out of tree
  project cannot construct them without reflection.
- The four writes were NOT reproduced by me over real HTTP with the self-hosted shared token or a raised session key
  (finding 6 says what the committed tests do and do not hold there).
- `.\scripts\test-local.ps1`, `-Parked` and `.\scripts\test-database.ps1` were not run again; the counts in the proof
  file are the author's.
- The 34 breaks of the proof file were not repeated. Breaks 7 to 33 of the proof file were not read.
- `LicenseSignInEndpointsTests`, `RequestLicenseApprovalHubTests` and `LicenseApproverStoreTests` were read by test
  name only.
- `LicenseSignInCoordinator.cs` and `LicenseApprovalExchange.cs` were not opened; the fit with the Director was
  checked against `LicenseSignInExecutor`, `GatewayStreamClient` and the shared types in `LicenseMessages.cs`.

## Findings

### 1. The limit on approvals in flight does not bound anything - MUST FIX

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs:97-117`, with the length check at `:161-162`.

**What is wrong.** `Accept` works out `overLimit`, and then does exactly what it does for a request under the limit:
it adds the request to `_inFlight` (line 100) and starts a task for it (line 106). The task refuses the request, and
then sends `license-approval-result` to the asking Director and waits up to 60 seconds for that Director to answer
(line 134). So a request past the limit is not refused "at once rather than queued", as the constant's comment says:
it becomes one more task, one more entry, and one more command held open for up to a minute. Nothing caps how many.

Three things make it worse:

- The request object stays alive for the whole of that wait, and the length of `AuthorizeUrl` is only looked at inside
  the task (line 162) - it is never refused on the call. The hub accepts a message of up to 32 megabytes
  (`DirectorStreamLimits.MaxInboundMessageBytes`, `DirectorUpStreamMessages.cs:156`), so each held request can be that
  large.
- The asking Director is the one that must answer the command. A Director that does not answer holds each of its own
  refusals open for the full 60 seconds.
- Every call counts the whole dictionary (`_inFlight.Keys.Count(...)`, line 98, which copies every key under the
  dictionary's locks), so the cost of each call grows with the pile.

**Why it matters.** The hosted Gateway is one process with 3.5 gigabytes for every account. One Director of one
account that asks in a loop with a new `SignInId` each time - a fault in a renewal loop is enough, it does not have to
be hostile - grows the Gateway's memory without limit for as long as it keeps asking, and the limit that was written
to stop exactly this is the thing that lets it through. It also takes the account's eight places: refused requests
count toward the eight for up to a minute each, so the account's real sign-ins are refused while it lasts.

The test that names this, `Accept_OnePastTheLimitOfApprovalsInFlight_IsRefusedAtOnce_AndAsksNoMachine`
(`LicenseApprovalRelayTests.cs:529`), sends one request past the limit and checks that it is answered. It would pass
with any number past the limit; nothing checks that the ninth costs less than the first.

A request past the limit can be refused on the hub call itself, the way a bad identifier already is: the Director
turns a failed call into `LicenseApprovalRelayException` and ends the sign-in (`GatewayStreamClient.cs:1337-1341`), so
no answer has to be addressed and nothing has to be held. The length of the address belongs in the same place.

### 2. The 15 minute rule can record a sign-in as failed when it worked - SHOULD FIX

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs:164-190`.

The rule is this change's own (the description says so). It is judged here as asked. Two ways it writes a wrong
outcome, and a row that has ended is never changed again (`LicenseSignInLog.TryFinish`), so the wrong outcome stays.

**(a) It decides from the machine's latest report whatever that report's age.** Nothing compares
`machine.LastReportAtUtc` (it is on `LicenseMachineView`) with the time the sign-in was asked for. A machine whose
tunnel dropped while it was signing in, and that has not reported since, is settled from the report it sent BEFORE
the sign-in: the holding is not there, so the row ends `failed` with the summary "was not signed in to" - for a
sign-in that may have completed. The committed test shows it:
`SettleUnanswered_ANewSpareTheMachinesReportDoesNotShow_EndsFailed` (`LicenseSignInServiceTests.cs:434`) sends no
report after the sign-in at all, and the row ends `failed` on the report from before it was asked. `cockpit-api.md`
section 7.5 says the row is settled "from the machine's next reports"; the code does not wait for a next report.

**(b) A renewal is judged by comparing two different clocks.** `signedIn > row.CreatedAtUtc` (line 181) compares
`SignedInAtUtc`, written by the machine's clock, with `CreatedAtUtc`, written by the Gateway's. The contract treats a
difference of up to 2 minutes as ordinary (section 7, "Clock skew logged above"). A renewal approved in a local
browser profile finishes in well under a minute, so a machine whose clock is one minute behind reports a
`SignedInAtUtc` earlier than the row's time, and a renewal that worked is recorded `failed`. The machine view carries
`ClockSkewSeconds`; the rule does not use it.

**This is not a rare path.** The route refuses a second sign-in only for the same machine AND account
(`LicenseSignInService.cs:121`). Two accounts on one machine are both accepted, the Director runs them one at a time
(contract 8.4, "As built"), and the Gateway's 5 minute wait for the second starts when it is sent, not when it
starts. A second sign-in behind a first that needs another machine's approval gets no answer in time by design, and
ends up in this rule.

**The contract already has the exact answer.** Section 8.4: results are "kept by `SignInId` for 24 hours and replayed
like a switch (3.6)", and a request past its `NotAfterUtc` for an identifier the Director does not know "starts
nothing". Sending the same `SignInId` again to a connected Director therefore returns what really happened, with its
reason word, and can start nothing. The row already has a `Replayed` column that this change writes and can never
set to true, because the Gateway never sends an identifier twice. At the least the rule should not settle until the
machine has reported after the last moment a Director could still have been running the sign-in.

### 3. A relay that throws leaves the asking Director with no answer, and no test would notice the message being logged - SHOULD FIX

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs:125-149`.

The class says "THE ASKING DIRECTOR IS ALWAYS ANSWERED". The catch at line 144 writes one line and returns. Anything
that throws inside `DecideAsync` - the approvers setting cannot be read (`LicenseApproverStore.Read` throws on
purpose, `LicenseApproverStore.cs:145-153`, and `JsonSerializer.Deserialize` throws `JsonException` for a damaged
value), or the read of the machines fails - ends the relay with nothing sent. The asking Director then waits the
whole three minutes and records the sign-in as `expired`, which is not what happened, and the owner is shown a wrong
reason on the Licenses page.

Two smaller things in the same method:

- `using var scope = _enterScope(tenant)` (line 130) is outside the `try`. If entering the scope throws, the failure
  is an unobserved task fault: no line at all.
- The catch logs the type only, "because a message could quote one". That is right, and nothing holds it. Every test
  that captures the log uses a sender that never throws (`LicenseSignInKeepsNothingTests.cs:55-98`). Change
  `ex.GetType().Name` to `ex.Message` and no test goes red. A test with a sender that throws an exception whose
  message contains the address would hold both this and the missing answer.

### 4. Text from a request reaches log lines before it is checked, and machine names are never checked for control characters - SHOULD FIX

- `LicenseApprovalRelay.cs:105`: `account=` is the request's `AccountEmail`, lower-cased and trimmed, written before
  `IsAccountEmail` runs (line 161, inside the task), and written again at lines 136 and 147 whether or not it passed.
  It has no length limit short of the hub's 32 megabytes and may contain line breaks. This is the one field of the
  request, other than the identifier, that the relay logs - and it is logged unchecked. A Director that put the
  address in the wrong field would have it written to the log.
- `LicenseSignInService.cs:94`: `machine={machineId}` is the body's `machineId`, written before it is parsed at
  line 99.
- `LicenseApproverStore.cs:86` and `:90-93`: `machineName` and `osUser` are checked for length only. A name with a
  line break or another control character is accepted, stored, written to the log at line 86, and put into the
  sentences the page shows. The brief for this review asked that control characters be refused; `browserProfile` is
  held to a shape, these two are not, and `LicenseApproverStoreTests` has no case for it. (`IsAccountEmail` refuses
  white space, which covers line breaks, but not other control characters.)

A forged line in the Gateway's log is the harm: the log is what the owner and every later reader trusts to say what
the relay did.

### 5. "No log line carries the address" is proven for the relay's own lines, not for the host's - SHOULD FIX

`LicenseSignInKeepsNothingTests` builds the relay over a hand-written sender (`_directors.SendAsync`), so the real
`DirectorCommandRouter.TrySendAsync` and `GatewayHost.SendCommandAsync` never run under the log capture. Those two
write three lines for every send, and one of them prints an exception's message: `DirectorCommandRouter.cs:128`,
"TUNNEL DROPPED mid-command: {ex.Message}".

I looked for a way that message could carry a payload and did not find one: the Director's Command handler catches
everything and answers with a result (`GatewayStreamClient.HandleCommandAsync`), the router's other lines print only
the verb, the identifiers and the status (`DescribeSendOutcome`, line 181-185), the relay never logs
`DirectorCommandResult.Error`, and the Gateway clears every logging provider (`GatewayHost.cs:4191`). So this is a gap
in the proof and not a leak I can show. It is still the one line on the path that prints text this change does not
write, and the hosted tests, which do go through the real router, do not capture the log. One hosted test that
captures the log across an approved relay and a dropped tunnel would close it.

### 6. The hosted tests hold less of the "who may call" table than the document says - SHOULD FIX

`missions/license-switching/cockpit-api.md`, section 7, "Who may call": the last column is "A Director's own key, the
machine token", and the text says `LicenseSignInHostedTests` "holds the last column over real HTTP".
`TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead` (`LicenseSignInHostedTests.cs:310`) uses a
Director's device key and an ordinary session key. It never sends the shared machine token, and never a raised
session key. A raised session is held only at the level of `SessionKeyGuard.Check(..., raised: true)` in
`LicenseGuardTests`.

By reading, both are refused: `FleetManagerOwnerDevice.Require` refuses any calling session before anything else
(`FleetManagerOwnerDevice.cs:28-33`) and any credential with no device (`:35-46`), and it runs before the body is
read in all four routes (`LicenseSignInEndpoints.cs:70-76`). I did not reproduce either over HTTP. The document
should claim what the test sends, or the test should send both.

### 7. No test writes a sign-in row on PostgreSQL - NOTE

The proof file reports `.\scripts\test-database.ps1` green, 71 of 71. This change adds no file to
`CcDirector.Gateway.DatabaseTests`, so that run says nothing about the three `ExecuteUpdate` statements of
`LicenseSignInLog` on PostgreSQL. By reading, every value fits the existing columns on both providers - `Kind`
"sign-in" in 16, `Reason` in 16, `State` in 16, the reason word cut to 32, the sentence cut to 500, the identifier a
new one under the unique index on tenant and `SwitchId` - and both pending-model-changes checks are clean. Every
reader of the table on main filters on the switch kind, so a sign-in row is never taken for an open switch
(`LicenseStore.cs:314`, `:362`, `:549`; `LicenseSnapshotSource.cs:89`; `LicenseDisplayFold.cs:428`).

### 8. The approvers: confirmed per tenant and durable - NOTE

One JSON value under `license_approvers` in `tenant_settings`, read and written through `TenantSettingsStore` with
the tenant named on every call; set, replace and remove each rewrite the one list under a lock and touch only the
named account. No route writes a tenant setting by key, so the owner-only rule and the browser profile's shape cannot
be gone around. The read-change-write is safe only while one Gateway process serves an account, which is the case
today.

### 9. Tenant isolation: the unit tests do hold it - NOTE

The description is right that the hosted tests cannot tell an explicit scope from an inherited one. The unit tests
can: `FakeLicenseDirectors.SendAsync` sends nothing unless a scope is in effect and the Director is connected in that
tenant, the tests call `Accept` with no scope in effect, and `Relay_EveryCommandIsSentInsideTheAskingDirectorsOwnTenantScope`
checks the scope of each send. I tried to construct the wrong-scope case - `Accept` called while another tenant's
scope is in effect on the calling thread - and it cannot happen: `AsyncLocalTenantContext.Enter` replaces whatever
was there (`AsyncLocalTenantContext.cs:53-54`), and the tenant handed to `Accept` is the connection's
(`DirectorHub.cs`, `RequireBoundTenant`). The approver, the machines, the connected check and the send all take that
one tenant. No test starts a relay inside the wrong scope to show the replacement; it would be a cheap one to add.

### 10. Two small mismatches - NOTE

- `cockpit-api.md` section 7.5 lists the reason words of a failed sign-in and leaves out `not-key-holder`, which the
  contract allows (8.4) and `LicenseSignInFold.ReasonText` has a sentence for (`LicenseSignInFold.cs:262`).
- The Gateway waits the full 3 minutes for `license-approve` even when the request's own `NotAfterUtc` is sooner
  (`LicenseApprovalRelay.cs:201`). The deadline is passed on to the approving Director, so this costs only a held
  command; it is noted because the wait and the deadline are two numbers where one would do.

## What was checked and found right

- **Returns at once.** `Accept` validates the identifier, adds one entry and starts a task; nothing on the hub call
  waits for a Director. A repeated `SignInId` while its relay runs is dropped.
- **The key.** `SealToRecipient` is `Context.Items` of the caller's own connection, set from its own Hello on every
  Hello, checked to be 32 bytes of base64, and never read from the request.
- **The waits.** 3 minutes, 60 seconds and 5 minutes, each from the shared constant in `LicenseMessages.cs`.
- **Every outcome of 8.3 reaches the asking Director** when nothing throws (finding 3 is the exception): approved
  with its sealed value; refused with a reason only from the contract's words; `no-approver`; `approver-offline` for
  a machine that never reported, is not connected, or drops mid-relay; a timeout; a body that is empty, not JSON, for
  another `SignInId`, approved with no sealed value, or with an unknown outcome - none of which passes a sealed value
  on.
- **The fit with the Director on main.** The hub method's name and single argument, the three verbs, every field of
  the four payloads, the Hello field `SecretTransferPublicKey`, the shape of `SignInId`, and the Director's own check
  of the outcome word all agree.
- **The owner-only writes.** The check runs before the body is read; the tenant comes from the credential; a body
  that is not JSON, or not sent as JSON, is `400` with a sentence; the sign-in routes answer `202` after the row is
  written; a second sign-in for the same machine and account is refused under a lock, so a double click cannot start
  two.
- **The client is dumb.** Who approves, whether a sign-in is offered and why not, and every state of a sign-in row
  are finished words from `LicenseSignInFold`, and the route refuses with the same sentence the page shows. The keys
  of the examples in `cockpit-api.md` section 7 match the shapes the code builds.
