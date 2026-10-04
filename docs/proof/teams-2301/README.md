# Proof - team invitations by email (devthrottle_internal#2301)

Branch `teams/2301-invitations` (Gateway and Cockpit) and `teams/2301-invitation-mail` in devthrottle_internal (the
website route that sends the email), 3 October 2026. Everything below ran locally on SOREN_NORTH. No production
database, no deploy, no Stripe, and no email was sent: every test that sends stubs the sender, and the screenshot rig
records the send instead of making it.

- `api-transcript.txt` - the whole flow over real HTTP on a hosted Gateway with Teams released: the bill gate
  refusing, the invite options, invite, open the link signed in, accept, a second accept refused, list, resend,
  cancel.
- `s2-*.png`, `s3-*.png` - the two screens, taken from a local hosted Gateway with Teams released, serving the
  built Cockpit (`take-screenshots.py`, which runs the rig `TeamInvitationProofRig` in Gateway.UnitTests). The
  accounts are the fleet's test accounts only (qa@, tech@, dev@ and docs@ mindzie.com), seeded into a scratch
  database that is deleted afterwards; the invitation mailer records and sends nothing.

| Screenshot | What it shows |
|---|---|
| `s2-invite-owner.png` | S2 for the Owner: all three roles offered, each with who pays; three waiting invitations with Resend and Cancel |
| `s2-invite-owner-after-sending.png` | The Owner sent one: the confirmation, and the new invitation at the top of the list |
| `s2-invite-manager.png` | S2 for a Manager: Manager is offered but disabled, with the reason "Only the Owner can invite a Manager." |
| `s3-signed-in-1-open.png` | S3 signed in: who invited, which team, which role, who pays, and which account is about to join |
| `s3-signed-in-2-joined.png` | After Join the team |
| `s3-cancelled-link.png` | A cancelled invitation's link: refused in plain words, naming who to ask |
| `s3-signed-out-1-sign-in.png` | S3 signed out: the browser is sent to sign in, carrying `next=/invite/<token>` (printed by the driver) |
| `s3-signed-out-2-back-on-the-invitation.png` | Signed in as the new account: back on the same invitation |
| `s3-signed-out-3-joined.png` | The new account joined, as a Collaborator |

## Dark until released

The invitation routes are mapped only when `CC_GATEWAY_TEAMS=1`, inside the same switch as #2300's team routes.
`HostedTeamsDarkTests.SwitchUnset_EveryInvitationRouteIsAbsent_AndNothingIsInvitedOrJoined` proves the default.
The website route answers 404 unless `TEAMS_BILLING_ENABLED=1` (Track A's switch).
`TeamInvitationEndpointsTests.SessionKeyGuard_EveryInvitationRoute_IsRefusedToAnAgentSessionKey` pins that an agent's
session key reaches none of them.

## The six tests from the issue

| Issue test | Where it is proven |
|---|---|
| 1. Owner invites any role below Owner; a Manager only Developer and Collaborator; a Developer or Collaborator nobody | `TeamInvitationTests.Issue2301Test1_...`; `TeamInvitationRulesTests` (the one rule, every pair); `HostedTeamInvitationEndpointsTests.ManagerInvitingAManager_IsForbidden_AndADeveloperInvitingAnyone_IsForbidden` (over the wire) |
| 2. Accepting creates the membership with the invited role, once | `TeamInvitationTests.Issue2301Test2_...`; `HostedTeamInvitationEndpointsTests.OwnerInvites_InviteeAcceptsOverTheWire_...` |
| 3. An expired, cancelled or declined invitation cannot be accepted, and says why | `TeamInvitationTests.Issue2301Test3_...`; `ResendAndCancel_OverTheWire_RenewTheLinkAndThenStopIt`; screenshot `s3-cancelled-link.png` |
| 4. An address with no account signs up and comes back to accept | `TeamInvitationTests.Issue2301Test4_...`; `HostedTeamInvitationEndpointsTests.SignedOutBrowserAtTheAcceptPage_IsSentToSignInAndBackToTheSamePage_...`; Cockpit `inviteRoutes.test.tsx` (the redirect to `/signin?next=/invite/<token>` and the return); screenshots `s3-signed-out-*.png` |
| 5. Any email domain may be invited | `TeamInvitationTests.Issue2301Test5_AnyEmailDomain_IsAccepted` |
| 6. Accepted on day 7 works; on day 8 it is expired | `TeamInvitationTests.Issue2301Test6_...`; `TeamInvitationRulesTests.EffectiveState_SentInvitation_ExpiresExactlySevenDaysAfterItWasSent` |

## The decided extras

| Decision | Where it is proven |
|---|---|
| The bill gate: no invitation until the team's bill is `active` or `past_due` (Tech Lead ruling), refused in the brief's words | `CreateInvitation_TeamWithNoBill_IsRefusedWithThePlainWordsReason`, `CreateInvitation_ByBillStatus_OnlyAStartedBillAllowsIt`, `CreateInvitation_BillCannotBeRead_IsRefusedAsUnavailableAndStoresNothing`, `InviteOptions_NoBill_IsBlockedWithTheBillReason`; over the wire `TeamWithNoBill_InvitationIsRefusedWithThePlainWordsReason_AndNoEmailIsAsked` |
| Seat sync after accept, remove and role change, from `CommitMembershipChange` only; a failed call retried by convergence, which reads the answer and stops | `SeatSync_AcceptRemoveAndRoleChange_EachCallSyncOnceWithTheTeamId`, `SeatSync_SendingResendingCancellingAndDeclining_NeverCallSync`, `SeatSync_AFailedCall_IsRetriedByConvergence_AndConvergenceStopsOnceTheCountsMatch`, `SeatConvergence_TeamWithNoBill_IsSkippedWithoutACall` (HTTP stubbed) |
| The Gateway names an invitation, never an address | `TeamInvitationMailerTests.SendInvitationAsync_SendsOnlyTheInvitationIdWithTheServiceHeader` (the body is exactly `invitation_id`, `team_id`, `token`); website `team-invitation-email.test.js` (a recipient field is a hard 400; another team's invitation is refused; a token that does not match the stored hash is refused; nothing is sent in any of them) |
| Only the hash of the link's secret is stored; the website checks it before building the link; resend replaces it | `CreateInvitation_StoresOnlyTheHashOfTheLinksSecret`, `ResendInvitation_StartsANewSevenDays_AndOnlyTheNewestLinkWorks`, `HashAcceptToken_IsLowerCaseHexSha256_TheFormTheWebsiteComputesToo` (the website's `hashAcceptToken` is tested against the same value) |
| The row records which account accepted, visible to the Owner and Managers | `AcceptInvitation_RecordsWhichAccountUsedTheLink_ForTheOwnerToSee` |
| The link's secret never reaches the access log | `RedactForLog_TheAcceptPagesSecret_IsCutOut_AndNothingElseChanges`; over the wire in `SignedOutBrowserAtTheAcceptPage_..._AndTheLogNeverHoldsTheSecret` |
| The invitations table on PostgreSQL: applies after `AddTeams`, the hash is unique, a team's deletion removes its invitations, Down removes the table | `AddTeamInvitationsPostgresTests` (real PostgreSQL, the run's own throwaway database) |
| The website's definer read, against a real PostgreSQL with two real teams | `website/supabase/tests/gateway-team-invitation.sql` - 28 of 28 assertions |

## Test runs (final code)

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (default gate), on 3f9017d5e | 10 suites, all `outcome=Completed`, 3,463 passed, 0 failed |
| `CcDirector.Gateway.UnitTests`, whole suite (`dotnet test`), on 3f9017d5e | 8,228 passed, 0 failed, 9 skipped (its PostgreSQL proofs, which only run under `-Parked`, and the screenshot rig) |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams\|FullyQualifiedName~MobileRedirect\|FullyQualifiedName~AddTeam"` - its own throwaway PostgreSQL, on 3f9017d5e | `executed=52`, 52 passed, 0 failed; includes the phone test, the `MobileRedirect` cases, `AddTeamsPostgresTests` and `AddTeamInvitationsPostgresTests` |
| Cockpit `npx vitest run` | 68 files, 594 passed |
| client-core `npx vitest run` | 140 files, 1,639 passed |
| `npm run typecheck` (all four workspaces), `eslint` on the changed folders | clean |
| Website `npm test`, on 64476bbd | exit 0; the node test runner reports 786 passed, 0 failed (includes `team-invitation-email.test.js`) |
| Website `npm run build` | exit 0 |
| Website `npm run test:team-invitation` (Docker PostgreSQL 16) | PASS, 28 assertions, all checked |

## Not run, and why

- **The whole `-Parked` run, and the whole `CcDirector.Gateway.Tests` and `CcDirector.Core.Tests` suites.** A single
  foreground command here is capped at ten minutes, and `-Parked` runs for tens of minutes. The Tech Lead runs the
  full `-Parked` gate. What ran of `Gateway.Tests` is every test whose name contains Teams or AddTeam.
  `Core.Tests` is named by the coverage-gap line because `TeamInvitationMailClient` is in `CcDirector.Core`; that
  client's tests are in `Gateway.UnitTests` (`TeamInvitationMailerTests`), which ran in full.
- **The real sign-up at devthrottle.com** is not driven in the screenshots: no account is created on the real
  identity provider. The screenshots show the signed-out browser sent to sign-in carrying `next=/invite/<token>`,
  then - with the new account's device key in the browser, which is what a finished sign-up leaves - the same
  invitation page, and the join. The redirect and the return are also proven by `inviteRoutes.test.tsx` and the
  over-the-wire test above.

## The review and its answers

The review of both pull requests, and the answer to each finding, is in the mission record:
`docs/missions/teams-v1-2026-10-03/reviews/review-2301.md` in devthrottle_internal. F1 (the link on a phone, and the
secret in two more log lines), F2 (an address in the log), F4 (resend's missing checks) and F5 (accept and the bill,
Tech Lead ruling) are fixed in 3f9017d5e; F3 (the link in the email log, Tech Lead ruling) in devthrottle_internal
64476bbd. The screenshots above were taken before those fixes; none of the fixes changes what the two screens show
on a desktop.
