# Pull request 3854 contract types review

Verdict: **changes needed**

Reviewed pull request head `4f5fd5fa7` against base `54fd5da0d`.

## Findings

1. **Must fix - the reflection guard does not scan every `License*` type.**
   `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs:23` restricts the discovered types to non-abstract classes, and line 42 inspects public instance properties only. In an external scratch copy, adding `AccessToken` to the existing `LicenseWindow` made the intended test fail, and adding a new sealed `LicenseFutureMessage` class with that property also made it fail. Changing the new type to `public readonly struct LicenseFutureMessage` made all 10 tests pass while the forbidden property remained. A future value type, abstract type, or static property can therefore escape the rule that covers every `License*` type. This matters because the test is the build-time boundary preventing sign-in material from entering the shared contract; a green result must not depend on a future contributor choosing the expected declaration form. Enumerate every matching type before inspecting its public static and instance properties, or add a separate assertion that rejects unsupported declaration forms before the property check.

2. **Must fix - new source comments contain the product or vendor name that repository instructions prohibit in code comments.**
   `src/CcDirector.Gateway.Contracts/LicenseMessages.cs:5` and line 23 introduce the prohibited name. `CLAUDE.md` lines 482-502 make this an absolute rule for code comments and anything reaching GitHub. This matters because the prohibited text is already part of the pull request and would be preserved in repository history if merged. Rephrase those comments without changing the contract identifiers or behavior.

3. **Should fix - the new prose does not follow the repository's plain-English, no-short-forms rule.**
   Examples appear in `src/CcDirector.Gateway.Contracts/LicenseMessages.cs:26`, lines 63, 95-100, 112, 121, 146, and 220, plus `missions/license-switching/proof/license-messages-contract.md:11`. The prose uses a time-zone abbreviation, a wire-format initialism, and shortened forms for identifier and hexadecimal. `CLAUDE.md` lines 48-52 requires these to be written out in comments, reports, and documentation. This matters especially in this shared contract file: its explanations are meant to keep two phases aligned, so readers should not have to expand short forms. Contract-mandated identifiers such as `SentAtUtc` and framework names can remain exact; the surrounding prose should be written out.

## Scope and evidence

- Read all three changed files, contract sections 2, 3, 6, and 7, the relevant repository instructions, the default-gate project list, and comparable tunnel contract types.
- Confirmed the six classes, every property name, type, nullability, default, vocabulary value, and named constant match the contract. The public get/set shape matches the existing MessagePack tunnel contracts, and the filtered test exercised the `System.Text.Json` web naming and timestamp behavior.
- Ran `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --filter "FullyQualifiedName~LicenseMessagesCarryNoKeyTests"`: 10 tests ran and all 10 passed.
- Ran three mutations outside the repository: a forbidden property on an existing class failed with `LicenseWindow.AccessToken`; the same property on a new sealed class failed with `LicenseFutureMessage.AccessToken`; the same property on a new value type passed all 10 tests, proving finding 1.
- Confirmed `CcDirector.Core.UnitTests.csproj` is in `scripts/test-local.ps1`'s default project list.
- Found no real email address or machine name in the change. The only addresses use the reserved `example.com` domain, and the proof says only "Windows machine".
- `git diff --check 54fd5da0d..4f5fd5fa7` passed, and the review tree was clean before this review file was written.
- I did not run the full default gate or a live tunnel exchange. MessagePack compatibility was checked from the contract shape and the existing configured tunnel pattern, not from an end-to-end connection.
