# Answers to the review of pull request 4127

17 October 2026, by the Track C Developer for approver registration. Every finding of `review-4127.md`, in its order.
All three are accepted and fixed.

## F1 - WhyNotReady with a blank or non-email account - ACCEPTED, fixed

`LicenseApproverReadiness.WhyNotReady` now throws `ArgumentException` (parameter `accountEmail`) when the account,
lower-cased and trimmed, is not an account email by `LicenseApproverStore.IsAccountEmail` - the same rule every
Licenses route refuses `invalid_account` with. It never folds a sentence around nothing.

Test: `LicenseApproverReadinessTests.WhyNotReady_ForAMissingOrNonEmailAccount_IsRefused_NeverASentenceWithAHole`, three
cases (null, spaces only, "not-an-email"). Without the check each returns "No machine is set to approve sign-ins for
. ..." and the test is red.

## F2 - A literal tab in the gate command, and the gate commit misplaced - ACCEPTED, fixed

The command in `c1-readiness.md` now reads `.\scripts\test-local.ps1 -Parked` with no tab (the file holds no tab at
all), and the sentence now says `9f79abe41` is the branch's commit on top of `origin/main` after 4109 merged, whose tree
differs from the pull request's head only in that file - not that it is on `origin/main`.

## F3 - A wrong-account row whose shown account is the account - ACCEPTED, fixed

`LicenseApproverRegistration.Checked` now refuses, as `invalid-row`, a `wrong-account` row whose shown account is the
account it checked: the mirror of the existing rule for a `can-approve` row showing another account. Such a row is
never stored, so it never folds into "signed in as alice@example.com, not as alice@example.com".

Test: a new case of `LicenseApproverRegistrationTests.Report_ARowOfTheWrongShape_IsRefusedWhole_AndStoresNothing`,
"wrong account showing the account itself".

## The notes

N1 (the Director's detail is screened but not proven fixed words) is for whoever reviews Developer C2's check. N2 (the
relay still asks a connected approver whatever its health says) is as designed; `canApproveNow` is a display verdict
and promises nothing about the relay. N3 needs no change: no writer can store a check time without a state.

## Run

License unit tests after the fixes: 958 run, 958 passed (`CcDirector.Gateway.Tests.Licenses`).
