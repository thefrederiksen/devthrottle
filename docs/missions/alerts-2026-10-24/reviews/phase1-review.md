# Alerts - Phase 1 (stopgap) review

Reviewer: a separate review session (585e89bb), 25 October 2026. Pull request 4470, branch
`alerts/phase1-stopgap`, one commit `36366ea0e` on top of origin/main `d0db12038`.

## Verdict

**Approve. No finding proves harm.** Three observations are recorded below for the Delivery Lead; none
blocks the merge.

## Scope

**What I read.**

- `docs/missions/alerts-2026-10-24/mission.md`: sections 2, 3, and Phase 1 of section 6, plus section 7
  (the check) to know which commands the mission expects to pass.
- The full diff `origin/main...HEAD`: five files, 96 insertions, 32 deletions.
  - `packages/client-core/src/restart/restartRequests.ts`: `visibleRestartRequests` now keeps only
    `Pending`; the thirty-minute closed-request rule and `CLOSED_REQUEST_VISIBLE_MS` are gone.
  - `packages/client-core/src/restart/RestartRequestsPanel.tsx`: the `now` prop and its props interface
    are gone; the capability `reason`, `guardedRestartReason` and the launcher version move into a
    closed `<details>` with a "Details" summary.
  - `packages/client-core/src/restart/RestartRequestsPanel.test.tsx`: four new tests (Details closed and
    holding the diagnostics; the 46-character version and 300-character reason rendered in full; both
    apps' stylesheets carry `overflow-wrap: anywhere` and `min-width: 0` on `.restart-request-line`; the
    panel does not render an accepted request) and the `visibleRestartRequests` test rewritten.
  - `apps/cockpit/src/styles.css`, `apps/mobile/src/styles.css`: one identical block each giving every
    card element `min-width: 0; overflow-wrap: anywhere`, letting the head wrap, and styling the
    Details paragraphs and summary.
- On origin/main, to check the client's assumptions against the Gateway: the whole of
  `RestartRequestsPanel.tsx`, the restart request types and client, the restart CSS blocks in both apps,
  the two mount points (`apps/mobile/src/pages/Home.tsx`, `apps/cockpit/src/sessions/SessionRoster.tsx`),
  every importer of `RestartRequestsPanel` and `visibleRestartRequests`, the `DirectorRestartRequestState`
  enum, and `DirectorRestartRequestStore` (expiry on every read, `Stamp` computing `CanAccept`).

**What I ran** (in this worktree, after `npm ci`; the tree stayed clean, `git status` empty).

| Command | Result |
|---|---|
| `npm run typecheck` | green, all four workspaces |
| `npm test --workspace @devthrottle/client-core` | 186 files, 2353 tests passed; exit 1 from 2 unhandled errors raised by `src/terminal/stream.test.ts` (a reconnect timer firing after its test ended). That file alone: 10 passed, no error. The file and everything it imports are outside this diff. |
| `npm test --workspace @devthrottle/mobile` | 38 files, 198 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 131 files, 1420 tests passed |
| `npm run lint` (`eslint .`) | 5 errors, in `apps/cockpit/src/factory/fixtures.ts`, `apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx`, `apps/mobile/src/pages/VoiceMode.tsx`. None of these files, nor `eslint.config.js`, nor the lockfile, is in the diff. Four of the five are "Definition for rule ... was not found". |
| `npx eslint` on the three changed TypeScript files | clean, exit 0 |

**What I could not reach.** I did not render either app in a browser at 360 px, so the wrapping is proved
by the stylesheet rules and the test that reads them, not by a screenshot. The mission puts the
screenshot proof in Phase 4's QA run, so this is within what Phase 1 owes. I did not run the Gateway;
the Gateway is not changed by this pull request.

## Checks against the mission

- **Phase 1 says `visibleRestartRequests` shows only `Pending`.** It does: `requests.filter((r) =>
  r.state === "Pending")`. The Gateway's `Stamp` sets `CanAccept = Pending && now < ExpiresAtUtc` and
  applies expiry on every read, so a `Pending` record the client receives is always one the owner can
  act on. There is no path to a pending card with no buttons and no way out; a pending request leaves
  the screen when answered or within thirty minutes when the Gateway stamps it `Expired`.
- **The owner's card from 24 October (accepted, lingering up to three hours)** no longer renders: the
  panel test "does not render a request the owner already accepted" proves it with a pending card as the
  arrival witness, so the absence is evidence, not a blank screen.
- **`overflow-wrap: anywhere` + `min-width: 0` on every card line in both apps.** Present on every
  element the card emits: the article, head, title, state, line, error, details, actions. The head also
  wraps. The one text element not a `<p>` on the card, the confirmation `<span
  class="restart-request-line">` inside the flex `.restart-request-actions`, is covered by the same rule.
  Specificity: `.restart-request-details > p` (0,1,1) beats `.restart-request-line { margin: 0 }`
  (0,1,0), so the 4 px top margin inside Details applies as intended.
- **Capability sentences behind a `<details>` "Details".** Done, closed by default, verbatim, with the
  launcher version added as its own line. The test asserts the main lines outside Details carry none of
  the diagnostics.
- **Both apps' tests mock the panel** (`RestartRequestsPanel: () => null` in five mobile tests), so the
  removed `now` prop breaks no caller: both shells mount `<RestartRequestsPanel />` with no props on
  origin/main.
- **Tests updated.** Yes, and the fixtures match the mission's own overflow fixture (46-character
  version, 300-character reason).

## Findings

None that prove harm.

## Observations (no harm proved; for the Delivery Lead)

1. **The owner now gets no on-screen outcome after accepting a restart.** On origin/main an accepted
   request showed its `progress` line and, once closed, its `stateReason` for thirty minutes. On this
   branch the card vanishes the moment the accept lands, and a later `Abandoned` or `Expired` (three
   hours with no final report) is shown nowhere on the phone or the Cockpit until Phase 4's "Earlier"
   section exists. This is exactly what Phase 1 orders and what the owner asked for on 24 October, so it
   is an accepted trade-off, not a defect. It is recorded so the gap is known for the length of the
   mission and so goal 6 (accepted moves to "Earlier" with a status line) is not forgotten.
2. **The stylesheet test is line-shaped.** "gives every card line overflow-wrap: anywhere and min-width:
   0 in both apps' styles" finds one source line containing `.restart-request-line`, `overflow-wrap:
   anywhere` and `min-width: 0` together. A reformat that puts the selector list and the declaration on
   different lines would fail this test while the CSS stayed correct. It passes today and Phase 4 deletes
   it with the card, so no action now; if it ever reds on correct code the ruling of 13 October applies.
3. **Two of the mission's check commands are red on this branch for reasons outside the diff.** The
   client-core suite exits 1 from a reconnect timer in `src/terminal/stream.test.ts`, and `npm run lint`
   reports five errors in four untouched files, four of them "rule definition not found". Neither
   involves a file, a dependency or a configuration this pull request changes, so the diff cannot be the
   cause, and the two are not a reason to hold it. They will meet Phase 4's QA run of section 7 unless
   someone fixes them first; the Delivery Lead may want them looked at on main.

## Not in this review

The secret transfer and message request panels, the Gateway, deployment. Phase 1 does not touch them.
