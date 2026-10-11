# Pull request 3876 review

## Verdict

**Changes needed**

## Round 2 answers

1. **Finding 1: fixed for base-type inheritance, including private members.** `MemberNames` inspects each type in the base chain with `DeclaredOnly`, so it sees the private base field as well as the inherited property. The probe asserts both are reported under `LicenseProbeInherited`. The interface traversal is present, but its test coverage is incomplete; see new finding 1.
2. **Finding 2: fixed.** The probes pass through `LicenseTypes(candidates, namespace)`, the same discovery helper used by the real assembly scan. The expected `LicenseProbeStruct.AccessToken` result means adding a class-only filter makes this test fail.
3. **Finding 3: declined by the Tech Lead; reviewer dissent.** The repository policy at lines 495–496 says any mention of the named assistant product in documentation is prohibited. `contract.md:144` names its sign-in command, so the rationale that the rule applies only to attribution does not match the literal wording. This records disagreement with the rationale; disposition belongs to the Tech Lead.
4. **Finding 4: fixed.** Section 5.6 now precedes 5.7. The section text is unchanged.

## New findings

1. **Should fix — inherited interface properties have no probe.** `src/CcDirector.Core.UnitTests/Contracts/LicenseMessagesCarryNoKeyTests.cs:121` tests inherited members from a base class, but none of the candidates at `:128` is an interface. Removing the `GetInterfaces()` walk at `:75` would leave the focused test green. Add a `License*` interface that inherits a forbidden property from a non-`License*` interface and assert it is reported.

## Contract meaning

The complete `contract.md` diff only moves the existing 5.6 text before 5.7; both section bodies are unchanged. I found no other meaning change in the contract.

## Verification

Ran `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~LicenseMessages"`: 12 passed, 0 failed, 0 skipped. The full test gate was not run. This result covers the requested filtered tests; it does not exercise inherited interface properties.
