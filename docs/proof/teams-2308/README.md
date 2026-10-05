# Teams 2308 - Requests to the Owner and Managers: proof

devthrottle_internal#2308, screen S9.

## What is here

| File | What it shows |
|------|---------------|
| test-runs.txt | Every check run, with its counts |
| red-tests.txt | Each Gateway rule broken on purpose, the tests that went red, and the source put back |
| red-tests-cockpit.txt | The same for the Cockpit pages and their routes |
| mutate.py | The helper that breaks a rule and always puts the source back |
| take-screenshots.py | The one command that takes every screenshot below |
| desktop-*.png, phone-390-*.png | Real renders from the built Cockpit, served by a hosted Gateway with Teams switched on |

## The screenshots

They come from `TeamRequestProofRig` (in Gateway.UnitTests, skipped unless `CC_TEAMS_2308_PROOF_RIG` is set). It seeds
the team "Acme QA" with the fleet's test accounts: qa@ (Owner), tech@ (Manager), dev@ (Developer) and docs@
(Collaborator). docs@ has four requests: one marked Not doing this with a reason, one Accepted, one Accepted then Done,
and one just Sent. Run it again with:

    python docs/proof/teams-2308/take-screenshots.py

| Screenshot | What it shows |
|------------|---------------|
| desktop-1, phone-390-1 | The Collaborator's page at /requests, in #2306's slot: the write box, and their requests newest first, each with its trail. The declined one shows who said so and why |
| desktop-2, phone-390-2 | The Owner's list at /team/{teamId}/requests: every request, with only the buttons the Gateway's verdicts allow |
| desktop-3, phone-390-3 | A Developer at the same address: the Gateway's refusal, and no list |
| desktop-4 | The Manager starts Not doing this: the reason is asked for, and the button stays off until one is written (the driver checks it is disabled) |
| desktop-5 | After it is sent: the card shows the Gateway's answer, and the reason box has closed |
| desktop-6, phone-390-6 | The Collaborator sees the change: who made it, and the reason |

At 390 pixels the whole Cockpit keeps its menu open until the person collapses it. The Owner and Developer shots at
phone width collapse it with the menu's own button first. A Collaborator's app is a bar at that width, so nothing is
collapsed for them.

## Where each page lives

- The Collaborator's Requests page fills #2306's `/requests` slot. #2306's route guard and navigation are unchanged.
- The Owner and Managers' list is at `/team/{teamId}/requests`, beside the Team page (`/team/{teamId}/members`) and the
  invite form. The team comes from the address, as it does on those pages, and like them it has no navigation entry yet.
  Anyone else who opens it gets the Gateway's refusal.

## Issue test 3 with team-bound keys

#3530 (merged) lets a Director hold a key bound to the team's tenant. The variant test sends the Owner's and Manager's
team Director keys, and a session key in the team's tenant under each, to all six request routes. Each one is refused
and nothing changes in the store.

Neither key is refused by the request routes' own check today, and the test pins exactly what does refuse each one:

- A team session key is refused first by the session-key guard (403 `session_key_out_of_scope`): a session key may not
  call a team route at all, in any tenant.
- A team Director key authenticates, and then the request-path access lease answers 402 `hosted_subscription_required`
  before the request routes see it. The lease reads a personal account's bill, and a team's tenant belongs to no one
  person; #3530's own tests record the same.

For the same reason, a team Director cannot open the tunnel over the wire yet. So the "a live session receives nothing"
half of test 3 runs on Directors on the Owner's and Manager's own accounts. When the lease reads the team's bill, the
Director-key half of the variant goes red on the status. The answer it must then show is the routes' own 403
person_only, which the personal-account test already proves for Director keys and session keys.
