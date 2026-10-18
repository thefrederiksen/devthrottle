# Review of pull request 4153 - License renewal through the standing approvers

Reviewer: a separate review session (License Switching - Reviewer - Track C 4153 renewal through approvers).
Read only through git objects: merge base 5d4d8caca, head refs/review/4153 (commits 7afb2a6bf, d39f68d02).
Nothing was built, run, checked out, committed or pushed. No gate was run by this review; the green
reported in the proof file is the author's.

## Scope - what was checked, and how

1. Do the hosted tests run the Director's own renewal and approval code end to end through the Gateway
   relay? Traced the path on the ref: `LicenseRenewer.TickAsync` -> real `LicenseSignInCoordinator`
   (`RunAsync`, the relay request, `_approvals.Expect`, the seal open, `CompleteAsync`) -> the real hub
   (`FakeTunnelDirector.RequestLicenseApprovalAsync` invokes `RequestLicenseApproval` on a real hosted
   `GatewayHost`) -> real `LicenseApprovalRelay` -> `license-approve` command -> the real
   `LicenseSignInExecutor.ApproveAsync` -> the real approver in `LicenseSignInCoordinator.cs` (calls
   `_tool.ApproveAsync` and `seal.SealAsync`) -> `license-approval-result` command -> the real
   `LicenseSignInExecutor.DeliverApproval` -> real `LicenseApprovalExchange` -> the coordinator opens the
   seal and completes. Fakes are exactly the four named: the license command line, the seal, and the two
   tunnel seams (relay request, report sink). The fake tunnel registers `Command` with a result, which
   is how `GatewayStreamClient` registers it (its comment at line 511 says the relay depends on that),
   and the real dispatcher it bypasses (`HandleCommandAsync` -> `SessionCommandExecutor`) holds no lock
   or semaphore that would serialise commands, so the bypass hides no serialisation. The executor's
   entry points are `internal static` and `CcDirector.ControlApi` has `InternalsVisibleTo`
   `CcDirector.Gateway.Tests`. No hand-built step stands in for a real one.
2. Is "no deadlock" proved with a bound? See finding 1.
3. The log row's content. Read `LicenseApprovalLog.Record` and `Sentence`: the row holds the asking
   machine's id, name, operating system user and Director id (from the read model), the account, the
   outcome, the reason word, and a sentence built from those plus `LicenseDisplayFold.RenewalWhy`. The
   reason word comes from `Decided.Answer.ReasonCode`, which `ReadApproval` restricts to
   `LicenseRenewalErrors.All` and the relay's own refusals set from constants. The approving Director's
   `Message`, the authorize address and the sealed approval are never passed to the log. The existing
   `LicenseSignInKeepsNothingTests` reads the database bytes after three relays (approved, refused,
   no approver) and asserts neither the address mark nor the sealed value is present; it now runs with
   the new rows in the table (its constructor change is the only edit) and the proof file says it is
   green. A unit test echoes the address into the Director's refusal message and asserts the row does
   not contain it.
4. Written once per relay, for every outcome, inside the right tenant. `Record` is called once at the
   end of `RelayAsync` for approved, refused by the approver, refused by the Gateway (no approver,
   approver offline, expired, no public half) and a relay failure (`DecideAsync` throwing becomes an
   `approval-failed` row naming no machine). It writes through `_db.CreateContext(tenant)` with the
   asking connection's bound tenant; a unit test asserts the other tenant has no row. Not written, each
   by design and each already a logged line: a request refused at `Accept` (nothing is kept, which is
   what makes the in-flight limit a limit), a repeat of a sign-in already in flight (dropped), a Director
   that has never reported to the tenant (no machine row to put it against), the Gateway stopping, and
   the one case where sending the answer throws (the router returns null or a Timeout status rather than
   throwing, so in practice this does not occur).
5. Existing readers, the Cockpit's log view, the client types and the fixtures. `LicenseStore.ListLogPage`
   returns every kind; `LicenseDisplayFold.LogEntry` routes `approval` to its own fold before the switch
   fold, exactly as `sign-in` is routed. Every other reader of `LicenseSwitchLog` filters on its own kind
   (`LicenseSignInLog` on `sign-in`, `LicenseStore` switch readers on `switch`, the no-room reader on
   `no-room`, `LicenseRenewalFailureLog` on its own), so an `approval` row is invisible to all of them.
   `packages/client-core` types `LicenseLogEntry.kind` as `string`; `LicensesView.tsx` renders
   `kindText`, `stateText`, `stateTone` and `summary` verbatim and branches on nothing (rule 7 holds).
   The Cockpit fixtures test (`fixtures.test.ts`) compares each fixture to the fenced json examples of
   `cockpit-api.md`; this pull request adds no fenced block and changes none (the diff touches two table
   rows and one prose paragraph), so its inputs are byte-identical to main. The Cockpit tests were not
   run by the author (the Cockpit did not change) and not by this review; the statement that they stay
   green is by inspection of unchanged inputs, not by a run.
6. Public repository: example names only (alice, bob, machine-a, machine-b, the-mac, the-pc,
   work-chrome, claude.example). ASCII only in the whole diff. No assistant attribution in either commit
   or the pull request body.

## Findings

### 1. "No deadlock" is proved by a passing run, not by a bound the test owns

`LicenseRenewalThroughApproverHostedTests` awaits `Renewer.TickAsync(CancellationToken.None)` and
builds each `LicenseSwitching` with `signInDelay: (_, ct) => Task.Delay(Timeout.Infinite, ct)`. The
class comment says the wait "ends only when the answer arrives, or the test is cancelled" - but nothing
in the test can cancel it. So a deadlock does not fail the test; it hangs it.

What bounds it today, and what does not:

- A deadlock on the APPROVING side (inside `license-approve`) is bounded by the Gateway's own
  `ApproveCommandTimeout` (3 minutes, real clock, in `DirectorCommandRouter.TrySendAsync`): the relay
  then answers `approval-failed`, the answer is delivered as a concurrent client-result command, the
  coordinator ends, and the test fails with a named assertion. That is a real bound.
- A deadlock on the ASKING side after the answer arrives - between the coordinator's `CompleteAsync`,
  the key-holder lock and the reporter, which is exactly what the self-approval test exists to guard,
  since the same Director is doing both - has no bound in the test. Locally, `-Parked` passes
  `--blame-hang 10m`, so the hang is named after ten minutes. In `.github/workflows/ci.yml` (line 272)
  the full run is `dotnet test cc-director.sln -c Release --no-build` with no `--blame-hang` and no
  `timeout-minutes`, so a regression here is a six-hour cancelled job that names no test, and until it
  is found main has no new last good commit to release from.

The harm is a future one, but it is the one thing this test was written to catch, and when it fires
the test gives the worst possible answer. The fix is small and does not make the test wait on the
machine: pass a token that cancels after a couple of minutes to `TickAsync` (and, since it is the same
token, to the renewal). The coordinator already honours it - its `_delay` is linked to it and it calls
`ThrowIfCancellationRequested` after `WhenAny` - so a deadlock then fails the named test on every
runner with an `OperationCanceledException`. A bound that only fires on a hang never decides a pass,
so it is inside the owner's test standard. Then make the class comment true.

Not blocking on its own; the author and Tech Lead decide. The passing self-approval run does show
that, as the code stands, the two sides do not deadlock.

### 2. The "never reported for this account" line and sentence say more than the code checks (minor)

`LicenseApprovalRelay.Record` matches the asking Director by `DirectorId` among the tenant's machines
and nothing else. The line it writes - `not in the Licenses log, the Director that asked (...) has
never reported for this account` - sits beside `account={email}`, so a reader takes "this account" as
the Claude account, which is false: a Director that has reported holding only bob writes a row for a
request about alice. The paragraph added to `cockpit-api.md` says the same ("has never reported for
the account"). Harm: a misread diagnostic on the day someone chases a missing row. Fix: "has never
reported to this DevThrottle account" (or "to this tenant"), in both places.

## Nothing else found

The product change is one row, written once, after the answer, from checked words only, inside the
tenant, read by a fold the clients render verbatim, and seen by no other reader. The hosted tests run
the real Director code at every step except the browser, the login and the seal.
