# Review: #2301 team invitations by email that expire

Pull requests: thefrederiksen/devthrottle#3526 (head 0e71caab7, only `a0c75ddb1..0e71caab7`) and
thefrederiksen/devthrottle_internal#2316 (head 611299c02, `origin/main...HEAD`). Written by a separate
review session, 3 Oct 2026.

## Scope

**Read in full (Gateway):** `Teams/TeamRegistry.Invitations.cs`, `Teams/TeamInvitationRules.cs`,
`Teams/TeamInvitationMailer.cs`, `Teams/TeamSeatConvergence.cs`, `Api/TeamInvitationEndpoints.cs`,
`Core/Account/TeamInvitationMailClient.cs`, the diffs to `TeamRegistry.cs`, `GatewayHost.cs`,
`GatewayDbContext.cs`, `TeamInvitationEntity.cs`, `HostedTeamsDarkTests.cs`, the two tenant-scope guard
tests; `AcceptInviteView.tsx`, `invitationsClient.ts`, the route table in `routes.tsx`. Read in part:
`TeamInvitationTests.cs` (the six issue tests, the bill gate, the seat sync), the hosted over-the-wire
test of the signed-out accept page, `InviteView.tsx` (only for decisions made in the client), and the
existing `MobileRedirect.cs`, `CockpitReactApp.cs`, `AuthMiddleware.cs`, `TeamSeatSync.cs` and the
client-core sign-in `next` handling, as far as the new accept link passes through them.

**Read in full (website):** `api/_lib/team-invitation-email.js`, `api/v1/team-invitations.js`, the
migration `20261003233000_team_invitation_email_read.sql`, the three small diffs; the test list of
`team-invitation-email.test.js`; the existing `email.js`, `email-log.js`, the `email_log` migration and
the admin read of it, and the `vercel.json` rewrite that routes `/api/v1/:resource/:path*`.

**Ran:**
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~TeamInvitation|FullyQualifiedName~TeamSeat"`
  on 0e71caab7: 143 passed, 0 failed, 1 skipped.
- `node --test api/_lib/team-invitation-email.test.js` on 611299c02: 33 passed, 0 failed. The file is
  inside the `test:unit` glob, so `npm test` runs it.

**Did not run / could not reach:** `Gateway.Tests` (the machine-wide lock) - so the dark test, the
over-the-wire tests and the PostgreSQL migration test are read, not run by me. The Supabase definer
proof (`run-team-invitation.sh`, needs Docker) - the SQL is read only. The Cockpit vitest suite. The
screenshots were not opened. The two generated migration designer files and the model snapshots were not
read line by line. Nothing was reproduced out of tree: F1 and F2 are from reading, and each names the
lines so they can be checked in a minute. Whether an invited person whose own trial has ended can hold a
device key at all (and so reach the accept page) depends on the hosted access gate and #2311; not judged.

**Held to the decisions and found sound:** who may invite whom is one pure function, enforced in the
registry on create, resend and cancel, against the caller's CURRENT role; a Manager cannot create,
resend or cancel a Manager invitation; nobody is invited as Owner. Accept is under the write lock, in one
save with the member row, refused a second time for any account. Day 7 / day 8 uses the injected clock.
Resend replaces the hash, so the old link is dead. The token is 32 random bytes, only its SHA-256 is in
the Gateway table, it is looked up by hash (so it cannot reach another team's invitation), it travels in
request bodies, and no route returns it. The website checks the service token before the body, treats a
recipient field as a hard 400 before anything else, reads the address only from the row, cross-checks the
team, compares the hash in constant time, refuses a non-waiting or expired invitation, and is 404 unless
`TEAMS_BILLING_ENABLED` is exactly `1`. The definer function pins `search_path = pg_catalog`, qualifies
every table, matches one primary key, and is granted to `service_role` only. The bill gate passes only
`active` / `past_due` and refuses an unreadable bill. The seat sync hangs only off
`CommitMembershipChange`, is skipped for team creation, is never reached by send / resend / cancel /
decline, and convergence calls at most once per team per pass and stops when the row matches. All eight
new Gateway routes are mapped inside the `CC_GATEWAY_TEAMS` branch, and the dark test proves absence by
status and by what was not written. Nothing new is linked from any screen a person without a team sees.

## Verdict

No blocker. Three should-fix findings (F1, F2, F3) and two notes. F3 needs a ruling from the Tech Lead
rather than only a code change, because two recorded decisions collide.

## Findings

### F1 - should-fix - #3526 - the invitation link does not work on a phone, and a phone writes the link's secret to the Gateway log

Location: `src/CcDirector.Gateway/Mobile/MobileRedirect.cs` (`ShouldRedirectToMobile`, and the log line
at 85), wired in `GatewayHost.cs:3923` after authentication; the new page `apps/cockpit/src/routes.tsx:87`
(`/invite/:token`); the link is built in the website's `team-invitation-email.js:118`.

The harm, two parts:

1. **The link is dead on a phone.** `ShouldRedirectToMobile` answers true for any GET with
   `Accept: text/html` from a phone browser whose path is not under `/mobile` or `/m`. `/invite/<token>`
   is such a path, so a signed-in phone is sent to the mobile app's front page and the invitation is
   dropped. A signed-out phone is first sent by the authentication step to `/signin?next=/invite/<token>`,
   and that navigation is then redirected to `/mobile` the same way; the mobile app has no invitation
   page (nothing under `apps/mobile` mentions one). An invitation arrives by email, and email is very
   often opened on a phone - the invited person presses "Accept or decline" and lands on a sign-in or a
   session list with no word about the team. Issue test 4 ("leads through sign-up and back to the accept
   page") holds on a desktop browser only; the hosted test sends no phone User-Agent, so it cannot see this.
2. **The secret is logged.** For the signed-in phone, `MobileRedirect.cs:85` writes
   `phone navigation {ctx.Request.Path} -> /mobile` with the raw path, which is `/invite/<token>`. The
   pull request redacts the access-log line (`GatewayHost.cs:3847`) and says the token is never logged;
   this second line, on the same request, is not redacted. The same is true of the unhandled-exception
   line at `GatewayHost.cs:3821`, which prints the raw path. Anyone who reads the Gateway log within the
   7 days can join that team in the invited role (any signed-in account holding the link may accept).

Why it must change: the feature's one entry point fails for a large share of the people it is sent to,
and the "token is never logged" claim is false on that same path. A fix needs a test that sends a phone
User-Agent to `/invite/<token>` and asserts both where the browser ends up and that no log line holds the
token.

Developer answer: Accepted, fixed in devthrottle#3526 at 3f9017d5e. The phone now gets the same accept page a desktop gets - one short page that reads on a narrow screen - rather than a new mobile page: `MobileRedirect.IsInvitationRoundTrip` keeps three addresses off the redirect, `/invite/<token>`, `/signin` only when its `next` is an accept page, and the Cockpit's own `/device-callback` (a phone reaches that only from a sign-in the Cockpit's sign-in page started; the mobile app returns to `/mobile/device-callback`). Every other phone navigation still goes to `/mobile/`, including `/signin?next=/sessions`. The secret is now cut out of every log line that prints a request path on these requests: the mobile redirect line, the Cockpit's browser-navigation line (`CockpitReactApp.cs:105`, which also printed it on every DESKTOP load - not in the finding, found while checking), the session-key refusal line in `AuthMiddleware`, and the unhandled-exception line, all through the same `RedactForLog` the access log uses. Tests: `HostedTeamInvitationEndpointsTests.PhoneAtTheAcceptPage_ReachesItSignedInAndThroughSignIn_NeverTheMobileApp_AndNoLogLineHoldsTheSecret` (iPhone User-Agent; signed in it reaches the Cockpit, signed out it goes to `/signin?next=/invite/<token>` and that page and `/device-callback` are the Cockpit's, never a 302 to `/mobile/`; `/sessions` and a plain `/signin` still go to `/mobile/`; no captured log line holds the token) and `MobileRedirectTests.ShouldRedirect_phone_invitation_round_trip_is_not_redirected_and_everything_else_still_is` (8 cases). Both are in Gateway.Tests, which is behind the machine lock the #2302 gate holds - see the run line at the end of the hand-back; they are not yet run.

### F2 - should-fix - #3526 - a person's email address is written to the Gateway log on every refused answer to an expired, cancelled or declined invitation

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.Invitations.cs:469` (`InvitationRefused` logs
`{reason}`), fed by `StateRefusal` at 331-339, with the sentences in
`src/CcDirector.Gateway/Teams/TeamInvitationRules.cs:150-157`.

The harm: `Expired`, `Cancelled` and `Declined` build their sentence with `view.InvitedBy`, and
`InvitedBy` is `DisplayFor(...)`, which is the inviter's account email whenever one is recorded. The
unit test pins exactly that: "Ask owner@acme.example to send a new one." `AcceptInvitation` (268) and
`DeclineInvitation` (307) pass that sentence to `InvitationRefused`, which writes it to the log in full.
So each time someone opens a dead link and presses a button, the Owner's or Manager's email address goes
into the Gateway log. The mandate forbids an email in the logs, and the class's own header says "The
address, the subjects and the accept token are never logged."

Why it must change: it is personal data in a log, on an ordinary and repeatable path. The log line needs
the outcome and the kind of refusal, not the sentence shown to the person. No test asserts the log is
free of addresses, so nothing would catch it coming back.

Developer answer: Accepted, fixed at 3f9017d5e. Every refusal now carries a short kind (`cancelled`, `declined`, `expired`, `already-a-member`, `bill-stopped` and so on - never an address, a name or a token), and `InvitationRefused` logs `REFUSED (<outcome>) kind=<kind>` only; the sentence goes to the person and nowhere else. `OpenInvitation`'s log line records `refusal=<kind>` the same way. I checked every other log line in the invitation code; none prints a sentence or an address. Test: `TeamInvitationTests.RefusedAnswers_LogTheKind_NeverTheSentence_SoNoAddressReachesTheLog` - a cancelled, a declined and an expired invitation each opened, accepted and declined, plus two refused creates, with the log captured: every kind is present, the sentence still names the inviter by address, and no captured line contains an `@` at all. The invitation unit tests now run in the log-capture collection so they can read the log.

### F3 - should-fix - #2316 - the link's secret is stored in plain text in the email log, so "only a hash at rest" does not hold

Location: `website/api/_lib/team-invitation-email.js:118` (the link, with the token, goes into both
bodies) and 224-230 (`sendEmail` with `kind: team-invitation`); the existing `website/api/_lib/email.js`
`sendEmail` then records the subject, `html` and `text` in `public.email_log`; the existing admin read
at `website/api/v1/admin.js:818` selects `html` and `text_body` for the admin screen.

The harm: two decisions were both followed and they contradict each other. The track log says the accept
token is "stored only as a hash"; decision D4 says every invitation email is written to the email log
with its body. The body contains the working link. `public.email_log` is in the same database as
`gateway.team_invitations`, so the plain token sits beside its own hash for the whole 7 days, and is
shown, clickable, on the admin screen. Because any signed-in account holding the link may accept (the
Tech Lead's ruling, which made the hash a condition), anyone who can read `email_log` - the admin screen,
the service role, a database copy or backup - can join any team with a waiting invitation in the invited
role, a Manager's included, and take a paid seat on that Owner's bill. The entity's comment "a copy of
this table cannot be turned into working links" is true of the table and not of the database.

Why it must change: the property the hash was ordered to give is not delivered. It needs a ruling, then
code either way: store the logged copy with the token cut out of the link (the person's own email still
carries it), or record in writing that the email log is accepted as holding live invitation links and
correct the comments that say otherwise. The test at `team-invitation-email.test.js:222` currently pins
that the logged `html` equals the sent `html`, link included.

Developer answer: Ruling applied, devthrottle_internal#2316 at 64476bbd. `sendEmail` takes an optional `logCopy` ({ html, text }) that `email_log` records instead of the sent body; without one it logs exactly what was sent, as before (`bodyToLog`, pure and tested). The invitation email passes a copy with the link replaced by `[accept link removed]` in both bodies; the email the person receives still carries the link. The test at `team-invitation-email.test.js:222` now pins the opposite of what it did: the logged row's `html` and `text_body` hold neither the token nor `/invite/`, they hold `[accept link removed]`, everything else equals what was sent, and the sent email still holds the token. The good-call test also checks the `logCopy` the route hands to `sendEmail`. Website `npm test`: 786 passed, 0 failed; build passes.

### F4 - note - #3526 - resending an expired invitation skips the two checks that creating one makes

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.Invitations.cs:164-191` (`ResendInvitation`)
against 93-101 (`CreateInvitation`).

The harm: create refuses an address that already has a waiting invitation or already belongs to a
member, but it allows a new invitation once the old one has expired, and the expired row stays
resendable. Resend checks neither. So after "expired, invited again", pressing Resend on the old row
gives one address two live links, and each link admits a different account; pressing it after the person
has joined sends an invitation to someone already in the team. Small, and only an Owner or Manager can
cause it, but it is one invitation admitting two people, which the Owner pays for. Either apply the same
two checks on resend or leave it and say so.

Developer answer: Accepted, fixed at 3f9017d5e. Create's two checks are one helper now, `AddressRefusal`, and resend runs it too, ignoring the invitation being resent: another waiting invitation for the address refuses with "already has an invitation waiting", and an address that belongs to a member refuses with "already a member". Tests: `ResendInvitation_AnExpiredOneSinceReplaced_IsRefused_SoOneAddressNeverHoldsTwoLiveLinks` and `ResendInvitation_AnExpiredOneWhosePersonHasSinceJoined_IsRefused`.

### F5 - note - #3526 - accepting does not look at the bill

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.Invitations.cs:252-285` (`AcceptInvitation`,
`AcceptRefusal` at 321-328).

The harm: the bill gate runs on create and resend only. An invitation sent while the bill was `active`
can still be accepted after the bill is `canceled`; the member is added in a paid role, and the
convergence pass skips a cancelled bill, so nothing reconciles it. The seam (section 5) grants a
cancelled team nothing, so I see no free use from it - which is why this is a note. The brief does not
decide the case; it is for the Tech Lead to say whether accept should refuse in plain words when the
team's bill has been cancelled.

Developer answer: Ruling applied at 3f9017d5e. Accepting passes the same bill gate as inviting, after the state and already-a-member checks: active or past_due joins; anything else is refused with "The team's bill has stopped, so nobody can join the team right now. Ask the team's Owner."; a bill that cannot be read is Unavailable (503) with "DevThrottle could not check the team's bill just now, so you have not joined yet. Try again shortly." The accept page shows the same refusal before the button is pressed, because opening uses the same check. Tests: `AcceptInvitation_BillCancelledSinceItWasSent_IsRefusedInPlainWords_AndNobodyJoins` (no member, no seat sync call), `AcceptInvitation_BillPastDue_StillJoins`, `AcceptInvitation_BillCannotBeRead_IsUnavailable_AndNobodyJoins`.
