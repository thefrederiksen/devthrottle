# Review: #2303 the Team page (pull request thefrederiksen/devthrottle#3529)

Written by a separate review session, 4 Oct 2026. Head reviewed: e10864530, as `git diff a8608a015 HEAD`
in the worktree `D:\ReposFred\devthrottle-teams-2303-review`. No file in that worktree was edited.

## Scope

**Read in full (the change):** `Api/TeamEndpoints.cs` (the three new routes and their answers),
`Teams/TeamRegistry.TeamPage.cs`, the `CommitMembershipChange` change in `Teams/TeamRegistry.cs`,
`Teams/TeamEndpointRules.cs`, `Teams/TeamPermissions.cs`, `apps/cockpit/src/team/TeamPageView.tsx`,
`apps/cockpit/src/routes.tsx`, `packages/client-core/src/teams/teamPageClient.ts`, and every changed or new test:
`TeamPageTests.cs`, `HostedTeamPageEndpointsTests.cs`, the added test in `HostedTeamsDarkTests.cs`,
`RoleTableSpec.cs`, `TeamInvitationTests.cs`, `TeamPageProofRig.cs`, `teamPage.test.tsx`, `teamPageClient.test.ts`.
Also the proof folder's `README.md` and `revert-proofs-gateway.txt`.

**Read as far as needed to judge the change (not under review):** `TeamAccess.cs`, `TeamEndpointGate.cs`,
`TeamInvitationRules.cs`, `TeamRegistry.Invitations.cs`, `TeamSeatConvergence.cs`, the rest of `TeamRegistry.cs`,
the seam document `seam-team-billing.md`, issues #2303 and #2098, the Developer's brief, and the S1 rows of the mockup.

**Ran, in the review worktree:**
- `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"` (built, not `--no-build`):
  587 passed, 0 failed, 2 skipped (the two proof rigs, which skip by design).
- `npx vitest run src/team/teamPage.test.tsx` in `apps/cockpit`: 7 passed.
- `npx vitest run src/teams/teamPageClient.test.ts` in `packages/client-core`: 3 passed.

**Could not reach, or did not do:**
- `CcDirector.Gateway.Tests` was NOT run - it queues behind the machine-wide lock the Tech Lead's gate run holds.
  So the three routes over real HTTP (`HostedTeamPageEndpointsTests`, `HostedTeamsDarkTests`) and the route-table
  walk were read, not run, by this review. What they claim is consistent with the gate and rule code I read.
- The whole unit suite and the whole web suites were not run, only the Teams filter and the two new web test files.
- No revert proof was repeated: this seat may not edit files. The Developer's four Gateway revert proofs were read;
  each names a rule under "Look hardest at" and the tests it lists do assert that rule.
- The screenshots were not opened. The proof rig and its driver were read only far enough to see the rig skips by
  default and mails nobody.
- The website's sync-seats route (parked in devthrottle_internal#2315) was not read. The judgment on the seat-sync
  change below rests on the seam document's statement that the website counts paid seats itself from the membership
  table and that the convergence pass repairs a missed call.

**What I checked and found sound (so an empty list here is not a check that never ran):**
- Who may do what. Change role asks `TeamAccess.Decide` for "make someone a Manager, change roles"; remove asks it
  for `ActionToAddOrRemove` of the target's role; the Owner as target is refused before the table is asked (nobody
  removes the Owner, the Owner cannot remove themselves); on the hosted Gateway the gate asks the same table first.
  I found no second permission rule. The two direct reads of the table (`RemoveRefusal`, `TeamInvitationRules.MayInvite`)
  choose words or mirror the same cell the write asks; they cannot grant what `Decide` refuses.
- `SeeTeamPage`. It does not contradict #2098: `SeeMembersAndRoles` stays yes for all four roles, and
  `GET /teams/{teamId}/members` is untouched, so a Collaborator still reads the member list. The new row is #2303's
  own sentence "Collaborator: no Team page".
- The seat sync. Counting paid members from the database before and after the save, under the write lock, gives the
  right answer on every path I traced: Collaborator to a paid role and back (moves), paid to paid (does not), remove
  a paid member (moves), remove a Collaborator (does not), an accepted invitation of each role, a role "changed" to
  itself (never reaches the commit), and creating a team (left out as before). Two changes cannot interleave inside
  one process. A call that fails after the save is repaired by the 15-minute convergence pass exactly as before.
  Changing #3526's behaviour is safe on the Gateway side: the call carries no number, so skipping it when the count
  did not move loses nothing the website would have read.
- Rule 7. The page renders `canChangeRole`, `roleChoices`, `canRemove`, `removeWarning`, `seat`, `summary`,
  `canInvite`, `canResend`, `canCancel` as given and never reads a role to decide an action. The field names in the
  client's types match the names the route writes.
- Edge cases. A role outside the four, a missing role, and a body that is not JSON are 400; "Owner" as the new role
  is refused by the team's own rule; the Owner changing their own role is refused; a member id from another team
  names nobody. There is no "last Manager" rule to break - a team may have none.
- A person with no team. Nothing here is reachable without a team id the caller is a member of, and all three routes
  are mapped only when Teams is released.
- Logging. No account subject or email is written by the new code: refusals are logged by kind, members by count,
  the team by its hashed form, and the member id in the address is an opaque hash.

## Verdict

**Sound to merge after F1 is answered.** No blocker. One should-fix and three notes.

## Findings

### F1 - should-fix - an expired invitation can never be cleared from the Team page, and is counted as "waiting"

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.TeamPage.cs` lines 65-77 (`DescribeTeamPage`).

The page lists invitations whose state is sent OR expired, and sets `CanCancel` only when the state is sent
(line 72). The server itself allows cancelling an expired invitation: `CancelInvitation` checks the STORED state,
which is still `sent` for an expired one (`TeamRegistry.Invitations.cs` line 203). So the page hides an action the
server permits, and nothing else removes the row.

The harm, for an Owner or Manager: an invitation sent to a wrong or abandoned address stays on the Team page for
good once seven days pass, marked "Expired", with Resend as its only button. The only way to clear it is to press
Resend - which emails that address again - and then Cancel. Meanwhile the counting line counts it:
`Summary(paid, free, invitations.Count)` (line 77) says "1 invitation waiting" for an invitation that is no longer
waiting on anyone.

Neither branch is tested: the unit tests run on a fixed clock and never build an expired invitation, and the
component test has no expired row, so the "Expired" pill and the missing Cancel are unproven either way.

Suggested direction (the Developer decides): offer Cancel on an expired invitation as the server already allows,
count only live invitations in the summary, and add one test with the clock moved past the expiry.

Developer answer: ACCEPTED. An expired invitation now offers Cancel as well as Resend (the server already allowed it: its stored state is still sent), so it can be cleared without emailing the address again; and the counting line counts only live invitations, so a lapsed one is listed but not "waiting". New tests move the clock past the expiry: `DescribeTeamPage_AnExpiredInvitation_IsListedWithResendAndCancel_AndIsNotCountedAsWaiting` and `CancelInvitation_AnExpiredInvitation_ClearsItFromThePage_WithoutSendingAnything` (unit), and an expired row in `teamPage.test.tsx` (Expired pill, Resend, Cancel through its confirmation). Revert proof: putting back either the old `CanCancel` or the old count turns the first test red (`revert-proofs-gateway.txt`, F1.1 and F1.2).

### F2 - note - a Manager can remove a member who was promoted to Manager in the same instant

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.TeamPage.cs` lines 124-134 (`RemoveTeamMember`), with
`TeamRegistry.RemoveMember` (`TeamRegistry.cs` lines 321-332).

The target's role is read and the permission decided OUTSIDE the write lock; `RemoveMember` then re-reads the row
under the lock and re-checks only "is this the Owner". If the Owner promotes a Developer to Manager between those
two steps, a Manager's remove of that person goes through - a Manager removing a Manager, which the table refuses.
The same gap lets a Manager who was demoted in that instant complete one removal.

The window is a few milliseconds between two requests that must be made at the same moment, and the damage is
bounded (the Owner invites the person again), which is why this is a note and not a should-fix. It is recorded
because the rule "a Manager removes only Developers and Collaborators" is stated as enforced by the server, and here
it is enforced on a role that may be stale. Change role does not have the gap in any way that matters: only the
Owner may, and the Owner's role never changes.

Developer answer: ACCEPTED. `ChangeMemberRole` and `RemoveTeamMember` now read both roles, ask `TeamAccess.Decide` and write inside the registry's write lock, the lock every membership write takes (the Owner's role change, an accepted invitation, add, remove), so the roles the decision reads cannot move before the write lands. The lock is a re-entrant monitor, so the inner `ChangeRole` / `RemoveMember` take it again without deadlock; every existing change and remove test passes through that path. Not added: a test that reproduces the interleaving. Without a test-only hook inside the registry the window cannot be forced, and a timing loop would pass almost always with or without the fix - a test that cannot fail, the defect F3 names. Stated here rather than claimed.

### F3 - note - one of the new tests cannot fail

Location: `src/CcDirector.Gateway.Tests/Teams/HostedTeamPageEndpointsTests.cs` lines 178-191
(`Transcript_TheTeamPage`).

It sends nine requests and writes the answers to the test output, with no assertion. It passes whatever the routes
answer, short of a crash, and it is counted in the "executed=41, all passed" line the proof cites. The rules it
walks are asserted by the three tests above it, so nothing is unguarded; the harm is only that the count overstates
by one. The Developer's brief asked that every test be able to fail.

Developer answer: ACCEPTED. `Transcript_TheTeamPage` now asserts each step's status (200, 403, 403, 200, 403, 409, 403, 200, 404) and, at the end, that the removed member is gone on the server, so it fails when a route answers wrongly.

### F4 - note - the seam document still says every membership change calls sync-seats

Location: `docs/missions/teams-v1-2026-10-03/seam-team-billing.md` in the mission record - the "Seat sync" paragraph
under "Track B - the invitations table" ("After every accept (and every add, role change and remove) ... calls ...
once") and section 4 ("After ANY membership change the Gateway commits ... it calls the website").

After this change the Gateway calls only when the paid-seat count moved. The seam is the contract the billing side
(devthrottle_internal#2315, parked) will be finished against; someone reading it there will expect a call on an
accepted Collaborator invitation and on a paid-to-paid role change, and neither comes. Nothing breaks from it today,
because the website counts for itself - but the record now disagrees with the code. This is for the Tech Lead as
much as the Developer, since the file lives in the record and not in the pull request. A smaller instance of the same
thing is in the code: the `seatSync` parameter comment in `TeamRegistry.cs` line 62 still says "every change says so
in the log", which is now true only of a change that moved the count.

Developer answer: ACCEPTED for the code, LEFT TO THE TECH LEAD for the record. The `seatSync` parameter comment in `TeamRegistry.cs` now says only a change that moved the paid-seat count asks the website, and points to `CommitMembershipChange`, which states the rule. The seam document is in the mission record, outside this pull request and my worktree, so I did not edit it. Suggested wording for both places: "After a membership change that moves the team's paid-seat count (Owner, Manager and Developer members, counted before and after the change), the Gateway calls sync-seats once, naming the team. A change that leaves the count unchanged - a role change between paid roles, or a Collaborator joining or leaving - makes no call; the website counts for itself and the convergence pass repairs a missed call."
