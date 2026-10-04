# Gateway contract - a Director set up for a team (devthrottle_internal#2311, Gateway part 1)

As built on branch `teams/2311-gateway-team-key`. This is what the Director-side work builds against. Every route
here is on the **hosted** Gateway only, and the team parts exist only where Teams is released
(`CC_GATEWAY_TEAMS=1`, the same switch as #2300 and #2302). With the switch off nothing below exists except the
unchanged personal enrollment, and a `teamId` is refused.

The three enrollment routes (sections 1 to 3) take the person's **account token** (the Supabase access token) as the bearer, exactly as
`POST /devices/enroll-hosted` always has: a Director being set up has no device key yet, and a Director changing team
proves its person afresh. All three are public paths in the auth middleware and validate the token themselves
(signature, expiry, audience, issuer). Errors are JSON `{ "error": "<sentence>" }` unless stated otherwise.

The sentences are constants on `HostedEnrollmentEndpoint`, so a client can match on them; the Gateway owns their wording.

---

## 0. The one "Teams released" signal - `GET /healthz`

Read this FIRST. `/healthz` is anonymous and the Director already reads it. Its answer now carries:

```json
{ "status": "ok", "version": "...", "teams": true, "serverTime": "..." }
```

| `teams` | Meaning | What the Director does |
|---|---|---|
| `true` | hosted Gateway, `CC_GATEWAY_TEAMS=1`: the routes below are mapped | ask for teams; from then on any non-200 is an error shown as it is |
| `false` | hosted Gateway, Teams not released | exactly today's path: no teams call, no question, no chip |
| absent | a Gateway from before Teams, or a self-host Gateway | the same as `false` |

`teams` is the same answer during start-up, when `/healthz` is 503 `starting`. It is a process fact, the same for every
account, so it is safe on this public endpoint. It is NOT one of the `subsystems` (the deploy asserts every subsystem
is "available").

Why not "404 means no teams": `AuthMiddleware` used to let only the exact path `/devices/enroll-hosted` through without
a device key, so a Gateway without the Teams half answers **401** to `/devices/enroll-hosted/teams`, which cannot be
told apart from a refused token. Both team routes are now public paths (a test proves it), but a Gateway from before
this change still answers 401 - hence the explicit signal.

---

## 1. `GET /devices/enroll-hosted/teams` - the teams a Director may be set up for

**Request.** `Authorization: Bearer <account token>`. No body.

**200**

```json
{ "teams": [ { "teamId": "6f0c...", "name": "Acme", "role": "developer", "memberCount": 4 } ] }
```

- Every team where the role table lets this person **run sessions on their own computers** - asked through
  `TeamAccess.Decide(team, person, RunSessionsOnOwnComputers)`, so roles `owner`, `manager`, `developer`.
- **Never** a team where the person is a Collaborator.
- **Never** the person's own account. The Director always offers "personal" itself.
- A person in no such team gets `{ "teams": [] }` - an empty list, never an error.
- `role` is the stored word: `owner`, `manager` or `developer`. `memberCount` counts every member, the person included.
- Ordered by team name.

| Status | When | Body `error` |
|---|---|---|
| 401 | no bearer | `an account access token is required` |
| 401 | token not valid (signature, expiry, audience, issuer, or no subject) | `the account token is not valid` |
| 404 | Teams not released on this Gateway (route not mapped) | the Gateway's ordinary not-found answer |

Skipping the question (issue: "a person with one team is never asked to choose") is the Director's decision from this
list: one entry plus personal, or none.

---

## 2. `POST /devices/enroll-hosted` - set a Director up, optionally for a team

**Request.** `Authorization: Bearer <account token>`.

```json
{ "deviceId": "<the Director's own id>", "machineName": "LAPTOP-1", "platform": "windows",
  "deviceType": "workstation", "teamId": "6f0c..." }
```

`teamId` is new and optional. **Null, absent, empty or blank = the person's own account, exactly as before.** Send the
Director's OWN id as `deviceId` (each Director instance has its own key - that is what lets two Directors on one
computer sit on two teams).

**200** - unchanged shape (`DeviceRegistrationResponse`):

```json
{ "deviceKey": "...", "deviceId": "<namespaced registry id>", "machineName": "LAPTOP-1", "status": "active", "deviceCount": 1 }
```

With a `teamId` the key is bound to **the team's tenant**, for **this person** (`AccountSubject` = the person). Every
session that Director registers belongs to that team. The registry id is namespaced by the team AND the person, so
two members presenting the same `deviceId` never share a row, and one person's Directors on two teams are two rows.
`deviceCount` counts the keys in the team's tenant.

**One Director, one key, one place - and setting it up somewhere else IS a move (review F1).** Where Teams is released,
setting a Director up in a DIFFERENT tenant from one where that person already holds a working key for the same
`deviceId` (another team, or the person's own account) gets exactly the move's rules, from the one piece of code both
use (`LeaveOtherPlaces`):

- while that Director has **any session registered** in the place it would leave, it is refused with the move's **409**
  `This Director still has sessions open. Close every session on it, then change its team. Nothing was changed.` -
  no key is minted and the old key and tunnel stay;
- otherwise that person's OTHER keys for the same `deviceId` are revoked (reason `director_set_up_again`) and the
  Director's **open tunnel in the place it left is cut**, before the new key is issued.

Setting a Director up again in the **same** tenant is today's behaviour: no session check, no tunnel cut, a fresh key.
Another Director of the same person, and the same `deviceId` set up by someone else, are untouched. Where Teams is dark
nothing is revoked - no other key of that Director can be live there.

A team enrollment **never reaches the personal trial or paid gate**: a team has no trial, and a member is never
refused for the bill here. It also mints no personal tenant.

Refusals added by teams (the personal ones - 400 `deviceId is required`, 401, 402 with `message`/`subscribeUrl`, 503 -
are unchanged and apply only to a personal enrollment):

| Status | When | Body `error` |
|---|---|---|
| 400 | `teamId` given, Teams not released | `This DevThrottle service does not offer teams yet, so a Director cannot be set up for one. Leave the team out to set it up for your own account.` |
| 403 | not a member of that team, or no such team (one answer for both) | `You cannot run sessions in that team, so a Director cannot be set up for it. You are not a member of this team, so you cannot do anything in it. Ask the team's Owner or a Manager to invite you.` |
| 403 | a Collaborator in that team | `You cannot run sessions in that team, so a Director cannot be set up for it. In this team you are a Collaborator, and a Collaborator may not run sessions on their own computers.` |
| 409 | Teams released, the person's working key for this `deviceId` is in another tenant, and the Director has a session registered there | `This Director still has sessions open. Close every session on it, then change its team. Nothing was changed.` |

Every 403 starts with `CannotRunSessionsInTeamLead` ("You cannot run sessions in that team, so a Director cannot be
set up for it."); the role table's own sentence follows. No key is minted on any refusal.

---

## 3. `POST /devices/enroll-hosted/move` - move one Director to another team, or back home

**Route shape (fixed with the Tech Lead).** It sits beside enrollment because it IS a re-enrollment: same bearer (the
account token - a team key could not be used, the access lease answers 402 for it until the next step), the same
permission check, and the same answer shape. The Director is named by its own id.

**Request.** `Authorization: Bearer <account token>`.

```json
{ "deviceId": "<the Director's own id - the deviceId it was set up with>", "teamId": "9a1d..." }
```

`teamId` null, absent or blank = move it to the person's own account. The Director's working key must have been issued
to the account the token names.

**200** - a `DeviceRegistrationResponse` exactly as enrollment returns, carrying the **new** `deviceKey`. Store it and
reconnect with it. On success, in this order:

1. the old key is **revoked** (reason `director_moved_to_another_team`) - it never works again;
2. the Director's open tunnel on the old team, on this Gateway, is cut (steps 1 and 2, and the 409 for sessions, are
   the same code setting a Director up somewhere else runs - section 2);
3. a new key bound to the new team (or the person's own account) is issued, keeping the machine name, platform and
   device type.

If step 3 fails the Director is left with no working key (it is set up again) - never with two.

The permission check is enrollment's: to a team, `TeamAccess.Decide(..., RunSessionsOnOwnComputers)`; to the person's
own account, the personal paid gate (trial on a first arrival, 402 / 503 as enrollment).

| Status | When | Body `error` |
|---|---|---|
| 400 | no `deviceId` | `deviceId is required` |
| 401 | no bearer / token not valid | as enrollment |
| 402 / 503 | moving to the person's own account and the personal gate refuses | as enrollment (402 carries `message` and `subscribeUrl`) |
| 403 | that Director's working key was issued to a different account | `That Director was set up by a different account, so it cannot be moved from yours. Sign in with the account that set it up.` |
| 403 | not a member of the target team / a Collaborator there | as enrollment |
| 404 | this account has no working key for that Director id (never set up, revoked, its person removed from the team) | `This account has no Director set up with that id, or its key is no longer active, so it cannot be moved. Set the Director up again.` |
| 409 | the Director is already in that team (or already personal) | `This Director is already set up for that team. Nothing was changed.` |
| 409 | the Director has **any session registered on the Gateway** (its last known roster there, read by the id it said Hello with - which for a team key is the id it was set up with, section 4) | `This Director still has sessions open. Close every session on it, then change its team. Nothing was changed.` |
| 409 | this account holds more than one working key for that id (enrollment no longer leaves this behind) | `This Director is set up in more than one place, so DevThrottle cannot tell which one to move. Set the Director up again.` |
| 404 | Teams not released (route not mapped) | the ordinary not-found answer |

A 401 from the move means the token: "sign in again". The server enforces the "no sessions" rule; the Director checks
too, before it asks (D3), and holds session creation while the move runs.

---

## 4. What a team key does after enrollment

- **Valid while the person may run sessions there.** On every request and every Hello, a key bound to a team's tenant
  is active only while a `gateway.team_members` row has that team, that person, and a role the role table lets run
  sessions. The role list is read from `TeamPermissions`, not written down again.
- **Removing the person, or making them a Collaborator**, at the one membership-change place
  (`TeamRegistry.CommitMembershipChange`): every key that person holds **in that team** is revoked durably (reason
  `team_member_removed` or `team_role_cannot_run_sessions`) and their **open tunnels on that team** are cut. Never the
  team's other members', never the person's other teams', never their personal tenant's. Invited back, the Director
  is set up again - the same as after a restart, whose start-up check quarantines such a key.
- **A team key says Hello for its own Director only (review F2).** A team key's Hello is accepted only under the
  Director id its own device row was enrolled with (the row id ends in `|<deviceId>`; compared ignoring letter case).
  Any other id is refused at Hello and the tunnel is closed before anything is registered - so a member can never take
  a colleague's Director id. A personal key is unchanged.
- **Start-up quarantine, Teams released**, keeps a team key whose person may still run sessions there and quarantines
  any other (reason `invalid_tenant_binding`), as before.
- **Start-up with Teams NOT released leaves every team key UNTOUCHED (review F4)** - no tombstone, no reason written.
  While dark such a key already resolves revoked on every request and every Hello, so nothing gets in; switching Teams
  back on restores it, with nothing set up again by hand. **For whoever flips `CC_GATEWAY_TEAMS`:** turning it off
  for a start (an incident, a missing variable on a deploy) cuts every team's Directors off until it is on again, but
  destroys nothing. A key bound to a tenant that is no team's and not its person's own is quarantined as before.
- **The team gate knows who is calling.** Inside a team's tenant the caller is the key's person. For a person's
  private things, the gate asks whose they are: the tunnel is the key's own; a Director is the person its Hello key
  was issued to; a session (any route naming `{sid}`) is the caller's own only when **exactly one** Director in the
  team holds that session id in its roster and that Director is the caller's. Two Directors holding one id is nobody's
  own and is refused (review F2); the roster itself does not refuse the duplicate, because a roster that kept the first
  writer would let a Director hide a colleague's session from them. Touching another member's session is joining or
  watching it, which no role may (403). A list across the team, an unknown session or Director, or a request not made
  with a device key is refused, never guessed.

## 5. What is NOT entitled for a team tenant yet (the next step)

The request-path **access lease** (`HostedAccessLeaseService`) reads a PERSONAL account's bill
(`SubjectForTenant(tenant)` then `EntitlementRegistry.Evaluate`). A team's tenant is no one person's, so today **every
authenticated request and every Hello made with a team key is answered 402** `hosted_subscription_required` by the
auth middleware - the key authenticates, and the bill check refuses it. Nothing is revoked by this (the lease denies
without a tombstone when the tenant names no subject). Wiring the lease, the narration plan and
`PreFreeTierKeyReinstatement` to the team's bill through `EvaluateTeamTenant` (merged in #3521) is the next step. Until
then a Director set up for a team can enroll, list its teams and move, but cannot connect its tunnel.
