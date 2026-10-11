# Proof - the License Switching contract types (`LicenseMessages.cs`)

Task: phase 2, Gateway storage and ingest, part 1 - every type and constant named by
`missions/license-switching/contract.md`, so phase 1 (the Director) and phase 2 build on one file.

## What is in it

- `src/CcDirector.Gateway.Contracts/LicenseMessages.cs`: `LicenseUsageReport`, `LicenseHolding`, `LicenseWindow`,
  `LicenseExtraUsage`, `LicenseSwitchRequest`, `LicenseSwitchResult`; the vocabularies `LicenseReportTriggers`,
  `LicenseReadErrors`, `LicenseSwitchReasons`, `LicenseSwitchOutcomes`, `LicenseSwitchReasonCodes`; and
  `LicenseContract` with the hub method name, the verb, the switch id shape and every number in contract section 7.
- `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs`: contract section 6 rule 1. The
  contract says "the contracts' test project"; there is none, so the test sits in the Director's unit tests, which
  reference the contracts project and run in the default local gate.

## Commands and results (2026-10-11, Windows machine)

`dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --filter "FullyQualifiedName~LicenseMessagesCarryNoKeyTests"`
- Passed 10, failed 0.

Mutation: added `public string? AccessToken { get; set; }` to `LicenseWindow`, ran the same filter:
- Failed 1 (`LicenseTypes_NoPropertyNamedLikeASignInKey`, collection `["LicenseWindow.AccessToken"]`), passed 9.
  Restored the file; the diff is clean of it.

`.\scripts\test-local.ps1`
- Completed: Engine 103, HostedAgent 89, Launcher 218, Terminal.Avalonia 82, Reclaim 310, setup 39,
  setup-engine 875, setup-cli 74 - all passed.
- Core.UnitTests and Avalonia.Tests were STOPPED over the 120-second ceiling, with the machine at 100% processor
  from other sessions. Run directly instead:
  - `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --no-build`: passed 1466, failed 0.
  - `dotnet test src/CcDirector.Avalonia.Tests/CcDirector.Avalonia.Tests.csproj --no-build`: first run passed
    1021, failed 1; second run passed 1022, failed 0. This change touches no Avalonia code; the first-run failure
    was not reproduced.

Parked suites: this change adds types only, read by nothing yet, so `-Parked` is run with the Gateway storage
and ingest pull request that first uses them.
