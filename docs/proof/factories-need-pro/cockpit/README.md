# Factories need Pro - the Cockpit upgrade page (issue #4326)

What changed: when the Gateway's switch answer says `reason: "plan_required"`, the Factories pages show an
upgrade page - the Gateway's `title` as the heading, its `howToStart` as the body, a primary "See Pro" link to
its `subscribeUrl` (new tab, `noopener noreferrer`), a "Try again" button that forgets the cached answer and asks
the Gateway again (no page reload), and the hint from `RemotePlanNotice.tsx`. Every other off answer
(`not_switched_on`, `plan_unknown`, or no `reason` from an older Gateway) shows today's `factories-off` page.
A `plan_required` answer without its `title` or `subscribeUrl` is a Gateway fault and is shown and reported as an
error, the same way an off answer without `howToStart` already is.

## Commands and counts (run in the worktree, 21 October 2026)

| Command | Result |
|---|---|
| `npm ci` (root) | installed |
| `npm test` in `apps/cockpit` | 130 files, 1395 tests passed |
| `npm test` in `packages/client-core` | 185 files, 2331 tests passed |
| `npx vitest run src/factory/FactoryPages.test.tsx` in `apps/cockpit` | 15 passed (8 new) |
| `npm run typecheck` (root) | exit 0, no errors |
| `npm run lint` (root) | exit 1: 5 errors, all in files this change does not touch (`FactoryRecordsTab.tsx`, `fixtures.ts`, `push/sw.test.ts`, mobile `Recorder.tsx`, `VoiceMode.tsx`) - four are "rule definition not found", one is an address literal in a fixture |

New tests in `apps/cockpit/src/factory/FactoryPages.test.tsx`: `plan_required` renders the heading, body,
"See Pro" (href = `subscribeUrl`, target and rel checked), Try again and the hint; Try again asks the Gateway a
second time and shows the area once it is on; Try again while still without the plan shows the upgrade page again;
`plan_required` missing its address is a reported error; `not_switched_on` and `plan_unknown` render
`factories-off` with `howToStart`; no `reason` renders `factories-off`; `enabled` with `reason: null` renders the
children.

## Screenshots

Taken from the real `FactoryAreaGate` and the Cockpit's own stylesheets (`styles.css`, `components.css`,
`factory.css`), rendered inside the Cockpit's `main-pane` / `routed-page` classes by a temporary harness page
(not committed) with `fetch` stubbed to return the switch answer. Served by the Cockpit's Vite dev server started
in-process on port 5391 (checked free first), shot with Playwright, then stopped. The rail is not in these
pictures; the rail is unchanged by this work.

| File | What it shows |
|---|---|
| `upgrade-desktop.png` | 1280x720, `plan_required`: heading "Factories come with Pro", the body sentence, "See Pro" (primary) beside "Try again", the hint |
| `upgrade-phone.png` | 390x844, the same; the two buttons fill the width, one per line |
| `off-desktop.png` | 1280x720, `not_switched_on`: today's page, heading "Factories" and the Gateway's sentence |
| `off-phone.png` | 390x844, the same |

The harness checked the link in the browser: href `https://devthrottle.com/pricing`, target `_blank`, rel
`noopener noreferrer`.

## The phone app

`apps/mobile` has no factory area: the only factory references are in session controls
(`SessionControls.tsx`) and a test. Nothing to build there.
