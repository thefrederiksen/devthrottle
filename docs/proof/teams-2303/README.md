# Proof - the Team page (devthrottle_internal#2303)

Stacked on pull request #3526 (devthrottle_internal#2301, invitations), which is not yet merged.

## What was built

- **Gateway.** `GET /teams/{teamId}/page` carries every verdict the page renders: per member whether the role is a
  dropdown and which roles it offers, whether Remove shows and what its confirmation says, the seat (Paid or Free);
  for the page, the counting line and whether "Invite someone" shows; the waiting invitations with seat, expiry and
  whether Resend and Cancel show. `PUT /teams/{teamId}/members/{memberId}/role` and
  `DELETE /teams/{teamId}/members/{memberId}` make the two changes. All three are declared in `TeamEndpointRules`,
  decided by `TeamAccess.Decide`, committed through `TeamRegistry.CommitMembershipChange`, and mapped only when
  `CC_GATEWAY_TEAMS=1`.
  - Change role is the row "make someone a Manager, change roles" (Owner only).
  - Remove is `TeamPermissions.ActionToAddOrRemove` of the member's role: the Owner removes anyone but themselves; a
    Manager only Developers and Collaborators. Nobody removes the Owner.
  - A new role table row, `SeeTeamPage` (Yes, Yes, Yes, No): a Collaborator has no Team page. They can still read the
    member list (`GET /teams/{teamId}/members`, unchanged).
  - A member is named by an opaque hash of team and account (`TeamMemberIds`), never the account subject, because the
    id travels in an address the access log records.
- **The bill.** `CommitMembershipChange` now counts the paid members before and after the save and asks the website
  to recount only when the number moved. So a Developer made a Manager, a Collaborator added and a Collaborator removed
  leave the bill alone; a paid member added or removed, or a move between a paid role and Collaborator, reach it. This
  also changes #3526's behaviour for accepting a Collaborator invitation (no longer a sync call) - its test is updated
  to say so. **The bill side itself waits on devthrottle_internal#2315** (the parked billing pull request holding the
  website's sync-seats route): this proves only that the Gateway calls it, with the right team, exactly when it should.
- **Cockpit.** `/team/{teamId}/members` renders the page from the Gateway's answer. The team comes from the address
  until the Cockpit team switcher (#2312) is on main.
- **No schema change.** No migration.

## Screenshots - real renders

Taken by `take-screenshots.py`: the built Cockpit served by a real hosted Gateway (`TeamPageProofRig`) with a seeded,
billed team - Owner qa@mindzie.com, Manager tech@mindzie.com, Developer dev@mindzie.com, Collaborators
docs@mindzie.com and james@client.example, and one waiting invitation. No email is sent and the website is never
called. After the run the driver reads the members back from the server and checks the role change landed.

| File | What it shows |
|---|---|
| `s1-owner.png` | The Owner: every role but their own a dropdown, Remove on everyone but themselves, Resend and Cancel |
| `s1-owner-after-role-change.png` | The Owner made dev@mindzie.com a Collaborator: the seat and the count follow from the server |
| `s1-owner-remove-confirmation.png` | Remove asks first, in the Gateway's sentence |
| `s1-manager.png` | A Manager: roles as plain text, Remove only on Developers and Collaborators, can invite |
| `s1-developer.png` | A Developer: the list, nothing to click, no invitations |
| `s1-collaborator-no-team-page.png` | A Collaborator: no Team page, in the role table's words |

## Test output

| File | What |
|---|---|
| `test-local-default.txt` | `.\scripts\test-local.ps1` - ten suites, every one `outcome=Completed`, all executed |
| `gateway-tests-teams.txt` | `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams"` - executed=41, all passed |
| `api-transcript.txt` | The page, a refused and an allowed role change, refused and allowed removals, over real HTTP |
| `revert-proofs-gateway.txt` | Four rules broken one at a time; each turns its tests red |
| `revert-proofs-cockpit.txt` | The page deciding from the role instead of the verdicts; the Manager and Developer views go red |

Also run, not saved as files: `dotnet test src\CcDirector.Gateway.UnitTests` - 8589 passed, 0 failed, 10 skipped;
the Cockpit web suite 601 passed; the client-core web suite 1642 passed; `npm run typecheck` clean.
