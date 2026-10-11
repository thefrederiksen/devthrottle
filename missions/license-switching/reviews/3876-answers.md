# Answers to the review of pull request 3876 (the 3854 follow-up)

Review: `missions/license-switching/reviews/3876-review.md`. Proof:
`missions/license-switching/proof/license-messages-contract.md`, section "After-merge review", part "Review of 3876".

## New finding 1 - inherited interface properties have no probe: accepted and fixed

The probe test now includes `LicenseProbeInterface`, a `License*` interface that declares nothing forbidden
and extends `IProbeWithKey`, an interface whose name does not start with `License` and which declares
`InterfaceToken`. `IProbeWithKey` is handed to discovery as a candidate and left out by it, so the property
can be reported only through the interface walk, as `LicenseProbeInterface.InterfaceToken`. Like
`ProbeBaseWithKey`, it sits outside the test class so it is not found directly.

Proven by mutation: with the `GetInterfaces()` walk removed from `SelfAndAncestors`, the probe test turned
red - `LicenseProbeInterface.InterfaceToken` was missing. Restored, green.

## Round 2 answers 1, 2 and 4

The reviewer confirms all three are fixed. No further change.

## Round 2 finding 3 - still declined

The reviewer records dissent: in their reading, the repository rule prohibits any mention of the product
name in documentation, so the reason for declining does not match the rule's literal wording. The dissent
is noted here. The Tech Lead keeps the decision, for the reason given in `3854-round-2-answers.md`: the
contract line names the sign-in command, which the contract cannot describe without naming, and it is
neither attribution nor a mention of who wrote anything.
