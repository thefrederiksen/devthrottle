# Answers to the round 2 review of pull request 3854 (the contract types)

Review: `missions/license-switching/reviews/3854-round-2-review.md`. Proof:
`missions/license-switching/proof/license-messages-contract.md`, section "After-merge review".
Pull request 3854 had already merged as `efe526a1c`, so these fixes go forward in a follow-up pull request.

## Finding 1 - inherited members evade the no-key guard: accepted and fixed

The guard still reads each type with `DeclaredOnly`, but now walks the members itself: the type, every
base type whatever its name, and every interface it implements or extends. A property or instance field
found anywhere on that chain is reported under the `License*` type's own name, so a forbidden member
inherited from a base type named anything else is caught. The walk is done by hand because
`GetProperties` without `DeclaredOnly` still hides a base type's private members.

The probe test now includes `LicenseProbeInherited`, which declares nothing forbidden and inherits
`InheritedToken` (a property) and `InheritedSecret` (a private instance field) from `ProbeBaseWithKey`, a
base type whose name does not start with `License`. Both are reported. The base type sits outside the
test class on purpose: nested inside `LicenseMessagesCarryNoKeyTests` it would count as inside a
`License*` type and be found directly, which would prove nothing about inheritance.

Proven by mutation on the real file: `LicenseWindow` made to inherit `AccessToken` from a new
`MessageBase` turned the guard red with `LicenseWindow.AccessToken`. The same mutation against the guard
as it is on main stayed green, which is the gap the review found. Restored, green.

## Finding 2 - the declaration-form probes did not exercise type discovery: accepted and fixed

Discovery is now `LicenseTypes(candidates, namespace)`. The real scan calls it with every type in the
contracts assembly and the contracts namespace; the probe test calls the same method with the test's own
nested probe types and `ProbeBaseWithKey`, then hands what it discovers to `Offenders`. The test is
renamed `Scan_EveryDeclarationFormAndInheritedMember_IsCaught` and asserts the exact set of seven offenders.

Proven by mutation: adding `&& t.IsClass` to discovery turned the probe test red - the struct's
`AccessToken` was no longer reported. Restored, green.

## Finding 3 - the product-name command in the contract: declined (Tech Lead's decision)

The contract line names the `claude auth login` command, which the contract cannot describe without
naming. The repository rule targets attribution of the work to an assistant, and this is neither
attribution nor a mention of who wrote anything. The line stays as it is.

## Finding 4 - section 5.7 before 5.6: accepted and fixed

`contract.md` now has 5.6 ("The two sides disagree about the active account") followed by 5.7 ("A spare
whose sign-in is about to expire"). Only the order changed; the text of both sections is unchanged.
