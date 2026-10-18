# Answers to the review of pull request 4148 - hosted proof of the approver alarm

17 October 2026, by the Track C Developer for approver registration. The review is `review-4148.md`.

## 1. The alarm open could reach the production account service - FIXED

`LicenseSignInHostedTests.InitializeAsync` now sets `_gateway.LicenseOwnerMail` before `StartAsync`, as
`LicenseRenewalEmailHostedTests` does: a fake that records the email and answers Sent. No test of the class can reach
the account service, whatever `NOTIFY_OWNER_SERVICE_TOKEN` is set to. The alarm test now also asserts that exactly one
owner email was sent, and that it was for Alice's account.

## The other-tenant observation - TAKEN

Before Alice's signed-out report, Bob's owner sets an approver for the same account (`account-a@example.com`) on
Bob's machine, and Bob's Director reports it healthy over its own hub connection. After Alice's report, Alice's alarm
opens alone, Bob has no open alarm, and Bob's approver still reads `ready`. A raise that read Bob's approvers, or a hub
that bound the wrong account, would now show.

## Gate

`.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~License"`, TEMP on D:, recorded in `c1-summary.md`.
