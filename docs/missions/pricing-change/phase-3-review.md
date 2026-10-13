# Pricing Change, phase 3 - review of pull request 3953

Reviewed 13 October 2026 by a separate review session. Head reviewed: c32d4b02d. Diff reviewed:
`git diff origin/main...HEAD` (46 files). The proof files in this folder were not used as evidence;
every statement below was read from the code at the head, or from a test run made here.

## Verdict

**No must-fix finding. One should-fix finding, and seven notes.** The change does what the mission's
Track B asks: a Free account's Director is served exactly as before and its usage is recorded; a
browser or phone key on a Free account created after the launch instant is refused with 402 at the
mint and on every request; the refusal never reaches the revoker and never touches the tenant's
positive lease; an unreadable plan, membership or bill is 503 with Retry-After and never 402; no new
trial is granted and nothing is deleted; the grace is "everyone is pre-launch" when the launch instant
is absent and "no end" when the cut-off is absent; a self-hosted Gateway is untouched. The clients
render what the Gateway says and decide nothing of their own. The new tests drive the real routes and
the real middleware.

## Findings

### Should-fix 1: the Cockpit and phone door can issue a key the request path treats as a Director's

**What.** Both doors mint through the one function `HostedEnrollmentEndpoint.Enroll`, and both
namespace the client-supplied device id the same way:
`src/CcDirector.Gateway/Api/HostedEnrollmentEndpoint.cs:242` builds
`NamespaceHash(tenant) + "|" + req.DeviceId` whichever door was used. The write at line 255 is
`RegisterForTenant`, which calls `RegisterCore` with `preserveActiveRecord: true`
(`src/CcDirector.Gateway/Pairing/DeviceRegistry.cs:175`). When an ACTIVE row already exists under that
id, lines 862 to 880 keep the row's `DeviceType` (the `if (!preserve)` block is skipped) and only issue
a new key against it.

So: a person on a plan that grants remote access (Remote, Pro, or a Free account inside the grace)
enrols a Director with device id D (type `workstation`). They then set the browser's install id to D
(it is held in browser storage, see `packages/client-core/src/auth/deviceKey.ts`) and sign in through
`POST /mobile/enroll`. The remote access rule says yes, the mint reaches line 255, the existing active
row is preserved with type `workstation`, and the browser is handed a key the request path classifies
as a Director's (`RemoteAccessRule.IsCockpitOrPhoneDevice` at `RemoteAccessRule.cs:184` reads the
stored type). When the plan lapses or the grace ends, that browser keeps working. The Director's own
key is invalidated by the same write (the hash is replaced), so the person has traded their Director
for the Cockpit, which is why this is not worse than the accepted limit of a Director key moved by hand
and is not must-fix.

The reverse collision also exists: the Director door on a device id that already holds an active
`browser` row hands the Director a browser-typed key, and on a Free account that Director then meets
402 `remote_plan_required` on every request, which the mission says Free must never see. Director ids
are GUIDs, so this one is unlikely by accident.

**Why it must change.** The design note (section "The rule") and the code comment above line 199 both
state that the type on a browser or phone key "was written by the Gateway at the mint, so a client
cannot change it afterwards", and line 81 of `RemoteAccessRule.cs` throws for the same class mismatch
in the other direction, calling it a wiring defect. The claim is the whole basis of telling requests
apart, and it is false whenever the scoped id collides. A guard that is documented but not enforced
will be relied on by the next change.

**Fix.** In `Enroll`, after computing `scopedDeviceId`, look the row up; if an active row exists whose
type class (`IsCockpitOrPhoneDevice`) differs from the door's, refuse with 409 and a code, before any
write. One query, no new state. Alternatively pass `preserveActiveRecord: false` from the Cockpit and
phone door so the type is always the door's, but that silently kills the Director's key, so the refusal
is better.

### Notes

**Note 1 - a Cockpit holding two accounts has no visible way out of the notice.** `RemotePlanGate`
(`packages/client-core/src/plan/RemotePlanNotice.tsx:70`) unmounts the whole app, including the
account switcher in `apps/cockpit/src/you/YouMenu.tsx`, on the first 402 `remote_plan_required`. A
person whose current account is Free and whose other stored account is Pro sees "Try again", which
reloads into the same Free account and the same notice. They are not trapped (typing `/signin` in the
address bar reloads the page and the module state resets), but nothing on the notice says so. Offer a
"Sign in with another account" action, or sign-out, on the notice.

**Note 2 - a stale lease can refuse a new subscriber for up to the sweep interval, and the notice
says to press Try again.** The positive lease carries the answer (`HostedAccessLeaseService.cs:286`),
is refreshed by the 60 second sweep (`EntitlementLeaseMonitor.SweepOnceAsync`, `RefreshAsync`), and
has a 5 minute lifetime. A person who subscribes and presses Try again inside that window meets the
notice again. The sentence "Subscribed already? Press Try again." is true but does not say "wait a
minute". Owner-facing wording; not a defect.

**Note 3 - a browser request while remote access is unknown costs one database read per request.**
`Answers` (`HostedAccessLeaseService.cs:168`) refuses to serve a Cockpit or phone request from a lease
whose remote access is `Unknown`, so every poll re-reads the plan and re-tries the team read until it
works. The single-flight gate serialises them but does not coalesce sequential polls. The Cockpit polls
every few seconds and the client does not honour the 10 second Retry-After. Bounded by the outage that
caused it; worth knowing when reading the log during one.

**Note 4 - a tenant-table failure at the Cockpit and phone door answers 502, not 503.**
`tenants.CreatedAtUtcForSubject(subject)` (`HostedEnrollmentEndpoint.cs:365`) has no try/catch, so a
database failure there leaves `Enroll` as an exception and `MobileEnrollmentEndpoint.cs:107` answers
502 "Could not reach the DevThrottle account service". Not a 402 and not a grant, so the rule holds,
but the sentence is wrong and there is no Retry-After. The lease path has the same shape through
`AccountOfTenant` (`HostedAccessLeaseService.cs:240`), which is what `SubjectForTenant` did before.

**Note 5 - who is "created before launch".** `PreLaunchGrace.Covers` (`RemoteAccessRule.cs:65`)
judges the tenant row's `CreatedAtUtc`, which exists only for an account that has reached the hosted
Gateway. A website account from before 2026-10-13T20:32:00Z that never signed its Director, phone or
Cockpit in has no row and is not in grace. Signing in to the Cockpit or phone mints the row, so anyone
who ever had either has one. The design states this; the owner's notice email should say it too.

**Note 6 - the pre-launch grace is not clipped to the cut-off.** The lease expiry is the lesser of the
5 minute lifetime and the paid period end (`HostedAccessLeaseService.cs` around line 280); the cut-off
instant is not used. A lease written just before the cut-off grants for up to 60 seconds past it,
until the sweep. Same bound as a cancellation; acceptable.

**Note 7 - the new matrix test reads the real clock through the product.** `RemoteAccessMatrixTests`
names no clock, so the machine-dependence guard does not list it, but its "created after launch" rows
are minted with `DateTime.UtcNow` (`TenantRegistry.cs:90`) and judged against 2025-01-01, and the lease
runs on `TimeProvider.System`. The verdict cannot flip before 2099 (the fixed far-future dates), so
this is a note. A clock injected into the lease and the mint would remove it.

## The ten questions, with evidence

### 1. Can any path hand a browser or a phone a working key on a post-launch personal Free account?

No, other than the accepted limit and should-fix 1 (which costs the Director).

- `POST /mobile/enroll` and `/m/enroll` are the only product callers that pass
  `EnrollDoor.CockpitOrPhone` (`MobileEnrollmentEndpoint.cs:139` to 144); the door forces the type
  from the platform and the mint asks `RemoteAccessRule.Decide` before any tenant or key exists
  (`HostedEnrollmentEndpoint.cs:358` to 379). Refused with no row written: proven by
  `TheCockpitAndPhoneDoor_ANewAccountThatArrivesThereFirst_Is402_AndNothingIsMinted`.
- `POST /devices/enroll-hosted` (the Director door) refuses a `phone` or `browser` type at line 199
  to 205, before the token is even validated, and before the team branch, so a team enrolment cannot
  carry one either. `Move` (line 691) refuses to move a browser's or phone's key, and the new key it
  mints takes the old row's non-cockpit type (line 741).
- Every product caller of `Enroll` and `Move` was listed with `git grep`: `MobileEnrollmentEndpoint.cs`
  and the two `Map` lambdas in `HostedEnrollmentEndpoint.cs`. Nothing else mints a tenant-scoped key:
  `DeviceRegistry.Register` (no tenant) is reached only by `MobileDeviceEnrollmentService.EnrollAsync`
  on the self-host branch and by the loopback-only `/devices/enroll-signed-in`, and a key with no
  tenant is denied by `HostedTenantBoundary.ResolveRequestTenant` on hosted.
- `AccountSignInCallbackEndpoint` stores the Gateway's own account credential and mints no device key
  (lines 100 to 173: it returns a status page or `{ ok }`).
- The public set at `AuthMiddleware.cs:155` to 241 and the shell allowlist at line 300 hand out
  nothing; every data route, the terminal WebSocket (authenticated by the cookie branch, which stamps
  the device type at `Accept`) and the relay pass the hosted block at line 352 to 381.

### 2. Can a remote access refusal reach the revoker, a tombstone, or the tenant's positive lease?

No. `DenyNoRemoteAccess` is produced only by `Serve` (`HostedAccessLeaseService.cs:179`), which reads
a lease and writes nothing. The only call to `_revoker.RevokeAsync` is at line 300, inside
`case EntitlementOutcome.NotEntitled`, which the remote access answer never enters: the Entitled branch
writes a POSITIVE lease whatever `Decide` answered (line 284 to 291) and returns through `Serve`. The
sweep calls `RefreshAsync` with `cockpitOrPhone: false`, so it can only rewrite the lease positive.
Concurrency: a personal tenant has one lease key and one refresh gate, so a Director's and a browser's
reads are serialised and both write the same positive lease; the fast path reads a struct copy.
Proven in both orders by `AFreeAccountsDirectorRequest_IsServed_OnTheSameTenantWhoseBrowserKeyWasJustRefused`
and by `AfterEveryFreeRefusal_NoDeviceKeyIsRevoked_...` (the lease is live and positive, the revoker
list is empty, every key is still active).

### 3. Is a Director's own request on Free served as before, and usage recorded?

Yes. `cockpitOrPhone` is false for a workstation key and for a session key (no device item), and
`Serve` answers `Allow` for any live positive lease regardless of the remote access answer, including
`Unknown` (line 179 to 189). The Director path changed in two ways only: `AccountOfTenant` replaces
`SubjectForTenant` (same row, same null rule, one query, `TenantRegistry.cs:231`) and `Decide` runs
under the gate once per refresh. `Decide` cannot throw into that path: `TeamRemoteAccess` catches the
membership read (`TeamRemoteAccess.cs:55`) and `EvaluateTeam` answers Unknown on a failed bill read.
Usage recording is not in the diff. The matrix asserts the Director's request is 200 on every row.

### 4. Unknown versus refused.

A failed plan read is 503 at the mint (`HostedEnrollmentEndpoint.cs:341` to 347, with Retry-After
set by `Refusal`) and `RetryUnknown` on the request path. A failed membership read is `null` from
`MemberOfATeamWithARunningBill`, a failed bill read on a team that would have decided it sets
`aBillCouldNotBeRead` (`TeamRemoteAccess.cs:72`) and answers `null`; `Decide` maps `null` to
`Unknown`; the mint answers 503 (line 366 to 373) and `Serve` answers `RetryUnknown`. No branch turns
`Unknown` into `Granted` or `NotGranted`. Proven by the three tests that drop the `entitlements`,
`team_members` and `team_bills` tables. See note 4 for the one 502.

### 5. The grace.

- Absent launch instant: `Covers` returns true for every account, a row-less one included
  (`RemoteAccessRule.cs:65` to 72, `Covers_WithNeitherInstantSet_...`).
- Absent cut-off: no end (`CutOffUtc is null` never refuses).
- Malformed value: `ParseInstant` throws naming the setting; `GatewayHostOptions.FromEnvironment`
  (line 70) is called from the `GatewayHost` constructor; the hosted worker ends the process with
  `StartFailureExitCode` when the host fails to start (`GatewayWorker.cs:129`). A blank value is "not
  set"; a value with an offset or no `Z` is refused (test `Parse_AValueThatIsNotAnInstant...`).
- The grace grants only `RemoteAccessAnswer.Granted` from `Decide`; the tier is untouched, the scope
  table's free line is `Set(HostedGateway)` alone (`EntitlementScopes.cs:95`), and `TenantAiGate` reads
  the table, so dictation stays 402 for an account in grace (matrix rows and
  `WithNeitherGraceSettingSet_...`).
- Boundaries: created strictly before launch is in; created at the instant is out; the grace ends at
  the cut-off instant and not a tick before. Both instants and the row's `CreatedAtUtc` are UTC kind,
  and `DateTime` comparison is by ticks either way.
- Self-host: `FromEnvironment` reads neither variable when not hosted; `_remoteAccess`, the lease and
  the hosted mint dependencies are built only under `Hosted` (`GatewayHost.cs:2014`, 4984); the
  middleware block needs `cfg.Leases`; `HostedEnrollmentEndpoint.Map` is not called. The only line of
  the diff a self-hosted installation sees is the Director wizard copy (B5), which is intended.

### 6. No new trials.

`GrantIfFirstArrival` (`TrialRegistry.cs:298`) has no caller outside tests and the ledger class itself
(`git grep` over `src`). `Evaluate` still folds a running trial to Pro (`EntitlementRegistry.cs`, the
`TrialOutcome.Active` branch, unchanged). `ExtendIfLater` cannot create one (`TrialRegistry.cs:452`
answers `NoTrial` on no row). The `TrialRegistry` diff is comments only; no table, row or type is
removed. `HostedProTrialTests` now asserts zero ledger rows after a first enrolment.

### 7. Teams.

A member of a billed team, any role, gets `Granted` (`TeamRemoteAccess.cs:58` to 66; `EvaluateTeam`
counts active and past due). An unbilled or cancelled team contributes nothing (line 76 to 80). A
removed member: `ListTeamsFor` no longer lists the team, so the next lease read answers `NotGranted`
(proven by `APersonRemovedFromTheirTeam_...` through the sweep). Where Teams is not released, the rule
is built with `teams: null` (`GatewayHost.cs:2015`) and `Decide` answers `NotGranted` without a team
read (line 157 to 161; control test `WhereTeamsIsNotReleased_...`). A browser or phone key is never
bound to a team tenant: `CompleteHostedEnroll` sets no `TeamId` (`MobileEnrollmentEndpoint.cs:134` to
140), so the "every member has remote access in a team's tenant" line at
`HostedAccessLeaseService.cs:358` is reachable only by Director and session keys today.

### 8. The lease.

A lost plan: the sweep refreshes every live positive lease every 60 seconds and rewrites the remote
access answer (`ARemoteAccountThatStopsPaying_...`); if the read fails the old lease stands until its
5 minute expiry, as for a cancellation. A new subscription: the same 60 seconds
(`ARefusedBrowserKey_IsServedAgain_...`). Neither direction exceeds the lease lifetime. Notes 2 and 6.

### 9. The clients.

`readRemotePlanRequired` (`remotePlan.ts:36`) accepts only status 402 with the exact code and a
non-empty sentence, so the older `hosted_subscription_required` 402, a code-less 402, the hosted-AI
credits 402 and a 503 `entitlement_unknown` are untouched (tests in `remotePlan.test.ts`,
`enroll.test.ts`, `deviceCallbackPlanNotice.test.tsx`). The notice shows the Gateway's sentence and
address verbatim and adds only the heading the mission asked for, two button labels and one hint; it
invents no address (non-web schemes become no button). The transport choke point reads a clone
(`client.ts:649`), so no caller's body changes. Trapping a paying user: the Gateway emits this code only
from `Serve` on `NotGranted`, which on production (live-mode rows, trials wired) means Free outside the
grace with no billed team, so a single stray 402 cannot come from a paying row; the two windows are
note 2 (up to 60 seconds after subscribing) and note 1 (a second account). The gate replacing the whole
app is right, because the Gateway refuses every request of that account.

### 10. The tests.

`RemoteAccessMatrixTests` mints through the real `/devices/enroll-hosted` and `/mobile/enroll` on a
started host with a signed token, judges requests with the real `AuthMiddleware.Run`, the real device
registry, boundary and lease, and asks the real `TenantAiGate`; nothing hand-builds a decision. Each
Free row asserts 402 at the phone door, the Cockpit door and a Cockpit request, 200 for the Director,
and an empty revoker list, so removing the mint gate flips the door cells, removing the lease answer
flips the request cell, and restoring the trial grant flips the ledger count. The one team-tenant test
that cannot reach a route says so and asks the lease directly. I did not run a revert proof, because
this review may not edit the tree. The real clock: note 7; the baseline shrank by one line and grew by
none.

## Scope

**Read:** the mission document (sections 3, 4, 5 Track B, 7 Gateway), the B2 design note, the pull
request body, and the full diff: `RemoteAccessRule.cs`, `HostedAccessLeaseService.cs`,
`AuthMiddleware.cs` (whole file at head), `HostedEnrollmentEndpoint.cs` (whole file at head),
`MobileEnrollmentEndpoint.cs`, `HostedEnrollDependencies.cs`, `EntitlementScopes.cs`,
`EntitlementRegistry.cs` (Evaluate, EvaluateTeam), `TeamRemoteAccess.cs`, `TenantRegistry.cs`,
`TrialRegistry.cs`, `GatewayHost.cs` (the wiring hunks, the sweep timer), `GatewayHostOptions.cs`,
`GatewayWorker.cs` (start failure), `GatewayEntryPoint.cs`, `Dockerfile`, `DeviceRegistry.cs`
(RegisterForTenant, RegisterCore), `HostedTenantBoundary.cs`, `MobileDeviceEnrollmentService.cs`,
`SessionOrigin.cs`, `AccountSignInCallbackEndpoint.cs`, `EntitlementLeaseMonitor.cs`, every new and
changed test file, every client file in the diff, the wizard markup and the two documentation pages.
Also `devthrottle_internal` `website/api/_lib/subscription.js` at origin/main, to confirm the Remote
tier and checkout are live before this deploys (they are, commit 415f3868).

**Ran (foreground, this worktree, Debug):**

    dotnet test src/CcDirector.Gateway.UnitTests --filter RemoteAccessMatrixTests|RemoteAccessRuleTests|TeamBillOnTheRequestPathTests|HostedTeamEnrollmentTests|TenantAiGateTests|TeamDirectorTunnelTests
      Passed 205, Failed 0, Skipped 0 (28 s)
    dotnet test src/CcDirector.Gateway.Tests --filter HostedProTrialTests|HostedMobileAccountEnrollTests
      Passed 29, Failed 0, Skipped 0 (33 s)
    dotnet test src/CcDirector.Avalonia.Tests --filter GatewayHostedCardWordingTests
      Passed 3, Failed 0, Skipped 0 (1 s)

**Could not reach:** the client tests (`packages/client-core`, vitest) - no `node_modules` in this
worktree and installing them is outside a read-only review; the four screenshots in this folder were
not opened; no request was made against the hosted Gateway; no revert proof was run.

## Second round

Reviewed 13 October 2026, head a0ae7aa24 (`origin/pricing-change/phase-3`), detached here. Read: the
four phase 3 commits since the first review (c13593d67, 747471537, f0e08f1cc, 5a643668b) in full, the
two "Review findings" sections, and the Move body at the new head. The other changes under `src` in
`1089efb86..HEAD` (twelve License Switching files, plus `GatewayHost.cs`) were checked file by file:
each is byte-identical to `origin/main` or touched only by the merge of main, so none is phase 3 work.
The committed copy of this review matches mine apart from line endings. Ran, foreground, Debug:
`RemoteAccessMatrixTests` and `HostedTeamEnrollmentTests` in `CcDirector.Gateway.UnitTests`:
passed 68, failed 0, skipped 0. The client tests were not run here (no node modules).

**Verdict: no must-fix finding. Should-fix 1 is closed at both doors; one new note on the move route.**

**(a) Should-fix 1.** Closed in both directions. `Enroll` now reads the active row under the scoped id
(`DeviceRegistry.DeviceTypeOfActiveDevice`, tenant-scoped, `Status == Active`) straight after computing
the id and before `LeaveOtherPlaces` and `RegisterForTenant`, and answers 409
`device_id_in_use_by_another_device_class` when the held type's class differs from the door's
(`HostedEnrollmentEndpoint.cs`, the block after line 250). Nothing is written first: on an existing
account the only earlier write is the email backfill in `MintOrLookupBySubject`, and on a new account
there is no row to collide with. A legitimate re-enrolment of the same device is not locked out: the
same door with the same id is the same class, proven by `TheSameDoorSentTheSameDeviceIdAgain_...`; an
inactive row is not in the way; an active row with an empty type reads as a Director's, which is what
the request path reads it as too. Every hosted browser or phone row has carried the Gateway's own type
since the hosted mint was introduced (commit 4b6713317, 21 July 2026), so no old phone row can now meet
the refusal on re-sign-in. The two collision tests go through the real routes and assert no key added
and the holder's key still active and still served.

*New note 8 - the move route is a third writer and does not carry the check.* `Move` registers the
Director's new key under `NamespaceHash(personal) + "|" + directorId` (`HostedEnrollmentEndpoint.cs:757`,
779) with `preserveActiveRecord: true`. If a browser was signed in under the Director's own id while
the Director sat in a team (its personal row then inactive, so the new door check does not fire), the
move back to the personal account preserves the active browser row: the Director is handed a
browser-typed key, meets 402 on a Free account, and the Director door then answers 409 for that id until
the row is revoked. Only the same person can set this up, it costs them their own Director, and it is
recoverable by revoking that device, so it is a note. The fix is the same one-line read in `Move`
before `LeaveOtherPlaces`.

**(b) Note 4.** The tenant row read at the Cockpit and phone door is wrapped (`catch when not
ArgumentException`) and answers 503 with `entitlement_unknown`; `Refusal` sets `Retry-After: 10` on
every 503. The catch returns before `Decide` is asked, so it cannot grant. Proven by
`ATenantRowThatCannotBeRead_AtTheCockpitAndPhoneDoor_Is503WithRetryAfter_NeverA502`, which renames the
table. For symmetry: the new `DeviceTypeOfActiveDevice` read is not wrapped, so a failure there still
surfaces as the door's generic answer (502 at the Cockpit and phone door); not a grant, same shape as the
lease path, note only.

**(c) "Sign in with another account".** It is a hard navigation,
`window.location.assign(profile.basename + profile.signInPath)`, to `/mobile/signin` or `/signin`, both
in the middleware's public set; nothing rides in the address. The page load resets the module-level
refusal, so no stale state. The sign-in screen makes no Gateway call and has no redirect of its own
(`SignIn.tsx` reads `listAccounts()` only to say "Add an account"), so it cannot bounce back into the
app. The round trip ends at `DeviceCallback`, where a refused account shows the notice without this
action, and "Try again" there is the sign-in path, so there is no automatic loop. The earlier account's
stored key is left as it was, which is existing behaviour. The action is only offered through
`RemotePlanGate`, so it never appears at sign-in. The test asserts it calls the way out once and does
not reload.

**(d) Nothing else of substance.** The remaining phase 3 changes are the hint sentence on the notice,
two client tests, the retaken screenshots, and the proof text. The builders' answers in the two proof
files match the code; the declined notes (3, 6, 7) are declined with reasons I accept.
