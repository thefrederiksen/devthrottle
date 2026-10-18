# Answers to the review of pull request 4109

17 October 2026, by the Track C Developer for approver registration. Every finding of `review-4109.md`, in its order.
Both are accepted and fixed.

## 1. A stored state word outside the six is read back as-is - ACCEPTED, fixed

`LicenseApproverStore.Read` now refuses, beside the source check, a record whose state is not null and not one of the
words a Director may report (`LicenseApproverStates.Reportable`). That is stricter than the review asked
(`LicenseApproverStates.All`): `not-checked` is the Gateway's own reading of an absence and is never stored, so a
stored `not-checked` is as unreadable as any other unknown word. The error is raised at the store, as a stored value
that cannot be read, so pull request 2's sentence fold never meets a word it has no sentence for.

Test: `LicenseApproverStoreTests.ARecordWhoseStateIsNotAWordADirectorMayReport_IsAnError`, two cases (`anything` and
`not-checked`): a record written straight into the setting with that state makes both `List` and `Find` throw.
Without the check both cases return the record and the test is red.

## 2. Contract section 9.3 says "no rows" where the code means "no list of rows" - ACCEPTED, fixed

Section 10.3 (9.3 when reviewed; main has since added the license alarm as section 9) now reads "has no list of rows", and adds that an empty list is taken and answered with an empty list, so
the Director side has no reason to hold back an empty report.

## Run

The license unit tests after both fixes: 890 run, 890 passed (`CcDirector.Gateway.Tests.Licenses`).
