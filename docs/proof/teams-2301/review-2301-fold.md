# Review: #2301 invitations, folded onto #2302's single permission check (delta only)

Pull request thefrederiksen/devthrottle#3526, only `git diff 2b4067b04 dc2101f1f` (the two commits after the
merge of main), read in the worktree `devthrottle-teams-2301-review`, detached at dc2101f1f. Written by a
separate review session, 3 Oct 2026. The earlier review of this pull request is `review-2301.md`; nothing in
it is re-judged here.

## Scope

**Read in full:** the delta (seven files); `Teams/TeamPermissions.cs`, `Teams/TeamAccess.cs`,
`Teams/TeamEndpointGate.cs`, `Teams/TeamEndpointRules.cs`, `Teams/TeamInvitationRules.cs`,
`Teams/TeamRegistry.Invitations.cs`, `Api/TeamInvitationEndpoints.cs`; the new
`TeamInvitationGateTests.cs`. **Read in part:** `TeamRegistry.cs` (`RoleOf`, `IsTeam`, the constructor),
`TeamEndpoints.ResolveCaller`, where `GatewayHost.cs` builds and installs the gate (1750, 3958-3960),
`TeamEndpointGateTests.cs` around the changed test, `TeamEndpointWalkTests.cs` (the walk, not the
over-the-wire part), and the role tests in `TeamInvitationTests.cs` and `TeamInvitationRulesTests.cs`.
The role table and "who invites" were read from issue devthrottle_internal#2098.

**Ran:** `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~TeamInvitation|FullyQualifiedName~TeamEndpointGate|FullyQualifiedName~TeamPermissions|FullyQualifiedName~TeamAccess"`
on dc2101f1f: 382 passed, 0 failed, 1 skipped.

**Did not run / not covered:** `Gateway.Tests` (the Tech Lead runs it) - so the changed assertion in
`HostedTeamInvitationEndpointsTests.cs` and the route walk are read, not run by me. No revert proof was made
(I may not edit files): "able to fail" below is from reading which assertion each mutation would break.
The Cockpit's invite form was not opened; whether it shows the gate's new sentence to a Developer well is
not judged.

## What was asked, and what I found

**One place for who may invite whom - yes.** `TeamInvitationRules.MayInvite` (TeamInvitationRules.cs:26-33)
no longer holds its own switch; it reads `TeamPermissions.Grant(inviter, ActionToAddOrRemove(invited))`.
Every write goes through `TeamRegistry.InviteDenial` (TeamRegistry.Invitations.cs:437-455), which asks
`TeamAccess.Decide` for the same cell: create at line 79, resend and cancel through `InvitationForManager`
at line 425 with the STORED invitation's role. The last rank shortcut in the invitation code
(`TeamRoles.IsAtLeast` in `ListInvitations`) is replaced by a `TeamAccess.Decide` call (line 133); a search
of `src/CcDirector.Gateway` finds no remaining caller of `IsAtLeast`. The answers for every pair are
unchanged: Owner -> Manager, Developer, Collaborator; Manager -> Developer, Collaborator; Developer and
Collaborator nobody; nobody as Owner. `TeamInvitationRulesTests` still pins all sixteen pairs as literals,
and it passed.

**An Owner-only Manager invitation - enforced, in the registry.** The gate lets anyone with "invite or
remove Developers and Collaborators" through `POST /teams/{teamId}/invitations`; the role is in the body, so
the gate cannot see it. `InviteDenial` then asks `ActionToAddOrRemove(Manager)` = "make someone a Manager",
which only the Owner's cell grants (TeamPermissions.cs:121, 150). Resend and cancel ask it of the stored
role, so a Manager cannot touch a Manager invitation. Pinned by `Issue2301Test1...` and
`ResendAndCancel_AManagerCannotTouchAManagerInvitation_ADeveloperNothing`, both through the real registry;
pointing `InviteDenial` at the wrong row would turn both red.

**The three `/team-invitations` calls - cannot be used to act in a team you are not in, or for someone
else.** What stops it is not the list (see F1) but three things: the caller is the account behind the
request's own device key (`TeamEndpoints.ResolveCaller`, never a body field); the team is found only from the
hash of the link's secret (`FindByToken`), so a caller names no team and no invitation id; and accept writes
the member row for that caller alone, under the write lock, after the state, already-a-member and bill
checks. From a key bound to a team's tenant the gate refuses all three as undeclared
(TeamEndpointGate.cs:137-138), and the new test asserts that.

**The changed #2302 test - still proves what it proved.** The case is "a route that names a team and has no
rule is refused by the gate itself, for the Owner and a stranger". `Check` takes the pattern as a string, so
`/teams/{teamId}/rename` needs no real route; no rule covers it; `NamesATeam` is true. The two rows above it
still cover "a method the existing rule does not cover".

**Tests for the fold - present and able to fail.** `TeamInvitationGateTests` asserts outcome AND stated
action per role for read and write, not-a-member for all five routes, and the three accept calls from a
personal account and from inside a team. One caution on a name: `WhoMayInviteWhom_..._AndNoSecondCopy`
compares `MayInvite` with the same expression `MayInvite` is written as, so it proves agreement with the
table, not the absence of a second copy; the literal table in `TeamInvitationRulesTests` is what would catch
a wrong answer. Not a finding - nothing is unguarded.

**One thing not reachable today, said so it is not a surprise later:** `TeamAccess.Decide` counts a cell of
`Own` as allowed, while `MayInvite` requires `Yes`. Both invite rows hold only `Yes` and `No`, so the form
and the registry agree on every pair today.

## Verdict

No blocker and no should-fix. The fold is sound: one table, asked through one place, with the same answers
as before on create, resend and cancel. One note.

## Findings

### F1 - note - `TeamEndpointRules.OwnAccountOnly` is read by nothing, so it is a comment, not a category

Location: `src/CcDirector.Gateway/Teams/TeamEndpointRules.cs:147-162` (the list and its claim "written down
so that their absence from All is a decision, not an omission"); the only reader is
`src/CcDirector.Gateway.UnitTests/Teams/TeamInvitationGateTests.cs:104-119`. `TeamEndpointGate.Check`
(TeamEndpointGate.cs:131-135) never looks at it.

The harm: the list neither opens nor closes anything. The three accept calls pass the gate from a personal
account for the same reason every ordinary route does - no rule, and `NamesATeam` is false because "team" is
in a literal path segment, not under `/teams/` and not in a parameter name. So the answer to "could a later
route be put in this category to skip the gate" is that it does not need to be: a later
`POST /team-invitations/<anything>` - for example one that takes an invitation id and so acts on a team's row -
is "not a team request" from a personal account without being written in this list or in `All`, and no test
goes red. The test cannot see it either: it loops over the list's own three strings, never over the mapped
routes, and its two per-entry checks (`Find` is null, `NamesATeam` is false) pass for any string at all. The
walk in `TeamEndpointWalkTests` asks every route from INSIDE a team's tenant only. The default deny that
#2302 built for "a route that acts in a team from a person's own account" therefore does not reach the
`/team-invitations` family, and the comment reads as if this list supplied it.

There is no present hole: the three routes that exist are held by the link's secret and the caller's own
key, as set out above. This is a note because the protection for the NEXT route in this family is a
developer remembering, which is what the gate was built to replace.

Why it should change, one of two ways: make it real - a test over the host's mapped route table asserting
that every route under `/team-invitations` is in `OwnAccountOnly` or has a rule - or reword the comment to
say plainly that the list is a record and nothing enforces it.

Developer answer: accepted, made real (commit 4cf0c99b9). A new test, `HostedTeamInvitationEndpointsTests.EveryRouteUnderTeamInvitations_IsDeclaredOwnAccountOrHasARule`, reads the HOST's mapped route table the way `TeamEndpointWalkTests` does (every RouteEndpoint, every method, normalised; it asserts it read more than 300 routes so an empty table cannot pass). Every route under the families in the new `TeamEndpointRules.OwnAccountFamilies` (today `/team-invitations`) must be in `OwnAccountOnly` or have a rule in `All`, and every `OwnAccountOnly` entry must be a mapped route in such a family, so a stale entry is red too. The comment on `OwnAccountOnly` now says plainly that the gate does not read the list and names this test as what enforces it. Revert proof: with `/team-invitations/decline` taken off the list the test failed (executed=1, failed 1) with "POST /team-invitations/decline is under an own-account family but is neither declared own-account ... nor covered by a rule ... Decide which, and write it down."; restored, diff 0. Runs on 4cf0c99b9: Gateway.UnitTests 8,532 passed, 0 failed, 9 skipped; `-Gateway -Filter Teams|MobileRedirect|AddTeam` executed=70, 70 passed, 0 failed.
