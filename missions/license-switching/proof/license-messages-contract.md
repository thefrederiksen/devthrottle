# Proof - the License Switching contract types (`LicenseMessages.cs`)

Task: phase 2, Gateway storage and ingest, part 1 - every type and constant named by
`missions/license-switching/contract.md`, so phase 1 (the Director) and phase 2 build on one file.

## What is in it

- `src/CcDirector.Gateway.Contracts/LicenseMessages.cs`: `LicenseUsageReport`, `LicenseHolding`, `LicenseWindow`,
  `LicenseExtraUsage`, `LicenseSwitchRequest`, `LicenseSwitchResult`; the vocabularies `LicenseReportTriggers`,
  `LicenseReadErrors`, `LicenseSwitchReasons`, `LicenseSwitchOutcomes`, `LicenseSwitchReasonCodes`; and
  `LicenseContract` with the hub method name, the verb, the shape of a switch identifier and every number in
  contract section 7.
- `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs`: contract section 6 rule 1. The
  contracts project has no test project of its own, so the test sits in the Director's unit tests, which
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

## Second round (2026-10-11): the review's findings and the sign-in expiry fields

Review: `missions/license-switching/reviews/3854-contract-types-review.md`. Answers:
`missions/license-switching/reviews/3854-answers.md`.

What changed:
- The guard (`LicenseMessagesCarryNoKeyTests`) now scans every `License*` type in the contracts
  namespace with no filter on its kind - class, struct, record, abstract, static, interface, and any type
  nested inside one - and inspects static and instance properties of any visibility plus instance fields.
  A new test, `Offenders_EveryDeclarationForm_IsCaught`, hands the scan one probe of each form (a
  read-only struct, an abstract class, a record, a static class with a static property, a class with a
  public field) and asserts each forbidden name is reported. Another, `LicenseTypes_FoundByTheScan_IncludeStaticTypes`,
  asserts the static classes are now in the scan.
- `LicenseHolding` gains `SignedInAtUtc` and `SignInExpiresAtUtc`; `LicenseContract` gains
  `SignInExpiryWarning` (3 days), asserted in `Constants_MatchContractSectionSeven`. Contract sections
  2.2, 2.5, 3.3 step 9, 5.7 (new), 6 rule 1 and 7 are updated to match.
- Comments in `LicenseMessages.cs` written out in plain English.

Commands and results (Windows machine). The full local gate was not run: the machine is short of
memory and the Tech Lead limited this round to these two commands.

`dotnet build src/CcDirector.Gateway.Contracts`
- 0 warnings, 0 errors.

`dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~LicenseMessages"`
- Passed 12, failed 0 (the 10 from the first round plus the two new tests).

Mutation on the real file, after committing the fix: appended
`public readonly struct LicenseFutureMessage { public string? AccessToken { get; init; } }` to
`LicenseMessages.cs` - the exact form the review showed passing all 10 tests in the first version - and
added `public static string? RefreshToken { get; set; }` to the static class `LicenseContract`. Same
filter:
- Failed 1, passed 11. `LicenseTypes_NoMemberNamedLikeASignInKey` reported
  `["LicenseContract.RefreshToken", "LicenseFutureMessage.AccessToken"]`.
- Restored with `git checkout -- src/CcDirector.Gateway.Contracts/LicenseMessages.cs`, rebuilt, ran the
  filter again: passed 12, failed 0.
