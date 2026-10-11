# Pull request 3854, round 2 - merged without a second-agent review of the expiry change

Round 1 (a separate review session, head `4f5fd5fa7`) reviewed the whole contract types file and found
three things. All three were answered in `3854-answers.md`: findings 1 and 3 fixed, finding 2 declined
with a reason.

The round 2 head (`417d29415`) also added the sign-in expiry fields (`SignedInAtUtc`,
`SignInExpiresAtUtc`, `LicenseContract.SignInExpiryWarning`) and contract section 5.7. **That expiry
change was NOT reviewed by a second agent before merge.** Why:

- The second-choice review seat answered "You've hit your monthly spend limit" before reading anything.
- Every first-choice review seat started after about 11:10 local failed: on the configured default
  model the provider refused the model, and on a listed model the seat could not find
  `cc-devthrottle`, stopped before its work, and would not read a typed prompt that gave the full path
  (issues #3861 and #3862). Five seats were tried.

What was done instead, under the Delivery Lead's ruling of 2026-10-11 15:53 UTC: the Tech Lead read
the whole round 2 diff against the contract and ran
`dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~LicenseMessages"`: 12 passed,
0 failed. The round 1 fixes were checked in the same reading (the guard now catches a struct, an
abstract class, a record, a static class and an instance field, each proven by a probe test).

**Owed after merge:** a second-agent review of the round 2 change as soon as a review seat works; its
findings are fixed forward.
