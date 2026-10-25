# Alerts - Phase 3 (client-core) review

Reviewer: a separate review session (9e1fa530), 25 October 2026. Pull request 4471, branch
`alerts/phase3-client-core`, two commits (`bace2c13e`, `0a037f5a6`) on top of origin/main `d0db12038`.

## Verdict

**Approve. No finding proves harm.** Five observations are recorded below for the Delivery Lead and the
Phase 2 and Phase 4 Developers; none blocks the merge.

## Scope

**What I read.**

- `docs/missions/alerts-2026-10-24/mission.md`: sections 3, 4, 5.1 to 5.6, Phase 3 of section 6, and
  section 7 (the check).
- The full diff `origin/main...HEAD`: 17 files, 2,187 insertions, 197 deletions. Every new file in
  `packages/client-core/src/alerts/` was read whole:
  - `alertsClient.ts` - the four typed calls, `AlertDto` as a discriminated union on `kind`, and the pure
    helpers (`newestFirst`, `alertClock`, `badgeText`, `bellLabel`, `askingSessionId`).
  - `AlertsProvider.tsx` - the one poller (`useVisiblePolling`, 5 seconds), the store, the read sequence
    guard so an older answer can never land over a newer one, `markSeen`, `clear`, `clearAll`,
    `waitingFrom`.
  - `AlertsBell.tsx`, `AlertsTray.tsx`, `AlertDetail.tsx` - the bell with its badge and label, the modal
    tray with its two sections, the focus trap and Escape, the seen-marking on open, the opened alert with
    the kind's action block and the collapsed Details.
  - `actions/RestartRequestActions.tsx`, `actions/SecretTransferActions.tsx`,
    `actions/MessageRequestActions.tsx` - the three answer blocks.
  - `alerts.css` - the one stylesheet and the variables a shell may set.
  - `alertsFixtures.testkit.ts`, `Alerts.test.tsx`, `actions/actions.test.tsx`.
  - The four modified files: `RestartRequestsPanel.tsx` and its test, `SecretTransfers.tsx`,
    `fleet/LinkRequestsPanel.tsx`, and `package.json` (seven new export paths).
- The shared code the branch leans on, in this tree (which is origin/main plus these two commits):
  `polling/useVisiblePolling.ts` (calls the refresh at once, ticks while visible, aborts the in-flight read
  when the tab hides), `ui/useDismissOnBackdrop.ts`, `api/client.ts` (`gatewayFetch`, `GatewayError.from`
  reading the Gateway's `error` sentence out of a failed body, `POLL_TIMEOUT_MS`),
  `errors/reportClientError.ts` (`describeAndReport` with the background option), the
  `SecretTransferPlace` type, and `docs/CockpitVisualStyle.md` (the rule on colour tokens and the two
  allowed literal whites).
- On origin/main, to see what the client was built against: there is NO Gateway side yet. No `AlertDto`,
  no `alert_marks`, no `/gateway/alerts` route exists on origin/main, and no Phase 2 branch or pull
  request exists. The contract the client encodes was checked against the mission document alone.
- The open Phase 1 pull request 4470 (`alerts/phase1-stopgap`), because both branches change
  `RestartRequestsPanel.tsx` and its test: its diff, and `git merge-tree` of the two branches.
- `apps/mobile/src/pages/LinkRequestsPage.test.tsx`, which renders the message request card this branch
  changed, to know that the app suites reach the change.

**What I ran** (in this worktree, against the node modules already present; `npm ci` was not run, as
instructed; `git status` was empty before and after).

| Command | Result |
|---|---|
| `npx tsc --noEmit` in `packages/client-core` | exit 0 |
| `npx vitest run` in `packages/client-core` | 188 files, 2,383 tests passed, exit 0 |
| `npx vitest run` in `apps/mobile` | 38 files, 198 tests passed |
| `npx vitest run` in `apps/cockpit` | 131 files, 1,420 tests passed |
| `npx eslint` on `src/alerts`, `src/restart`, `src/secrets`, `src/fleet` in client-core | clean, exit 0 |
| `git merge-tree --write-tree origin/alerts/phase1-stopgap HEAD` | a tree, no conflict |

I did not run the root `npm run lint` over the whole repository (the Phase 1 review records five
pre-existing errors in files outside both branches) and did not run the dotnet suites: this pull
request changes no C#, and the mission's dotnet filters would match nothing on this branch.

**What I could not reach.**

- The Gateway side does not exist, so nothing here proves the client and the Gateway agree. The client's
  expectations are listed under Observation 1 so the Phase 2 Developer can build to them.
- No browser rendering at 360 px or in a 280 px popover. The wrapping is proved by the stylesheet rules
  and the test that reads them (`AlertDetail_OverflowFixture_...`); jsdom does no layout. The mission
  puts the screenshot proof in Phase 4's QA run, so this is within what Phase 3 owes.
- The combined result of Phase 1 and Phase 3 merged together was not built or tested. Git merges them
  without conflict; I read both diffs for a semantic clash and found none (Observation 4).

## Checks against the mission

- **Section 5.3, the components.** All six exist with the names and placement the mission gives:
  `alertsClient.ts`, `AlertsProvider` + `useAlerts()`, `AlertsBell`, `AlertsTray`, `AlertDetail`,
  `alerts.css`, in `packages/client-core/src/alerts/`. `waitingFrom` is on `useAlerts()` as 5.3 asks.
- **The bell** shows the Gateway's `needsAnswerCount` and nothing else (the test with twelve as the count
  and one alert in the list shows "9+"; the client never recounts), no badge at zero, "9+" above nine,
  `aria-label` "Alerts" / "Alerts, 1 needs you" / "Alerts, 2 need you", badge colour `--attention`.
- **The tray**: "Needs you" above "Earlier", newest first by `updatedAtUtc`, one row per alert with the
  unseen dot, title, ellipsised one-line summary, status word (Earlier only), time, an X on Earlier rows
  only, "Clear all" in the header only while Earlier holds anything, "Nothing needs you." as the empty
  state once loaded, a load-failure line that keeps the last-known list (proved with fake timers and a
  503 on the second poll), Escape closes, Tab and Shift+Tab wrap inside, focus returns to the bell, rows
  are buttons. `role="dialog"` with `aria-modal`.
- **Opening marks seen**: only the alerts whose `seen` is false, once per alert version, and nothing is
  sent before the tray opens (both proved).
- **The detail**: title and status in the header, the summary, "Expires HH:MM" on a needs-answer alert,
  the kind's action block, and the diagnostics (launcher version, capability sentences, guarded restart
  reason, outcome, record) inside a `<details>` that is closed by default (proved).
- **The action blocks are the existing card internals moved, not rewritten.** `RestartRequestActions` is
  the old card's `act`, `confirming`, `busy`, `failure` and the two button groups verbatim, including the
  two-step confirm with the Gateway's `acceptSentence` and the `mounted` guard; its three tests moved
  with it and two were added (Not now; offered only while `canAccept`). The secret transfer and message
  request blocks carry the same `answerSecretTransfer(id, approve, where)` and
  `answerMessageLinkRequest(id, choice)` calls and the same refusal-in-the-Gateway's-words behaviour;
  the busy and error state moved from the two providers into the blocks, which is required for a block
  that must also stand alone inside the detail. The three old cards now render the blocks, and their
  existing tests (`SecretTransfers.test.tsx`, `RestartRequestsPanel.test.tsx`, the mobile
  `LinkRequestsPage.test.tsx` with its Approve and Deny through the Gateway call) still pass, unchanged
  or trimmed only where a test moved.
- **The place of answer survives**: `where` travels bell -> tray -> detail -> `SecretTransferActions`, and
  the block's test proves the body `{approve, where}` for "phone" and for "cockpit".
- **Answered means gone from the count at once**: an answer in the detail calls `refresh()`; the test
  declines a restart and watches the badge disappear and "Declined" appear without a poll tick.
- **A body without `alerts` is an error, never an empty list** (5.2) - and the branch extends it to a
  missing `needsAnswerCount`. Both proved.
- **Section 6, Phase 3's test list**: zero / one / many, badge counts only needs-answer, "9+", seen
  marking, clear, clear-all, clear refused with the Gateway's sentence, load failure keeps the list, the
  overflow fixture (46-character version and 300-character reason, both asserted by length), keyboard and
  focus - every item has a test. Beyond the list: the deep link opens the tray on that alert, an unknown
  kind from a newer Gateway says so plainly, `waitingFrom` filters by session and kind, and a guard test
  fails if any stylesheet under `apps/` writes an `.alert-` or `.alerts` rule (there are 34 stylesheets
  there today; none does).
- **The stylesheet** uses the theme tokens throughout; the two `#fff` for text on the accent and
  attention fills and the one soft shadow are exactly what `docs/CockpitVisualStyle.md` permits as
  non-tokens. Every text class the tray draws has `overflow-wrap: anywhere` and `min-width: 0`, read back
  by the test. The shell variables are documented at the top of the file, and the tray never exceeds the
  viewport less 16 px whatever `--alerts-tray-width` says.
- **Merge plan (section 8)**: nothing mounts the new components yet; the old panels stay and now render
  the shared blocks, so there is no build where both or neither are on screen.

## Findings

None. Nothing I read, ran or reasoned about produces a wrong screen, a lost answer, a wrong count or a
broken test with the code as it stands in this phase.

## Observations (no proven harm; for the seats named)

1. **For the Phase 2 Developer - what this client expects of the Gateway.** Since no Gateway code exists
   yet, the contract lives only in the mission document and in this branch. The client requires, exactly:
   `GET /gateway/alerts` answering `{ alerts: AlertDto[], needsAnswerCount: number }` (a 200 missing
   either is shown as a failed read); `kind` as one of `"restart-request"`, `"secret-transfer"`,
   `"message-request"`; `detail` serialised with the SAME camelCase JSON the existing
   restart-request, secret-transfer and link-request routes emit, because the client reuses those types
   (`DirectorRestartRequest.canAccept`, `machine`, `id`; `SecretTransfer.transferId`, `canAnswer`,
   `askedBySessionId`; `MessageLinkRequest.requestId`, `status`, `requesterSessionId`);
   `POST /gateway/alerts/seen` with body `{ ids: string[] }`; `POST /gateway/alerts/{id}/clear` where
   the id arrives URL-encoded (the colon as `%3A`); `POST /gateway/alerts/clear`; and a refusal as a
   non-2xx whose JSON body carries the sentence in `error` (that is what `GatewayError.from` reads). The
   mission's own example id `restart:3f9c...` (5.2) differs from the fixtures' `restart-request:req-1`
   (the second commit's "full kind word"); the client never parses the id, so either form works, but the
   two documents should say the same thing.
2. **For the Delivery Lead - the unseen dot is visible for one round trip only.** Opening the tray posts
   `seen` for every unseen alert and then re-reads the list, so the dot and the "New:" text disappear as
   soon as that read lands. That is a literal reading of 5.3, which asks for both the dot and the
   marking-on-open, and the implementation is faithful to it. If the owner expects the dot to say "new
   since you last looked" for the length of a visit, the mission document, not this code, needs the
   decision (for example: mark seen on close, or on opening the alert).
3. **For the Phase 4 Developer - the deep-link effect.** `AlertsBell` reopens the tray whenever
   `openAlertId` is truthy, and its effect also depends on `onOpenChange`. A shell that passes an
   inline arrow for `onOpenChange` AND leaves `openAlertId` set after the tray closes would see the tray
   reopen on the next render. The prop's own comment says the shell clears the deep link on close; do
   that in the `onOpenChange(false)` handler (the state updates batch into one render), or pass a stable
   callback. With a shell that follows the comment there is no harm, which is why this is not a finding.
4. **For the Delivery Lead - Phase 1 and Phase 3 together.** Both change `RestartRequestsPanel.tsx` and
   its test. Git merges them cleanly. Phase 1's test "renders the longest real strings" asserts that every
   `<p>` in the card carries `restart-request-line`; with Phase 3 the card's answer block renders no
   `<p>` until Accept is pressed or a refusal arrives, so that test still holds in the merged tree as far
   as reading shows. Whichever merges second should run the client-core suite once on the merged result
   before the pull request closes; I did not build the merged tree.
5. **For the Phase 4 Developer - the bell cannot be clicked to close while the tray is open.** The
   backdrop is fixed over the whole viewport, so a pointer on the bell lands on the backdrop, and the
   backdrop's dismiss closes the tray - the same outcome by a different path. Keyboard users reach the
   bell's own toggle. No harm; noted so a Phase 4 screenshot run does not read it as a defect.
