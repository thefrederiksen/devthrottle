# License Switching - review of phases 2 and 4 as a whole

**Verdict: no blocking finding.** The Gateway side (phase 2) and the adding and renewing of spare sign-ins
(phase 4) fit together as the contract says, every message one side sends is handled by the other with the
same meaning, the hard traps of `MISSION.md` section 5 hold in the code as merged, and with
`licenseSwitching.enabled` off nothing reports, switches, renews, approves or emails. Four findings follow, none
of which stops phase 5; two are questions for the Architect, with an answer proposed for each.

## Scope

Read on this tree, which is `origin/main` at `4f844472a`:

- `MISSION.md`, `contract.md`, `cockpit-api.md` (sections 6 and 7 in full, the rest by heading), `phase-2-report.md`,
  `phase-4-report.md`, `review-phase-1-whole.md` and its answers.
- The contract types and constants: `src/CcDirector.Gateway.Contracts/LicenseMessages.cs`.
- The Director side: `LicenseSwitching.cs`, `LicenseSwitchingSetting.cs`, `LicenseKeyHolderLock.cs`,
  `LicenseUsageReporter.cs`, `LicenseSwitchCoordinator.cs`, `LicenseSignInCoordinator.cs`, `LicenseRenewer.cs`,
  `LicenseApprovalExchange.cs`, `LicenseSwitchResultStore.cs`, `LicenseCommandLine.cs` and
  `LicenseCommandLine.SignIn.cs` in `src/CcDirector.Core/Licenses`; `LicenseSwitchExecutor.cs`,
  `LicenseSignInExecutor.cs`, `LicenseApprovalTunnel.cs`, `GatewayLicenseReportSink.cs`, the license parts of
  `ControlApiHost.cs`, `GatewayStreamClient.cs` and `SessionCommandExecutor.cs` in `src/CcDirector.ControlApi`;
  the start in `App.axaml.cs`.
- The Gateway side: every file in `src/CcDirector.Gateway/Licenses`, the license parts of `DirectorHub.cs` and
  `GatewayHost.cs`, `Api/LicenseEndpoints.cs` and `Api/LicenseSignInEndpoints.cs`.
- Trap 4: `UsageLimitInputGuard.cs`, the guard's place in `Session.cs`, `TerminalStateDetector.cs`.
- The Python key store and its tools: `license_store.py` (lock, exchange journal, sign-in target, finish,
  abandon), `license_usage.py` (read, read with refresh, spare refresh), `license_signin.py` (sign in, approve in
  browser, renew due), `license_signin_ops.py`.
- The tests by name and header only, to see what each one stands on: `LicenseSwitchServiceTests`,
  `LicenseSignInHostedTests`, `LicenseSwitchCheckHostedTests`, `LicenseSignInKeepsNothingTests`,
  `RequestLicenseApprovalHubTests`, `LicenseSignInExecutorTests`.

## What was run, and what was not

- **One program, out of tree.** The phase 4 report says that whether `license-approval-result` is delivered while
  the same Director is still answering a long `license-sign-in` on the same connection "has not been tested". The
  code gives no answer by reading: the Director registers one `Command` handler on the SignalR client and awaits the
  dispatch inside it (`GatewayStreamClient.cs` line 510, `ControlApiHost.cs` line 1244), and the Gateway sends every
  command as a client result (`GatewayHost.cs` line 7107). If the client ran handlers one after another, a sign-in
  waiting up to four and a half minutes for its approval would hold the approval's own result behind it, the
  sign-in would end `expired`, and the result would be dropped: a blocking defect. So a stand-alone program was
  built in the session's scratch folder, outside this tree, with the same packages the Director uses
  (`Microsoft.AspNetCore.SignalR.Client` 10.0.9, MessagePack), one hub, one client, one `Command` handler in
  which message A waits on a gate that message B opens. It ran once, in the foreground. Result: with client
  results (the `Command` shape) B ran while A waited and the server received both answers; the whole took under a
  second. With fire-and-forget sends (`SendAsync`, no invocation identifier) the client IS sequential: B did not
  run until A gave up after three seconds. **The Command path is therefore safe as built**, and the phase 4
  report's open item is answered for the transport, though not yet for a real Gateway - see finding 3.
- **Nothing else was run.** No test suite, no Gateway, no Director, no sign-in. Every other statement below comes
  from reading the code on this tree.
- **No tracked file was changed.** This file is the only addition.

## The contract, message by message

Each message was followed from the code that builds it to the code that reads it, on the other side.

| Message | Sender | Receiver | Agrees |
|---|---|---|---|
| `PushLicenseUsage(sequence, LicenseUsageReport)` hub method | `GatewayStreamClient.PushLicenseUsageAsync`, fed by `LicenseUsageReporter` through `GatewayLicenseReportSink` | `DirectorHub.PushLicenseUsage` into `LicenseUsageIngest` (ownership epoch, sequence gate, skew shift) | yes; the hub names the method only when the ingest exists |
| `RequestLicenseApproval(LicenseApprovalRequest)` hub method | `GatewayLicenseApprovalRelay` in `LicenseApprovalTunnel.cs`, called by `LicenseSignInCoordinator` only when the capability list names it and a seal exists | `DirectorHub.RequestLicenseApproval` into `LicenseApprovalRelay` (in-flight cap 8 per tenant, refusal as `HubException`) | yes; the hub drops the method from the capabilities when the relay is absent (`DirectorHub.cs` line 507) |
| `license-switch` command | `LicenseSwitchService` through `LicenseCommandSender` | `LicenseSwitchExecutor` to `LicenseSwitchCoordinator` steps 2 to 12; answers `LicenseSwitchResult` | yes; the same `SwitchId` is resent and the stored answer is replayed with `Replayed = true` |
| `license-sign-in` command | `LicenseSignInService.StartAsync` from the two Cockpit routes | `LicenseSignInExecutor` to `LicenseSignInCoordinator.RunAsync`; answers `LicenseSignInResult` within 5 minutes | yes; the service settles an unanswered sign-in from its own deadline |
| `license-approve` command | `LicenseApprovalRelay` to the approving machine's Director | `LicenseSignInExecutor` to `LicenseApprover`, which seals before it returns | yes; 150 seconds for the tool, 3 minutes for the command |
| `license-approval-result` command | `LicenseApprovalRelay` back to the asking Director | `LicenseSignInExecutor.DeliverApproval` into `LicenseApprovalExchange`, keyed on `SignInId` | yes; an unknown or already-settled identifier is answered, not thrown |

The constants are one set, in `LicenseContract`, and both sides read them from there. The sealed approval is the
one value the contract lets cross the Gateway, and it crosses sealed to the asking Director's public key taken from
its own Hello (`DirectorHub.cs` line 487); the Gateway holds no column for it and
`LicenseSignInKeepsNothingTests` checks the database bytes and the log lines for both the address and the approval.

## Findings

### Not blocking - a switch that keeps failing is decided again every minute, and the owner is never emailed about it

**Where.** `src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs`, class summary lines 67 to 68 and the pass
around lines 451 to 499; `src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs` lines 76 to 118.

**What.** When a Director answers a switch `failed`, the row ends and "the next check decides afresh with a new
identifier". The engine's bar is written on the SOURCE account, so that account is not chosen as a target while its
window is tired; nothing holds the MACHINE. A machine whose Director fails every exchange for a reason the Gateway
cannot see in a report - the key store locked by a stuck process, a verification that keeps failing, a spare
folder the file system refuses - is over the threshold on a fresh reading every minute, has a target every minute,
and so is sent a new switch and writes a new failed row every minute until the window resets: up to a week on the
weekly window. Because a target exists, no "no account has room" event opens, so the one email the mission gives
the owner (`MISSION.md` section 4) never goes. The owner sees it only by opening the Licenses page and reading the
log.

**Why it matters.** It is not a wrong switch and no key is at risk, which is why it does not block. But it is the
one way the whole can be quietly stuck: the Gateway believes every minute that it is about to fix the exhaustion,
the machine stays on a tired account, and nobody is told. The log grows by one row a minute per machine.

**Proposed.** After a machine has answered `failed` with the same reason code for the same source and target twice
in a row, hold that machine out of the automatic decision for a wait that grows the way the email wait does (one
minute, two, four, up to an hour), recorded on the last failed row so a Gateway restart keeps it. When every
eligible machine on a tired account is held this way, treat it as the no-room case, so the existing one-email rule
tells the owner once. Switch now should ignore the hold, as it already ignores a bar.

### Not blocking, for the Architect - one cause can send one email per machine per account in the same minute

**Where.** `src/CcDirector.Gateway/Licenses/LicenseRenewalEmailService.cs`, `TickAsync` lines 128 to 200;
`contract.md` lines 554 to 590. Raised by the phase 2 report ("For the Architect"), not changed since.

**What.** The email is one per failure streak, and a streak is one holding: one machine, one account. The code
does exactly what the contract asks. But the common cause of a failed renewal is one approving browser profile that
has been asked to log in again (the phase 4 report saw claude.ai do this after about thirty approvals in a day), and
that profile approves every account it is set for on every machine. Five machines holding three accounts is fifteen
emails in one check, all saying the same thing in different words.

**Why it matters.** Fifteen emails for one signed-out browser teaches the owner to ignore the channel, and the
channel is also the one that says "no account has room".

**What to do.** Group by check, not by cause: the pass already runs once per tenant per minute and already builds the
list `due` before it sends anything. Send ONE email per tenant per check carrying every holding that became due in
that check, one line each (machine, account, when the sign-in ends, the machine's reason), and mark every row in it
with the same confirmation. A holding that becomes due on a later check gets its own email, as now. This keeps every
guarantee the contract makes (one email per streak, the row is what makes it go once, a restart does not resend)
and needs no new column: the rows already carry the three reason words. Grouping by the approving machine instead
would need the asking Director to know which machine approved, which it does not for a browser on its own machine,
so the by-check grouping is the simpler and the more honest one.

### Not blocking - the concurrency the sign-in relay depends on is a property of the client-result shape, proven out of tree only

**Where.** `src/CcDirector.ControlApi/GatewayStreamClient.cs` line 510; `src/CcDirector.Gateway/GatewayHost.cs`
line 7107; the phase 4 report's "Unproven until the relay exists".

**What.** The stand-alone run above shows that the SignalR client runs client-result invocations concurrently and
fire-and-forget invocations one after another. The relayed sign-in works because `Command` is a client result. Two
things follow. First, if anyone ever changes the Gateway's command send from `InvokeAsync` to `SendAsync`, or adds a
fire-and-forget handler on the Director's connection that can wait, a sign-in will again hold the approval result
behind it; there is no test on either side that would catch it, because the hosted tests fake the client. Second,
the proof is of the transport, with one hub and one client, not of a Director and a Gateway: the phase 4 report's
item stays open for phase 5 in that narrower form.

**Proposed.** A one-line comment at the registration naming the dependency, and in phase 5 one live run in which a
Cockpit-started sign-in is approved on another machine, which exercises exactly this.

### Not blocking - in-flight relay state and the sent-after-expiry marks live in memory

**Where.** `src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs` (the in-flight set and cap);
`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs` (the marks that stop a second send after `NotAfter`).

**What.** A Gateway that restarts while an approval is being relayed forgets the request; the asking Director waits
until its own `approveBy` and ends the sign-in `expired`, and the approving Director's `license-approve` answer
reaches a Gateway with nowhere to put it. The contract accepts this (a sign-in is a few minutes, a restart is rare)
and the Director's sign-in log records the outcome, so this is noted, not asked to change. The same holds for the
switch marks: after a restart the one resend happens again, which the contract allows and the tests cover
(`Restart_AfterNotAfter_ResendsOnceAnyway_AndTheDirectorsExpiredAnswerSettlesTheRow`).

## The other requested traps, as checked

- **A sign-in key copied instead of exchanged (trap 2).** The exchange is `license_store.py` `_switch`: copy the
  spare in, copy the active out to the spare's folder as the replacement, drop, with a journal and `_verify_switch`
  after; the Director's coordinator only calls `license switch <target> --expect-active`. A sign-in is written once,
  in place, by `finish_sign_in`, and a renewal never creates a second folder. A new spare's folder is created empty
  and a leftover empty folder from a Director that died mid-sign-in is reused, not refused (`sign_in_target`,
  lines 1094 to 1100).
- **A key reaching the Gateway, a log, a report or an email (trap 3).** `LicenseUsageReport` carries plan words,
  percentages, times, outcome words and reason codes; `LicenseStore.ReadUsageRows` nulls the tool detail. The seal
  and open pass the value on standard input (`CcSecretsLicenseApprovalSeal`), never as an argument, so
  `ProcessRunner` could log every argument and still log no key. The two emails are built from the read model, not
  from a report's free text. No file under `Licenses` writes a token to `FileLog`.
- **Anything but Escape reaching a session on the usage-limit dialog (trap 4).** `UsageLimitInputGuard` wraps the
  terminal backend for every console session (`Session.cs` line 2297) and lets through only a single 0x1B byte while
  the detector's settled state says the dialog may show.
- **A switch to a sign-in about to expire (trap 5).** Both sides refuse: the engine's `SpareSignInExpiresTooSoon`
  leaves the target out, and the Director's step 9 refuses with `target-sign-in-expiring` if the Gateway's picture was
  older than the key. Switch now, the forecast and the minute check all go through `ChooseTarget` and
  `SwitchableMachines` (`PreviewManual`, `DecideManual`, `Decide`), so the page offers Switch now exactly where the
  route accepts it, and the one difference is deliberate: a NAMED target that is barred is allowed by hand.
- **Setting off.** `LicenseSwitchingSetting` is off unless the file says `true`. `LicenseSwitching.Start` returns
  before the reporter, the lock, the renewer or the sign-in coordinator start. A `license-switch` to an off Director
  answers `failed` with `not-key-holder` and stores nothing; a `license-sign-in` and a `license-approve` answer
  `not-key-holder` and `no-approver`. The Gateway's services are inert for a tenant with no machine rows
  (`HasMachines` before anything is read), so a tenant whose Directors are all off never reports, so nothing switches,
  renews or emails for it.
- **Tenant scoping.** Every background pass enters the tenant's scope through `ForEachTenantAsync`;
  `SendCommandAsync` refuses a send with no scope; the Switch now route throws before writing if the scope in effect
  is not the caller's tenant; the sign-in routes take the tenant from the credential and the machine from a row of
  that tenant; the relay sends only to the approver stored for that tenant and account.
- **Across restarts and offline.** Gateway restart: switch rows and sign-in rows are stored, the one resend is
  bounded, the renewal streaks and the no-room events are rows. Director restart: the key-holder lock is retaken,
  the reporter sends a `Connected` report at the next Hello, replayed switch answers come from the disk store for
  24 hours, and a renewal's in-memory failures are gone until the next daily run (the email rule was fixed for this
  in 3948). Director offline mid-switch: `pending` with `director-offline` and the same identifier until `NotAfter`,
  then settled from the machine's next report. Director offline mid-sign-in: the Gateway's `SettleUnansweredAsync`
  ends the row from the deadline; the Director's `finally` cancels the login process and abandons a new spare's
  folder if the process lives, and reuses the empty folder next time if it did not.

## Claims in the two reports

Every claim checked against the code held. Two are narrower than they read:

- Phase 4: "the Gateway's waits in contract 8.5 can be built as written" - built in phase 2 and matching, yes.
- Phase 2, 3948: "a streak ends only on something present" - true, and it is the reason the by-check grouping above
  needs no new rule for ending.

## Not reviewed

- No test was run. Which tests are red or green on this tree is not known from here.
- The Cockpit page (phase 3) and anything under `phase-3/`.
- The `cc-secrets seal` and `open` commands themselves, and the Keychain backend on the Mac: read as called, not
  as implemented.
- The browser-harness approval script and the lost Authorize click, which the phase 4 report says is not proven
  cured.
- `LicenseUsageHistory` and the forecast's numbers beyond its target choice.
- Phase 1's second finding, the old token copies left in `accounts.json`, deferred to phase 5 in the phase 1
  answers; still deferred.
- The PostgreSQL byte-order pinning of the failed-renewal lookup column that the phase 2 report lists as not done.
