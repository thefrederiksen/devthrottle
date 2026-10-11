# Pull request 3854 round 2 review

Reviewed the round 2 changes by comparing `4f5fd5fa7` with `efe526a1c` for the three requested files.

Verdict: **changes needed**

## Round 1 answers

1. **Finding 1: fixed in the current implementation, but not fully proved by the added test.** `LicenseTypes()` currently has no declaration-kind filter and the static-type presence test reaches the production assembly scan. However, `Offenders_EveryDeclarationForm_IsCaught` passes its test-only probes directly to `Offenders`; it does not pass them through `LicenseTypes()`. The test therefore proves member-name inspection, but not that the scanner discovers each declaration form. See finding 2.
2. **Finding 2: the decline is not reasonable under the repository rule as written.** The reason distinguishes a product reference from attribution, but `CLAUDE.md` says any mention in code comments and documentation is prohibited. The rule does not provide that exception. The new contract text at line 144 also adds a prohibited product-name mention; see finding 3.
3. **Finding 3: fixed.** The reviewed comments spell out the time-zone, wire-format, hexadecimal, and identifier terms. The new expiry comments do not reintroduce those shortened forms.

## Findings

1. **Must fix — inherited members evade the no-key guard.** `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs:27` and `:30` set `DeclaredOnly` for properties and fields, while `MemberNames` reads only those declared members. A `License*` message can inherit a forbidden property or instance field from a base type whose name does not start with `License`; the scan will find the message type but will miss the inherited member. Walk the base types when collecting members, or otherwise make the guard include inherited members.
2. **Should fix — the declaration-form probes do not exercise type discovery.** `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs:97` passes the probes directly to `Offenders`. Since `LicenseTypes()` scans only the Gateway contracts assembly, those test-assembly probes never enter the discovered set. A future class-only filter in `LicenseTypes()` could omit structs while this probe test and the static-type presence test still pass. Make discovery testable with supplied types or add equivalent probes to the assembly the scan actually enumerates.
3. **Must fix — the repository's no-mention rule is contradicted by new contract text.** `missions/license-switching/contract.md:144` adds a product-name command reference. `CLAUDE.md:495` prohibits any such mention in documentation, independent of whether it is attribution. This also means the rationale declining round 1 finding 2 does not close that finding under the literal rule.
4. **Note — section order jumps backward.** `missions/license-switching/contract.md:455` introduces section 5.7 before the existing section 5.6 at line 466. Put the sections in numerical order for navigation.

## Contract consistency and evidence

`contract.md` and `LicenseMessages.cs` agree on the nullable `SignedInAtUtc` and `SignInExpiresAtUtc` fields, their Director-owned values, and unknown/null behavior. The contract's three-day `SignInExpiryWarning` matches the named constant and its unit-test assertion. Section 5.7 makes Gateway target eligibility depend on expiry relative to `NotAfterUtc`, defines all four Cockpit verdicts, and leaves comparison to the Gateway. Section 3.3 step 9 separately tells the Director to reject a known expiry at or before its current time. Those rules are clear enough for both phases to implement. I found no other behavior conflict in the reviewed delta.

Ran `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~LicenseMessages"`: 12 passed, 0 failed, 0 skipped. The full test gate was not run.
