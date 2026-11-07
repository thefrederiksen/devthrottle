# Mission Button on the Web - Developer proof

Mission: "Mission Button on the Web - the Cockpit and the mobile app show each session's Mission".
Branch `mission-button-web`, cut from origin/main at 1396d7a00. No Gateway change.

## The Gateway already delivers the field

Checked before building, on the branch's base. `StampFleetRolesAndFold` calls `MissionButtonFold.Stamp` on every
row, and all three paths the web clients read pass it the missions:

- the roster read the Cockpit and the phone poll (`GatewayEndpoints.cs`, the `/sessions` handler, `missionsFor:` passed);
- the by-id session read (`GatewayEndpoints.cs`, same call with `missionsFor:`);
- the pushed display-state path (`GatewayHost.cs`, `StampFleetRolesAndFold(..., missionsFor: missionsFor)`).

The wire name is `missionButton` with `verdict`, `label`, `colorKey`, `tooltip`, `documentAddress` (camel case,
the default web serialiser). Nothing to raise.

## What changed

- `packages/client-core/src/api/client.ts` - `missionButton?: MissionButtonDisplay | null` on `SessionDto`, and the
  `MissionButtonDisplay` interface matching the C# contract. Hand-written next to the type, for the same reason
  `modelDisplay` is: the committed `schema.ts` predates the field.
- `packages/client-core/src/sessions/MissionButton.tsx` and `missionButton.css` - the ONE shared component, exported
  as `@devthrottle/client-core/sessions/MissionButton`.
  - Shows `label`, `tooltip` as the `title` attribute, and draws the look `colorKey` names: `mission` indigo #4338CA
    filled (border #6366F1, white text), `mission-outline` the same indigo as an outline (text #A5B4FC, the desktop's
    outline text), `warning` yellow #CA8A04 (border #EAB308) with dark text #1F1F1F.
  - With a `documentAddress` it is a link: `href` is the Gateway's address verbatim, `target="_blank"`,
    `rel="noopener noreferrer"`. Without one it is a button.
  - An unknown colour key draws a visible problem note naming the key, and is reported centrally with
    `reportShownError` (background, so a poll repeating the same broken verdict reports it once). Never a guessed
    colour. An address that is not a Gateway path (`/...`, not `//...`) is the same kind of broken contract, as in
    the desktop's `MissionButtonPresenter.BuildOpenUrl`.
  - A null or absent `missionButton` renders nothing.
  - It never reads `verdict`. The only mapping is colour key to class, which is layout.
  - Touch: a long press (500 milliseconds) shows the tooltip in a small bubble and does not open the document; on a
    button with nothing to open, a tap shows it. Escape or a tap elsewhere closes it.
- Cockpit `apps/cockpit/src/sessions/SessionDetail.tsx` - mounted in the session header (the tab strip), after the
  model chip and before the session menu, with the selected row's `missionButton`. The brief said "near
  `SessionActionBar`"; the mission document says "the Cockpit session detail header beside its actions". The action
  bar sits at the bottom above the composer and is hidden on the Wingman tab, so the header beside the session menu
  is where both readings meet and where it shows on every tab - the desktop draws it in its tab bar the same way.
  Layout in `apps/cockpit/src/styles.css` (`.session-mission`).
- Mobile `apps/mobile/src/components/SessionAppBar.tsx` - mounted on row 1 of the session app bar, between the spacer
  and the alerts bell, with the live row's `missionButton` (`manage.session`). Row 2 stays the session name's alone.
  Capped at 46 percent of the screen width with an ellipsis, in `apps/mobile/src/styles.css`.
- Roster rows are unchanged.

## Tests

New:

- `packages/client-core/src/sessions/MissionButton.test.tsx` - 15 tests: each colour key, the verdict word never
  picks the look, null and absent render nothing, unknown key shows the note and reports it, a non-Gateway and a
  protocol-relative address are problems, the link target and new tab, no link without an address, tap shows the
  tooltip, long press shows it and blocks the open, a short tap opens normally.
- `apps/cockpit/src/sessions/missionButtonMount.test.tsx` - 3 tests: the selected row's verdict is drawn in the
  header before the session menu (another row's is not), the yellow verdict, nothing without a verdict.
- `apps/mobile/src/components/sessionAppBarMission.test.tsx` - 4 tests: the indigo link in the session app bar, the
  yellow verdict, nothing without a verdict, nothing without a live row.

Results, run on this branch on 7 November 2026 (the local gate runs no web tests):

| Package | Type check | Tests |
|---|---|---|
| `packages/client-core` | `tsc --noEmit` clean | 193 files, 2,495 passed, 0 failed |
| `apps/cockpit` | `tsc --noEmit` clean | 138 files, 1,504 passed, 0 failed |
| `apps/mobile` | `tsc --noEmit` clean | 41 files, 214 passed, 0 failed |

`eslint` on every changed source file: clean. The error display scans in client-core, the Cockpit and the phone
pass with the new problem note (it shows only the reported sentence).

## Screenshot

`mission-button-states.png` - the shared component itself, built from the real source and stylesheet into a scratch
page and captured with headless Edge: the three looks, the unknown-key problem note, the null verdict drawing
nothing, and the touch tooltip open.

![Mission button states](mission-button-states.png)

I could NOT screenshot the two surfaces themselves locally: there is no mock Gateway for the Cockpit or the mobile
app, and running a test Gateway with a Director and missions is out of this seat's scope. The screenshots of both
live surfaces are the mission's QA step after the deploy.

## Review findings

To be answered here when the separate review session reports.
