# Cockpit factory map (issue #3383)

Mission document and proof. Standalone mission: one session from request to merged, reviewed by a second agent
before merge (the method, section 1).

## Why

The owner, 24 Sep 2026: "How do I see the map? If I'm looking at the factory, there should be a menu on the
factory to show this, how things are tied together." Factories are defined by talking to an agent, never drawn,
"but we need to be able to visualize it and see it".

## Goal

The owner opens Factory Agents, presses **Map** on a factory's card, and sees how its agents connect. The map is
the one the factory's own files say, and each agent shows the same live status as its card. Picking an agent
shows its spec: what it reads, what it makes, what done means, and what it may not do.

## Owner rulings this builds on (24 Sep 2026)

- The map lives with its factory: a Map tab on the factory's page, which opens first, and a Map button on every
  factory card. There is no Map tab across all factories.
- The map is generated from the factory's files with Graphviz, never drawn by hand. There is no editor: a
  factory is changed by talking to an agent.
- Chains listen only for run results. Business events are shown, not subscribed to.

## Design

| Piece | What it does |
|---|---|
| `PUT /gateway/factory/map` (`FactoryMapEndpoints`) | A factory publishes its map: boxes with positions and spec rows, arrows with their Graphviz paths. A session key may call it. Behind the factory agents switch, per account. |
| `FactoryMapStore` | Keeps the latest map per factory in the account's settings (`factory_maps`). A map is refused whole when any part is wrong: an unknown kind, an arrow to a missing box, a coordinate outside the drawing, too many boxes or words. |
| `GET /gateway/factory-agents/factories/{factory}/map` | The owner's Map tab. `FactoryMapFold` adds each agent's status word, tone and last run from the same card fold the Factories tab reads, and returns every word, tone, line style and SVG path finished (critical rule 7). A session key is refused, as on every owner page. |
| `FactoryCardDto.MapHref`, `FactoryAgentPageDto.FactoryHref` | The Map button on each card, and the agent page's breadcrumb back to its factory. |
| Cockpit `FactoryPageView`, `FactoryMap` | Route `/factory-agents/:factory`, with Map and Agents tabs. The map is drawn as React SVG elements from the Gateway's positions, never as markup handed in, so a published map cannot inject anything. Boxes are keyboard-selectable. |

Why the layout comes from the factory: the owner chose Graphviz, and the browser has no Graphviz. The factory's
tool runs Graphviz (`dot -Tjson0`) and sends positions and paths. The Cockpit then needs no graph-layout library,
and the picture in the Cockpit is the same layout as the committed `map.svg`.

The first publisher is the website factory's `cc-website-factory publish-map`, in the private cc-consult
repository.

## Proof

`proof/shoot.py` runs everything in one foreground run: a throwaway local Gateway from this build (factory
switch on, no authentication, port 7899) and the Cockpit dev server as its child processes. It seeds six record
rows, publishes the real website factory map, takes the screenshots, and stops both children before it exits.

| Screenshot | Shows |
|---|---|
| `01-factories-card-with-map-button.png` | The Factories tab: the new **Map** button on the card. |
| `02-factory-page-opens-on-the-map.png` | The factory page opens on Map: 9 boxes, 11 arrows. Chains are green on success and red to the owner on failure, the ask is amber, calls are dashed, and the trigger is dotted and grey because it is not switched on. Each agent carries its status. |
| `03-picked-sender-spec-beside-the-map.png` | Sender picked: its live status and last run from the Gateway, then its spec from its file. |
| `04-picked-scout.png` | Scout picked. |
| `05-agents-tab.png` | The Agents tab: the same rows as the card. |
| `06-agent-page-crumb-links-to-the-factory.png` | An agent page; its breadcrumb now links to the factory page. |
| `07-picked-mail-watcher.png` | Mail Watcher picked. |
| `08-phone-width.png` | 390 px: the map scrolls inside its frame. The rail does not collapse at this width, which is how the desktop Cockpit already behaves. |
| `put-responses.txt` | The real map published: 200, 9 boxes, 11 arrows. The same map with one arrow pointing at a missing box: 400, with the reason. |

Gates run on this branch:

| Gate | Result |
|---|---|
| Gateway unit tests (full) | 7,373 passed, 8 skipped, **8 failed**. All 8 are `PendingDeletionBadgeTests`, which fail at origin/main since #3384. This change touches no session code. Filed as #3386. |
| Gateway unit, factory and guard | 569 passed, 23 of them new (`FactoryMapTests`: store round trip, 18 refusals, fold tones and paths, status merge). |
| Gateway integration, factory and session key | 21 passed. New: a session publishes, the owner reads with live status, a broken map is refused, a session key is refused the owner's page, an unknown factory answers 404, and the route answers 404 with the switch off. |
| Cockpit (vitest, full) | 551 passed, 6 of them new (`FactoryMap.test.tsx`). |
| client-core (vitest, full) | 1,520 passed. |
| Typecheck (all workspaces), eslint on the changed folders | clean |

## Not proven here

- The hosted Gateway: this needs a release before the owner's own Cockpit shows a Map tab. Until then,
  `publish-map` against it answers 404.
- A map with dozens of agents. The limits are 80 boxes and 250 arrows, and nothing near that has been drawn.
- Colour by the day's run result (as the daily dashboards do). The Gateway has no notion of a factory run yet, so
  the boxes carry the card's status word (FAULT, PAUSED, WORKING, IDLE).
