# Review: #2312 pull request 2 - the Fleet Map by role (thefrederiksen/devthrottle#3533)

Written by a separate review session, 4 October 2026. Head reviewed: 2f1a0896d, against origin/main at its merge
base 24cf04bfa (`git diff origin/main...HEAD`, after `git fetch origin`).

## Scope

**Read in full:** the whole production diff - `Teams/TeamFleetMap.cs`, `Teams/TeamEndpointRules.cs`,
`Api/TeamEndpoints.cs`, `Discovery/DirectorRegistry.cs` (the new method and the registry's bind and removal paths),
the `GatewayHost.cs` mapping, `teamFleetMapClient.ts`, `TeamFleetMapView.tsx`, the `FleetMapView.tsx` change - and
every test file in the diff. Also, unchanged but load-bearing: `TeamEndpointGate.cs` (whole file), `TeamAccess.cs`,
`TeamRegistry.ListMembers` / `ChangeRole` / `RemoveMember` / `CommitMembershipChange`, `PushedSessionStore.GetLastKnown`,
the `DeviceCredentialEntity` key, `CurrentTeam.tsx`, the proof README and `pr2-test-runs.txt`. Issue #2312, the role
table in #2098, the Developer's brief and `seam-director-key.md`.

**Ran, in the review worktree, nothing edited (`git status` clean afterwards):**
- `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"`: 456 passed, 0 failed, 0 skipped.
  The Teams area only, not the whole unit suite.
- Cockpit `vitest run` on `TeamFleetMapView.test.tsx` and `FleetMapView.test.tsx`: 2 files passed (13 and 19 tests).
- client-core `vitest run src/teams`: 3 files, 39 tests passed.

**Did not run / could not reach:**
- `CcDirector.Gateway.Tests` was NOT run, as instructed (the machine is short of memory). So the over-the-wire tests
  this pull request adds (`OverTheWire_TheFleetMap_*` in `TeamEndpointWalkTests`, and the dark-switch test in
  `HostedTeamsDarkTests`) have no local verdict from me, and the Developer's own proof file says the same for this head.
  The continuous integration run 37224144008 is on this head; when I looked, its ".NET build and test" job was still
  in progress (the web and Python jobs were green). Somebody must read that job's result before merge.
- I did not re-run the Developer's red proofs. I judged "can it fail" by reading each test against the code.
- I did not render the Cockpit; the mockups were compared by reading the component, not by eye.
- F1 below is from reading the code path, not from a run: I may not add a test file to the worktree.

**What I checked and found sound (no finding):**
- "Own" is decided on the server only: the caller is the account subject behind the request key's personal tenant
  (`TeamEndpoints.ResolveCaller`), compared against the subject on the Director's device credential. Nothing the
  client sends names the caller or marks an entry as own.
- A Manager cannot get another person's repository or mission from this route: `SessionEntry` builds the two-field
  record for every entry that is not the caller's, and the fields are absent from the wire (pinned three ways).
- The answer carries no session id and no Director id, so it gives no handle for any follow-up route. A key bound
  to the team's tenant is refused on this route twice (the gate: caller unknown; the endpoint: not a personal account).
- A Director on another team, with no device key, on a revoked credential, on a credential bound elsewhere, with no
  person, or whose person has left the team: not on the map. Each has a test that fails if its condition is dropped.
- `TeamNarrowedToCaller` needs no gate change: the gate's only "own-only" refusal is keyed to `TeamTarget.Team`, so
  the new target lets an Own cell through and nothing else changes. Default deny holds (writes to the route and any
  path under it find no rule and are refused), and exactly one rule uses the new target.
- The person label is the email or a fixed sentence, never the subject. No subject or email is logged; the team id
  is logged hashed.
- Rule 7: the page lays out what arrives - layouts, sentences, status words and the cut are all the Gateway's. A
  person with no team (or a Gateway with Teams dark) reaches the unchanged own map.
- The issue's five tests are present (Test 1, 2, 4, 5 in `TeamFleetMapTests`; Test 3 in `TeamEndpointGateTests`).

## Verdict

One should-fix (F1), two notes. No blocker. The privacy cut is on the server and I found no path by which a Manager
or a Developer reads more than the rulings allow.

## Findings

### F1 - should-fix - a person changed to Collaborator keeps their Directors on the map

**Location:** `src/CcDirector.Gateway/Teams/TeamFleetMap.cs` lines 93-96 (the `labels` dictionary) and line 106
(`!labels.ContainsKey(subject)`); the credential filter at line 170.

**The harm:** the mandate requires that a Director "whose person has left the team or become a Collaborator does not
appear", and the role table gives a Collaborator no sessions and no Fleet Map. The code only checks that the
Director's person is still a MEMBER: `labels` is built from every row `ListMembers` returns, Collaborators included,
with no look at the role. And at this head nothing revokes a device credential when a role changes -
`TeamRegistry.ChangeRole` ends in `CommitMembershipChange`, which saves and logs and does nothing else - so the row
still has `RevokedAtUtc == null` and passes line 170. Result: change a Developer who has a Director on the team to
Collaborator, and the Owner and every Manager go on seeing that person's Director and their sessions' names and
status for as long as the Director stays connected. The seam document says the key "resolves as revoked on the next
request" once #2311 lands, but that is a live answer about the key, not necessarily a write to `RevokedAtUtc`, and
this map reads the column directly - so it cannot be assumed that #2311 closes this by itself.

`Read_APersonWhoLeftTheTeam_IsNoLongerOnTheMap` covers removal only. There is no test for the Collaborator case, so
it would stay green either way.

**What would settle it:** attribute a Director only to a member whose role the table lets run sessions (ask the role
table, not a hard-coded list), plus a test: seed a Developer's Director, `ChangeRole(..., Collaborator)`, and assert
it is off the Owner's map.

Developer answer: ACCEPTED, fixed in d532a582f. A Director is attributed only to a member whose role the role table lets run sessions: `TeamPermissions.Grant(member.Role, TeamAction.RunSessionsOnOwnComputers) != TeamGrant.No`, asked of the table, not a hard-coded list (`TeamFleetMap.Read`, the `labels` built from `ListMembers`). So a person changed to Collaborator is off the map at once, whatever their device credential says. Tests: `Read_APersonChangedToCollaborator_IsNoLongerOnTheMap` (Owner and Manager viewing: the second Developer's Director is on the map, `ChangeRole(..., Collaborator)`, then it, its session and the person are gone) and `Read_ACollaboratorWithADirectorOnTheTeam_IsOnNobodysMap` (a Collaborator seeded with an active credential bound to the team). Red proof: dropping the role check fails all three (3 failed | 69 passed in the TeamFleetMap filter); restored, tree clean.

### F2 - note - the issue's Test 3 proves the decision function with inputs the running Gateway cannot yet produce

**Location:** `src/CcDirector.Gateway.UnitTests/Teams/TeamEndpointGateTests.cs` line 343
(`Issue2312Test3_Check_AManager_OpeningReadingOrTypingIntoAnotherPersonsSession_IsRefused`), with its helper `InTeam`
at line 75; against `src/CcDirector.Gateway/Teams/TeamEndpointGate.cs` lines 188 and 212.

**The harm:** the test hands `Check` a known caller (the Manager) and `TeamOwnership.SomeoneElses`. In the running
Gateway, `RunAsync` passes `_ => TeamOwnership.Unknown` always, and `CallerSubject` answers null for any key bound to
a team's tenant. So today a Manager's attempt is refused over the wire by a different branch (caller unknown) than
the one the test exercises. The refusal is real either way and the test can fail (change a rule's `OthersAction` and
it goes red), so this is not a defect in this pull request. But the sentence "refused by the server" in the issue
will, once #2311 supplies the caller and the ownership answer, rest entirely on that resolver saying "someone
else's" - and this test will stay green whatever the resolver says. The test that closes Test 3 over the wire
belongs with #2311's resolver; it should be named there so it is not assumed done here.

Developer answer: ACCEPTED as a note; no change in this pull request. Agreed on the reading: today the running Gateway refuses a Manager's attempt through "caller unknown", and Test 3 proves the decision function given a known caller and "someone else's". The over-the-wire closure of Test 3 belongs with #2311's resolver, which is what will supply the caller and the ownership answer - the test that a Manager's request on another person's session is refused through the resolver's "someone else's" answer should be named in #2311 (the Tech Lead has it noted there). Nothing in #3533 claims the over-the-wire half.

### F3 - note - one session reporting a state the fold does not know fails the whole team's map

**Location:** `src/CcDirector.Gateway/Teams/TeamFleetMap.cs` lines 244-252 (`TeamFleetMapStatus.Fold`), reached from
`Read` for every session of every Director; caught only by `TeamEndpoints.Guarded`.

**The harm:** the state is a string the Director sends. If any one session on any one teammate's Director reports a
state outside the six known words (a Director on a later build with a new state, or a blank), `Fold` throws, and the
Owner, every Manager and that Developer get a 500 on every poll until that session ends - one row takes the map away
from the whole team, and the page can only say "could not read your teams". I could NOT show a current producer that
emits such a state: the enum has exactly the six values the fold handles, and nothing on this head sets the assessed
state. So this is a blast-radius observation, not a proven failure. Throwing rather than inventing a word is right
by rule 3; the question for the Developer is only whether the failure should be the one session's (left off, and
logged loudly) or the whole team's.

Developer answer: ACCEPTED, fixed in d532a582f, the one session's failure rather than the team's. Before folding, `Read` asks `TeamFleetMapStatus.Knows(session)`; a session in a state the fold does not know is LEFT OFF the map and logged loudly ("UNKNOWN SESSION STATE '<state>' on team <hashed id> - that session is LEFT OFF the team Fleet Map. Teach TeamFleetMapStatus.Fold the state."; the state is cut to 40 characters, no subject, email or session id). The rest of the map stands. No word is invented: `Fold` itself still throws on an unknown state, and `Knows` lists exactly the six states `Fold` handles. Tests: `Read_ASessionInAStateTheFoldDoesNotKnow_IsLeftOff_AndTheRestOfTheMapStands` (a new state and a blank state on one Director: both left off, its known session and every other person's still shown), `Knows_ExactlyTheStatesTheFoldHandles` (and that each known state folds without throwing), `Knows_TheGatewaysAssessedState_WinsOverTheDirectorsOwn`. Red proof: removing the filter makes the read throw and fails the leave-it-off test (1 failed | 71 passed); restored, tree clean.
