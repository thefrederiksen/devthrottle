# Proof - Gateway storage and ingest of the usage report

Task: phase 2, the Gateway stores what each machine's Director reports (contract sections 1, 2, 4, 5.2, 5.7, 6, 7).

## What is in it

- `DirectorHub.PushLicenseUsage(long sequence, LicenseUsageReport report)` - refused before Hello (`RequireBoundDirector`),
  tenant from the binding, runs in `EnterBoundTenantScope()`, taken only from the Director's active connection
  (`PushedSessionStore.IsActiveConnection`) with a sequence higher than the last one accepted from that connection.
  A refusal, and a store failure, are logged and dropped, never an error to the Director. `HubMethods` lists it
  with no other edit (proven by a test).
- `src/CcDirector.Gateway/Licenses/LicenseUsageIngest.cs` - the gate, the shape check (no machine, two active
  holdings, an account twice, an empty email, a switch identifier of the wrong shape), and the clock skew rule of
  section 5.2: beyond two minutes the ages are judged from the receive time, logged once per machine.
- `src/CcDirector.Gateway/Licenses/LicenseStore.cs` - applies an accepted report as a complete snapshot in one save;
  writes `spare-added` / `spare-removed` log rows by comparing with the machine's previous holdings (none on a
  machine's first report: there is nothing to compare with); the switch log methods the decision engine uses
  (`CreatePendingSwitch`, `RecordSend`, `TryMark`, `TrySettle`, `FindSwitch`, `ListOpenSwitches`, `ListLog`) and
  the bars (`Bar`, `ListActiveBars`). A final switch row is never changed again.
- `src/CcDirector.Gateway/Licenses/LicenseReadModel.cs` - pure fold: accounts with their freshest reading from any
  machine and whether it is stale; machines with active account, spares, holdings with the sign-in expiry verdict
  of section 5.7, and staleness.
- Five tables, one migration each for SQLite (`20261011151110_AddLicenseSwitching`) and PostgreSQL
  (`20261011151344_AddLicenseSwitching`): `license_accounts`, `license_machines`, `license_holdings`,
  `license_switch_log`, `license_bars`. Every one is tenant-scoped and keyed by a Gateway-minted identifier. No
  column holds a sign-in key: emails, plan words, numbers, times, words and the Director's sentences only.
- `PostgresProviderProofTests` census: the license columns pinned to byte-ordinal collation, and the two
  `secret_transfers` columns that were declared but missing from the list (the test was red on main for them).

## Commands and results (2026-10-11, Windows machine, under heavy load from other sessions)

- `dotnet ef migrations has-pending-model-changes` for SQLite and for PostgreSQL (`CC_GATEWAY_EF_PROVIDER=postgres`):
  "No changes have been made to the model since the last migration." for both.
- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"`:
  passed 41, failed 0.
- Mutation: the hub passed `true` in place of `IsActiveConnection(...)`; the same filter failed 1
  (`PushLicenseUsage_FromASupersededConnection_IsDropped`), passed 34 (before the expiry tests were added). Restored.
- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --no-build`: passed 10463, failed 0.
- `.\scripts\test-database.ps1`: outcome Completed, 68 executed, 68 passed, 0 skipped (final migrations).
  An earlier run hit a read timeout under load that left one half-created database, failing 12 tests that could not
  open it; the rerun on the same code was green.
- `.\scripts\test-local.ps1`: Engine 103, HostedAgent 89, Launcher 218, Terminal.Avalonia 82, Reclaim 310, setup 39,
  setup-engine 875, setup-cli 74 - all passed. Core.UnitTests and Avalonia.Tests were stopped over the 120-second
  ceiling with the machine at full load; run directly: Core.UnitTests passed 1468, failed 0; Avalonia.Tests passed
  1022, failed 0.

## After rebasing onto main (contract types pull request 3854 merged)

- No migration landed on main after these two; `has-pending-model-changes` again says no changes for both providers.
- License, hub, tenant scope guard and migration tests in Gateway.UnitTests (filter
  `Licenses|DirectorHubTests|TenantScopeGuard|Migration`): passed 110, failed 0.
- `LicenseMessages` tests in Core.UnitTests: passed 12, failed 0.

## Not yet run: `.\scripts\test-local.ps1 -Parked`

Refused twice by the machine-wide locks: first by another Developer's Gateway.Tests run (over 90 minutes), then by
the release seat's `-Parked -Configuration Release` gate, which still held the Gateway.Tests lock after more than
95 minutes. Of the three parked suites, Gateway.UnitTests ran in full above (10463 passed). Gateway.Tests and
Core.Tests have not run on this change; this section is updated with their counts when the lock frees.
