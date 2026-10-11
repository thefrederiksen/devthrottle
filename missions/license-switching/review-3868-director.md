# Pull request 3868 — Director review

## Scope

Reviewed `origin/main...HEAD` (`c4fdc033a` to `14c902305`), including the Director license command adapter, report loop, key-holder lock, result store, switch coordinator and executor, startup/tunnel wiring, and the touched tests. Read `MISSION.md` sections 5 and 7 and `contract.md` in full.

The switch path follows the specified order through the preflight checks, handles completed-result replay, waits for the same in-flight `SwitchId`, rejects a different in-flight ID, reads back the active account, and requests an after-switch report. The report loop keeps the prior usage numbers and `ReadAtUtc` when a read fails. The key-holder lock uses an exclusive file handle; only its holder reports and executes exchanges. The command adapter resolves `cc-devthrottle` under this Director's installed Python environment, never through `PATH`; a missing tool produces an actionable list/read error or a failed unknown-state switch result, rather than a success.

## Findings

### P1 — Unrecognized CLI error values can carry provider text to the Gateway

`ReadUsageRows` deserializes `readError` as an unrestricted string. For an unrecognized value, `ReadErrorSentence` interpolates it into `ReadErrorDetail`, which is then included in the report sent to the Gateway. The switch parser validates `outcome` but not `reasonCode`; an unrecognized reason is logged and interpolated into the result `Message`. These are process-output-to-log/report paths, so a provider response or other sensitive text placed in either field crosses the boundary that contract rule 6.1 forbids. See [LicenseUsageReporter.cs:293](src/CcDirector.Core/Licenses/LicenseUsageReporter.cs:293), [LicenseCommandLine.cs:138](src/CcDirector.Core/Licenses/LicenseCommandLine.cs:138), and [LicenseSwitchCoordinator.cs:249](src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:249).

The marker tests cover an unsafe `ReadErrorDetail` and `LicenseExchangeAnswer.Message`, both of which are overwritten or ignored. They do not exercise an unknown `ReadError` or `ReasonCode`. Reject values outside the contract vocabularies and use a fixed generic sentence for unknown values before logging or reporting them.

### P2 — The Director does not refuse a spare whose sign-in is expired

Contract 3.3 step 9 requires `spare-not-usable` when the target's `SignInExpiresAtUtc` is at or before the Director's clock. The coordinator checks only `unauthorized` and `refresh-failed`, then calls the exchange command. The reporter retains expiry timestamps, but exposes no expiry lookup to the coordinator; the `license list` rows also contain only account and role. Thus a target known locally to have expired can still be made active. See [LicenseSwitchCoordinator.cs:144](src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:144).

The tests prove expiry is reported and preserved after a failed read, but do not prove an expired target is refused. This can move a machine from its current sign-in onto one the Director already knows has expired.

### P2 — A `switch-in-progress` result is not replayed by `SwitchId`

When a different ID arrives during a running switch, `Refuse` returns a completed `LicenseSwitchResult` but does not save it. If the Gateway times out before receiving that answer, its contract requires retrying the same ID. After the first switch completes, that retry is treated as new and can execute, so the same ID does not replay the stored refusal for 24 hours as required by contract 3.6. See [LicenseSwitchCoordinator.cs:65](src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:65) and [LicenseSwitchCoordinator.cs:193](src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:193). The current test checks only the first refusal, not a retry after the other switch finishes.

## Verification and limits

Ran only the two touched test projects:

- `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj` — passed, 1,528 tests.
- `dotnet test src/CcDirector.Avalonia.Tests/CcDirector.Avalonia.Tests.csproj` — passed, 1,044 tests.

These tests use an in-memory fake for switch/report behavior and hand-built JSON for the process-output parser. The CLI tests inject a null resolver or test path resolution; none launches `cc-devthrottle`. The test runs therefore did not touch the real default Claude login. A normal Director launch does start the reporter; with no isolation environment variable and a Gateway that advertises `PushLicenseUsage`, it invokes the installed CLI without `--default-config-dir`, using the real default Claude config. That is the product path; the external CLI's claimed phase-one refusal of real default config was not verifiable from this C# checkout.

`MISSION.md` section 7 also requires an isolated live-swap proof script for Windows and Mac. No such script is present under `missions/license-switching/`, and this review did not run a real swap. The fake-based tests do not prove the installed Python CLI's atomic exchange or the running-process account change.
