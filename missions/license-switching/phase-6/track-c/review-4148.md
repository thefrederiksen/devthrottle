# Review of pull request 4148 - License Switching Track C, hosted proof of the approver alarm

Reviewer: a separate review session (54040994). Head: refs/review/4148 (767c946dc), merge base 66fc4ef15.
Read only through git objects; nothing built, run or checked out.

## Scope

One file changed: src/CcDirector.Gateway.Tests/Licenses/LicenseSignInHostedTests.cs, one new test (29 lines added):
ASignedOutCheck_OverTheDirectorsHub_OpensTheApproverAlarm_AndACanApproveCheckClosesIt.

Read alongside it, all at the review head: LicenseApproverAlarm.cs, LicenseAlarmService.cs, LicenseApproverRegistration.cs,
LicenseApproverReadiness.cs, LicenseSignInFold.Label, DirectorHub.ReportLicenseApproverChecks, GatewayHost wiring
(lines 2483-2492 and 2935-2951), LicenseOwnerMailer.cs, AccountNotifyByTenantClient / DevThrottleApi, LicenseAlarmPushNotifier,
FakeTunnelDirector.cs, and the unit tests in LicenseApproverAlarmTests.cs for comparison.

## The brief's questions, answered

**Does it drive the real path?** Yes. The fake Director invokes the real hub method over a real SignalR tunnel
(FakeTunnelDirector.ReportLicenseApproverChecksAsync -> _conn.InvokeAsync(ReportChecksHubMethod)). DirectorHub enters the
bound tenant's scope and calls LicenseApproverRegistration.Report, which writes the record under the store's lock and then
calls _afterReport, which GatewayHost wires to LicenseApproverAlarm.RaiseSoon with the REAL tenant boundary
(tenant => _tenantBoundary.EnterScope(tenant), GatewayHost.cs line 2949). The raise reads the real approver store and read
model and writes through the real LicenseAlarmService into the Gateway database; the test reads back through
LicenseAlarms.ListOpen, which is the same database. The only thing hand-built is the check report itself, which is exactly
what a Director sends. Nothing is substituted that could hide a hosted-only failure in the hub, the scope or the store.

**Does it wait on a condition, not a sleep?** Yes. RaiseSoon registers its task in _running synchronously, inside the hub
call, before the hub answer is returned (Report calls _afterReport and only then returns), so by the time InvokeAsync
completes the task is already tracked and WhenIdleAsync awaits exactly it. No fixed sleep anywhere. The once-a-minute tick
can coincide but is serialised by the per-tenant semaphore and reads the records under that same gate, so it cannot reopen
an alarm the report's raise just closed; it is idempotent with the tracked raise.

**Can it pass when the alarm never opened?** No. RaiseSoon swallows and logs a failed raise, so WhenIdleAsync completes
either way - but the first assertion after it is Assert.Single(ListOpen(alice)) plus the exact kind, account and Problem
sentence, a presence check. A raise that failed silently leaves zero rows and that assertion fails. The closing half,
Assert.Empty, is only reached after the open was proven, and a second raise that failed silently would leave the row open
and fail it. The expected sentence matches the code: who = "The approver for {account} (browser profile \"{profile}\" on
{label})", ProfileName falls back to BrowserProfile ("work"), and Label is the bare machine name because MAC_OF_ALICE has a
single operating system user in the read model.

**Public repository, no real names?** Clean: example.com accounts, placeholder machine names.

## Findings

### 1. The alarm open in this test can reach the production account service (one line to fix)

Opening an alarm runs LicenseAlarmService.RaiseAsync -> SendDueEmailAsync -> EmailAsync -> the host's LicenseOwnerMail,
inside the very task WhenIdleAsync awaits. On this fixture the host is built with Hosted = true, and the mailer is the
real LicenseOwnerMailer (GatewayHost.cs 2485-2490) over a real HttpClient with a 30-second timeout, whose base address
defaults to https://devthrottle.com (DevThrottleApi.DefaultBaseUrl) and whose only guard is the NOTIFY_OWNER_SERVICE_TOKEN
environment variable (LicenseOwnerMailer.SendAsync: hosted is true, so it goes straight to the token check).

So on any machine where NOTIFY_OWNER_SERVICE_TOKEN is set - the hosted box, or a developer who set it to try the owner
email - this test POSTs to the production notify-owner endpoint asking it to email the owner of a tenant that exists only
in the test database, and waits up to 30 seconds for the answer. The pass or fail is not decided by it (the email outcome
is recorded, never thrown), but the gate rule is that a gate-suite test never touches a real external service or the
real clock, and this one does under a setting that is nothing to do with the test. On this machine the variable is not
set (checked in User, Machine and process scope), so here it fails fast with "setting is missing" and the test is fine -
which is also why the author would not have seen it.

The precedent in the same folder already solves it: LicenseRenewalEmailHostedTests sets
_gateway.LicenseOwnerMail = (tenant, email, _) => ... a fake that records and returns Sent, with the comment "no test
of this class reaches the account service". This fixture should do the same in InitializeAsync, before StartAsync. The
existing tests in this file never opened an alarm, so this pull request is the first one in the file that reaches the
mailer; the fix belongs with it.

Severity: a real outbound call to production from a test, conditional on an environment setting. Not a false pass.

## Observations, not findings

- **The other-tenant assertion proves little.** Assert.Empty(ListOpen(bob)) can only catch a raise that WROTE into the
  wrong tenant, and the write is keyed by the explicit tenant that travels RaiseSoon(tenant) -> RaiseAsync ->
  _db.CreateContext(tenant). Bob has no approver at all, so a cross-tenant READ bug - ConditionsNow seeing another
  tenant's approvers, or the hub binding the wrong tenant - would still leave Bob's list empty. It is the same shape as
  the unit test OneTenantsRaise_NeverOpensOrClosesAnotherTenantsAlarm. A stronger shape costs a few lines: give Bob a
  healthy approver for the same account on MACHINE_OF_BOB (SetApprover(_bobOwner, A, BobMachine) then a CanApprove
  report from _bobDirector) before Alice's signed-out report; then Alice's alarm must still open alone, Bob's list
  must stay empty, and Bob's approver must still read "ready". Not a defect in the product; the assertion is honest, just
  weak.
- The push on open (LicenseAlarmPushNotifier.NotifyOpenedAsync) finds no phone subscriptions for the fresh tenant and
  returns without sending; it enters the real tenant scope to look, which is a small extra proof of the scope wiring.

## Verdict

One finding, a one-line fixture change with an in-folder precedent. The test itself proves what it claims: the real hub,
the real scope entry, the real store, a condition wait, and a presence check before the absence check.
