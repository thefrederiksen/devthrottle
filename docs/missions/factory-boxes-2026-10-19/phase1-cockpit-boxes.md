# Factory Boxes, phase 1 - the Boxes screen in the Cockpit (the proof)

Mission issue: #4258. This is the Cockpit half of phase 1: a Boxes screen that lists the desktops the account's
Directors host and opens one. It is built against the merged list contract
(`phase1-hosted-desktop-list.md`, sections 5 and 6) and the relay leg's ticket route as it stands on the branch
`boxes/phase1-relay-leg` (pull request #4280, not yet merged): `POST /boxes/{boxId}/desktop-ticket` answering
`{ "url": "...", "expiresAtUtc": "..." }`, or `{ "error": "..." }` on a refusal.

## What was built

| Part | Where |
|---|---|
| The two calls, typed, for the Cockpit and later the phone | `packages/client-core/src/boxes/boxesClient.ts` (`getBoxes`, `getDesktopTicket`) |
| The Gateway's switch, read once per page load | `packages/client-core/src/boxes/useBoxesSwitch.ts`, from `boxes` on `GET /account/status` (added to `AccountStatus` in `account/accountClient.ts`) |
| The page | `apps/cockpit/src/boxes/BoxesView.tsx`, `boxes.css` |
| The route | `/boxes` in `apps/cockpit/src/routes.tsx` |
| The rail entry and its icon (a cube) | `apps/cockpit/src/AppShell.tsx` (`BOXES_ITEM`, `withBoxes`), `components/NavIcon.tsx` |
| A reload of `/boxes` gets the page, not the JSON list | `boxes` added to `CockpitReactApp.BrowserPageRoots` (Gateway) and a matching split in the Cockpit dev proxy (`apps/cockpit/vite.config.ts`) |

### Where the rail entry sits, and why

In Work, directly after Factories - and after the Mail Desk when the Mail Desk is offered too. That is where the brief
put it, and the existing rail says the same: the Mail Desk sits after Factories because it is a factory, and a box is
a desktop a factory works on, so it belongs with the factory items. Both orders are pinned by tests.

### How each rule of the brief is met

- **Shown only when the Gateway says so (rule 7).** The rail entry is drawn only when `GET /account/status` answers
  `"boxes": true`. False, not yet answered, or unreadable: no entry. At `/boxes` the page asks the same switch: false
  is the Cockpit's ordinary "Page not found", byte for byte the same markup as an unknown address (a test compares the
  two), and the list is never asked for. A switch that cannot be read is shown as the failure itself, never as
  "Page not found", which would be a guess.
- **Every field verbatim.** Display name, box id, machine name, Director name and state are printed exactly as
  `GET /boxes` sent them. A Director with no display name (the contract sends `""`) has no Director field rather than
  a made-up one. A state the Cockpit has never heard of is printed as it came.
- **Empty, loading, error.** "Loading..." shows at once, while the switch and then the list are asked. An empty list
  says "No Director in your account is hosting a desktop. The Gateway lists 0 boxes." with the Gateway's count. A
  failure shows the Gateway's own sentence in the red banner, with Try again.
- **Open.** The new tab is opened inside the click, before anything is awaited, so no pop-up blocker stops it; it shows
  "Opening the desktop..."; then the ticket is asked for and the tab is sent to the returned `url` exactly. Before it
  leaves, the tab's link back to the Cockpit (`window.opener`) is cut, so the desktop's page cannot steer the
  Cockpit's tab. A refused ticket closes the tab and puts the Gateway's sentence on that row only. If the browser
  refuses to open a tab at all, the row says so and no ticket is asked for. The desktop is never put in a frame, and
  the Cockpit never builds a desktop address.
- **Failures are reported.** List and ticket failures go through `describeAndReport` (shown and reported); the
  switch's failures are reported by the hook itself, once per attempt, and asked again on the shell's rhythm
  (15 seconds, 30, then every 60).

### One thing found that the brief did not name: `/boxes` is two things at one address

`GET /boxes` is the Gateway's JSON list, and `/boxes` is also the page's address. The Gateway hands a browser's page
load the Cockpit only for the paths named in `CockpitReactApp.BrowserPageRoots`; without `boxes` there, a reload, a
bookmark or a typed address would have shown the person the raw JSON list instead of the screen. `boxes` is added,
and `BoxesBrowserPageTests` pins all three halves: a browser load of `/boxes` gets the Cockpit; a program asking for
JSON (or with no Accept at all) still gets the list; and nothing under a box - `/boxes/{id}/desktop/...`, the ticket
POST - is ever turned into the page. The rule matches one or two segments only, so the desktop routes (three or more)
cannot be caught by it. The Cockpit dev server's proxy got the same split, or `/boxes` could not be loaded under
`npm run dev`.

## Tests

All fakes: `fetch` stubbed or the client modules mocked, answers settled by the test itself. No network, no sleeps
(the one retry test uses fake timers).

| Suite | New tests | What they prove |
|---|---|---|
| `client-core/src/boxes/boxesClient.test.ts` | 9 | `GET /boxes` and `POST /boxes/{id}/desktop-ticket` (id encoded); the contract shape carried verbatim; empty list; the Gateway's sentence on a refusal; a row missing a field, a body that is not JSON, and a ticket with no address are thrown, never drawn |
| `client-core/src/boxes/useBoxesSwitch.test.tsx` | 7 | asking, on, off; a failure carries the Gateway's sentence and is reported; asked again after 15 seconds; a settled answer is not asked again; unmounting stops the asking |
| `client-core/src/account/accountClient.test.ts` | 1 | `boxes` carried; missing reads as false |
| `cockpit/src/boxes/BoxesView.test.tsx` (through the real route table) | 15 | flag off looks exactly like an unknown address and never asks for the list; loading at once; switch unreadable shows the failure, not "Page not found"; every field verbatim, including an empty Director name and an unknown state; empty; error with Try again; Open opens the tab before asking for the ticket and shows "Opening the desktop..."; the tab is sent to an odd, uncookable `url` byte for byte and its opener is cut; a refused ticket closes the tab and shows the sentence on that row only; a second Open clears the old message; a blocked tab asks for no ticket; no frame |
| `cockpit/src/AppShell.test.tsx` | 4 | entry after Factories when true; after the Mail Desk when both are on; absent when false; absent and reported when the status cannot be read |
| `cockpit/src/routes.test.tsx` | 1 | `/boxes` is an address of the real route table |
| `Gateway.UnitTests/Boxes/BoxesBrowserPageTests.cs` | 5 | the page-load split above |

Runs, on this branch, on devlinux:

- client-core: 184 files, **2,325 passed**, 0 failed (`npx vitest run --minWorkers=1 --maxWorkers=2`).
- Cockpit: 127 files, **1,358 passed**, 0 failed. The error-display scan read 154 files and 225 sites, 0 unreported;
  the one new exemption is the switch banner, whose failures the hook reports itself.
- Gateway.UnitTests, filter `Boxes` or `Cockpit`: **95 passed**, 0 failed.
- `npm run typecheck` (every workspace): clean. `npm run lint`: the same 5 errors main has today
  (`factory/FactoryRecordsTab.tsx`, `factory/fixtures.ts`, `push/sw.test.ts`, two in `apps/mobile`); none in a file this
  change touches. Checked by running the lint with this change stashed.

## The screenshots

Taken from the **Cockpit dev server against a faked Gateway**, not a real one: `phase1-cockpit-boxes/fake-gateway.mjs`
answers `GET /account/status`, `GET /boxes` and the ticket route in the contract's shapes, and
`phase1-cockpit-boxes/shoot.mjs` drives headless Chrome (155) over the DevTools protocol. Desktop is 1440 by 900;
phone is 390 by 844, with the Cockpit's rail collapsed to its icons (its own remembered setting), as a person on a
phone would have it. The live run through the real Gateway is the Tech Lead's, after deploy. `results.json` is the
run's own record.

| Shot | What it shows |
|---|---|
| `list-desktop.png`, `list-phone.png` | two boxes; the second has no Director name, so no Director field |
| `empty-desktop.png`, `empty-phone.png` | count 0 |
| `error-desktop.png`, `error-phone.png` | `GET /boxes` refused with 503 and the Gateway's sentence, with Try again |
| `nav-flag-on.png` | Work reads: Sessions, Fleet Map, Fleet Manager, Factories, **Boxes**, History, Voice Recorder, Reports |
| `nav-flag-off.png` | Work reads: Sessions, Fleet Map, Fleet Manager, Factories, History, Voice Recorder, Reports |
| `boxes-flag-off.png` | flag off, `/boxes` loaded by its address: the ordinary "Page not found", no Boxes entry |
| `open-ticket-error.png` | the ticket refused: the row shows "No connected Director in your account hosts a desktop for that box." and the tab that was opened is closed again (two pages before, two after) |
| (no picture) `open-ok` in `results.json` | a real click with a user gesture: the new tab arrived at exactly the address the fake ticket answered, `.../__qa/desktop/demo-desktop-soren?ticket=fake` |

To run them again: start `node fake-gateway.mjs 7979`; in `apps/cockpit`,
`COCKPIT_PROXY_TARGET=http://127.0.0.1:7979 npx vite --host 127.0.0.1 --port 5173`; Chrome with
`--headless=new --remote-debugging-port=9333`; then `node shoot.mjs`.

## What this proof does not cover

- **The real Gateway and the real relay.** The ticket route is built against branch `boxes/phase1-relay-leg` as read
  on 20 October 2026; if its answer changes before it merges, `getDesktopTicket` and its tests change with it. Nothing
  here proves the desktop itself opens through the relay - that is the relay leg's proof and the Tech Lead's live run.
- **The `/boxes` page-load fix through a running Gateway.** The unit test pins the rule `IsBrowserPageRequest` decides;
  a hard reload of `/boxes` on the deployed Gateway is the check that it is wired, and it belongs in the live run.
- **The phone app.** The calls are in client-core so the phone can reuse them; there is no phone screen in phase 1.
- **The dev server's `/sessions`.** Under `npm run dev` a page load of `/sessions` is sent to the Gateway (a dev proxy
  limitation that predates this change), so the rail shots are taken on `/history`.

## Developer's answers

No findings to answer: the Codex review (`reviews/phase1-cockpit-review-codex.md`, in this folder unchanged) found none.
