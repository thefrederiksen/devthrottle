# Pricing Change, B2 - how a Cockpit or phone request is told apart from a Director's

Settled 2026-10-13 by the Tech Lead for the Gateway track. The mission document (`devthrottle_internal`,
`docs/missions/pricing-change-2026-10-11/MISSION.md`, section 5, Track B, item B2) makes this the first
task of phase 3. Everything here was read from `origin/main` at 26afc4ed1.

## The question

`hosted_gateway` is being split. A Free account keeps the Director tunnel and usage recording, and loses
the Cockpit, the phone app and the relay behind them (the new `remote_access` scope). So on every hosted
request, not only at enrolment, the Gateway has to know whether the caller is a Director or a person's
browser or phone.

## What the Gateway already knows

Every hosted credential is a per-device key, and every key was minted by one function,
`HostedEnrollmentEndpoint.Enroll`, through one of two doors:

| Door | Who uses it | Device type recorded on the key |
|---|---|---|
| `POST /devices/enroll-hosted` | a Director | what the Director sent; blank becomes `workstation` |
| `POST /mobile/enroll` (and the old `/m/enroll`) | the phone app and the Cockpit | decided BY THE GATEWAY from the platform: `phone` or `browser` (`MobileDeviceEnrollmentService.DeviceTypeForPlatform`) |

On every later request `AuthMiddleware` verifies the key and puts the device record, with that type, on
the request (`AuthMiddleware.AuthenticatedDevice`). The Gateway already rules on it in a dozen places:
`SessionOriginSurfaces.FromDeviceType` answers `phone`, `cockpit` or `unknown`, and the "people only"
routes (the Mentor pages, team requests, the Fleet Manager's owner check) refuse anything that is
`unknown`.

## The rule

**A request is a Cockpit or phone request when the verified device key it carries was minted with the
device type `browser` or `phone`, which is exactly when `SessionOriginSurfaces.FromDeviceType` does not
answer `unknown`. Every other authenticated caller is not: a Director's key, a session key (an agent
inside a Director), a service token.**

No route list, no header, no client claim. The type on a `browser` or `phone` key was written by the
Gateway at the mint, so a client cannot change it afterwards.

## Where `remote_access` is asked

Two places, and one function answers both.

1. **At the mint.** `HostedEnrollmentEndpoint.Enroll` is told which door it was called through. Through
   the Cockpit and phone door, an account without `remote_access` is refused 402 before any tenant is
   minted or key issued. Through the Director door the same account enrols exactly as today. Every
   caller of `Enroll` and every other path that can hand a browser or a phone a key (check
   `AccountSignInCallbackEndpoint`, the team routes and the move route) is covered or shown not to mint
   such a key. The Director door must not be a way round: it refuses a request that asks for the device
   type `phone` or `browser`.
2. **On every request.** In `AuthMiddleware`, in the hosted block that already asks the access lease,
   after the lease has answered Allow: if the authenticated device is a Cockpit or phone device and the
   tenant (in a team: the person's seat) lacks `remote_access`, the request is refused 402. The relay
   needs nothing of its own: a relayed command and the terminal WebSocket are requests carrying a
   `browser` or `phone` key, and they pass through this same check.

**The answer rides the lease, so the hot path stays one in-memory read.** The lease already makes one
entitlement read per tenant per five minutes. That same read yields the tier, so the lease keeps, beside
its Entitled outcome, whether remote access is granted (the plan, or the pre-launch grace below). One
read answers both questions, so they can never disagree, and the Cockpit's polling costs no database
read. The cost is that a plan change is seen within the lease's lifetime (five minutes, or the 60 second
sweep), the same as cancellation is today.

## A refusal refuses the request and nothing else

It follows `DenyNotAMember`. A new decision (for example `DenyNoRemoteAccess`) is returned for the
request, the tenant's positive lease is left exactly as it is, and the revoker is never called: no device
key is revoked, nothing is tombstoned, the Director's tunnel is not cut and its lease still answers
Allow. The refused phone or browser key itself is left alone too, so a member who subscribes is let in
on the next lease read without signing in again.

A plan that could not be read is never a refusal: 503 with `Retry-After: 10` and `entitlement_unknown`,
as the lease already answers.

## The refusal, as the clients read it

HTTP 402, flat JSON, the same shape the two enrolment doors already answer plus a code:

    { "error": "...", "code": "remote_plan_required", "message": "<one finished sentence>", "subscribeUrl": "<the pricing page>" }

The sentence and the address are owned by the Gateway, in one constant beside
`EntitlementRegistry.SubscribeMessage`. The clients render them and decide nothing. The same body is
answered at the mint and on a request.

## Pre-launch grace (B4)

Two values, held in a committed file: the repository's `Dockerfile`, which builds the hosted Gateway
image. Each is an `ENV` line there, beside the image's own `CC_GATEWAY_HOSTED=1`, because the deploy
writes no application setting of its own and the image is the one place a hosted setting can be stated
and reviewed. The Gateway reads them once, when it starts, and only when it is hosted; a self-hosted
Gateway never reads either.

- `CC_GATEWAY_PRICING_LAUNCH_UTC` is the launch instant. It is committed as `2026-10-13T20:32:00Z`, the
  moment the new prices went live (Delivery Lead, 13 October 2026).
- `CC_GATEWAY_REMOTE_ACCESS_GRACE_CUTOFF_UTC` is the cut-off instant. It is not in the file, so the grace
  does not end. The owner sets it later with a one-line pull request.

Each is one instant in Coordinated Universal Time written as `yyyy-MM-ddTHH:mm:ssZ`. A value that is
present and cannot be read in that form stops the Gateway at startup with an error naming the setting;
it is never read as absent.

What each means in code, whatever the file holds:

- **the launch instant.** An account is "created before launch" when its tenant row
  (`TenantEntity.CreatedAtUtc`, written the first time the account reached the hosted Gateway) is older
  than this. **Absent means every account counts as created before launch**, so a deploy with nothing
  set takes nothing from anyone.
- **the cut-off instant.** Grace ends at this moment. **Absent means no cut-off: grace does not end.**
  No date is written in code. The owner sets the cut-off when he approves the notice email.

Grace grants `remote_access` and nothing else. It never grants an AI scope and it does not change the
tier the account is on. It is computed in the same place as the plan's answer, so the mint and the
request path cannot disagree about it.

An account that signed up on the website before launch but never reached the hosted Gateway has no
tenant row. With a launch instant set, its first arrival mints a row dated after launch and it is not in
grace. That is deliberate: it never had the Cockpit to keep.

## Teams

A person's browser or phone key is never bound to a team's tenant. `MobileEnrollmentEndpoint` mints it
for the person's own account, and the team pages are reached from that personal key through the
`/teams/{teamId}/` routes. So on a Cockpit or phone request the lease that is asked is the personal
tenant's, and the team's own lease read is never consulted. Left alone, the split would refuse every
team member whose own account is on Free, a paid seat included, although the mission gives the team
tier `remote_access`.

The rule, added to the one function that answers remote access (the personal read and the mint):

**A person has remote access when their own plan grants it, or the pre-launch grace applies, or they
are a member, in any role, of at least one team whose bill is running** (the same test
`EntitlementRegistry.EvaluateTeamTenant` uses for "the bill grants": active or past due).

- A Collaborator of a billed team is in: the team pays, and a Collaborator has no Director to fall back
  on.
- A team with no running bill gives its members nothing here. Otherwise creating an unbilled team would
  be a free way into the Cockpit, which is what this mission closes.
- A failed membership or bill read is unknown (503), never a refusal and never a grant.
- It grants `remote_access` only: no AI scope, no change of tier.
- A request in a team's tenant is a Director's and is served as today, whatever the seat or the bill.
- Where Teams is not released, no team registry is wired and the rule is absent.

Open, for the owner, and not decided here: the Owner of a team with no running bill, on a personal Free
account outside the grace, cannot open the Cockpit.

## What does not change

- **Self-host (decision D5).** No entitlement registry means no lease service and no gate at the mint.
  Not one line of the `entitlements == null` path changes.
- **The Director.** A Free account's Director enrols, tunnels and has its usage recorded exactly as
  today. `hosted_gateway` still means that, and Free, Remote and Pro all hold it.
- **The AI gate (B1).** Untouched. Remote gets `remote_access` and still no AI scope.

## Known limits, accepted

- **A Director key used by hand.** Someone who takes the device key out of their own Director and puts
  it in a browser reaches the request routes as a Director. The Gateway cannot tell, because nothing
  attests that a Director is a Director. It is accepted: it takes deliberate work, it gives no AI and no
  phone app, the "people only" routes still refuse that key, and the same person may run a self-hosted
  Gateway for nothing (D5).
- **A stream already open** when an account loses `remote_access` is checked when it was opened. If the
  existing 60 second sweep can close a phone or browser stream on that change with a small edit, it
  does; otherwise the stream lives until it next reconnects, and the pull request says so.
