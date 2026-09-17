# Brief - phase 3 viewer Worker: the Reports view in client-core, the Cockpit and the phone

You are a Worker on the Dev Reports mission. Your supervisor is the phase 3 Manager (session 14fde710). You report
to it, once, when done - one line, `cc-devthrottle message send 14fde710 "..."`. You never contact the owner.
Conduct: `cc-devthrottle workflow instructions mission --version 17` (read the Worker parts).

Issue: #3010 (child of #2936). Your worktree: `D:\ReposFred\devthrottle-dev-reports-p3-viewer`, branch
`mission/dev-reports-p3-viewer`, cut from `mission/dev-reports-p3`. Commit and push to that branch as you go. Never
merge anything, never push to main or to `mission/dev-reports-p3` - the Manager merges your branch.

## Read first

- `docs/missions/dev-reports/HANDOFF-phase-3.md` - the phase mandate. Its seven design rulings bind you.
- `docs/missions/dev-reports/STATE.md` - mission rulings 1-9 bind you.
- `packages/client-core/src/devreports/CONTRACT.md` - the whole thing. **Section 4 (Trust) is the law for a host.**
- `packages/client-core/browser-tests/dev-report-notes-proof/index.html` - a test host that already does section 4
  correctly. It is your reference implementation; port its logic, do not invent a new one.
- `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` - the four OWNER routes and the exact response shapes. Call
  only those four. Do NOT edit Gateway code; a phase 2 fix round is changing delivery on the mission branch.
- Repository `CLAUDE.md` rules 7 (the Gateway rules, the client renders) and 8 (one page on two surfaces lives
  once in client-core; follow the same pattern as `packages/client-core/src/settings/`).

## What to build

**1. client-core (`packages/client-core/src/devreports/`), everything that is not frame chrome:**

- A typed client for the owner routes: list (`GET /dev-reports?sessionId=`), detail (`GET /dev-reports/{id}`),
  html (`GET /dev-reports/{id}/html?version=`, plain text, version in `X-Dev-Report-Version`), send
  (`POST /dev-reports/{id}/send`). Use the existing Gateway fetch plumbing in `src/api/client.ts`, the way other
  routes do. A 404 on a report means it does not appear - no special error screen.
- **The frame host**, framework-free (a class or module testable without React), doing CONTRACT section 4 rules
  1-6 exactly: builds the document as the host's own head (Content-Security-Policy meta with a fresh nonce, the
  injected script with that nonce and a fresh token) then the report bytes untouched; accepts one `ready` from
  the frame's window with the current token and exactly one port, then forgets the token; everything else over
  that port only; a frame `load` it did not cause closes the port and ignores the frame until it loads the report
  again. Every message's shape checked; anything else ignored whole.
- **The note script is bundled from its one source file** (`dev-report-notes.js`, e.g. a Vite `?raw` import).
  Never a copy, never a second file.
- **The host holds state** (handoff ruling 3): the last `state-changed` state, per report and version, in local
  storage, restored with `restore` after every `ready` - so it survives a frame reload and an app reload. When a
  NEW version of the report is published, seed the new version's state from the previous version's so the
  half-typed text, queued items and scroll position carry across the republish.
- **Send**: on `send`, POST the items; answer every item with a `status` over the port, using the Gateway's
  `status` and `statusLabel` VERBATIM. If the request itself fails (network, 5xx, no response), the Gateway ruled
  nothing: answer each item `refused` with the transport error as the label, so it stays queued and can be sent
  again. Never invent a Gateway state.
- **Live refresh** (handoff ruling 4): there is no push for dev reports, so poll the detail route while a report is
  on screen - use `src/polling/useVisiblePolling.ts`, every 5 seconds is modest - and push `status` for every item
  and `reply` for every reply the Gateway returns. When `report.version` rises above the loaded version, fetch the
  new html and reload the frame in place (new nonce, new token), restoring state.
- **React components, shared by both shells**: the report list for one session (title, status, version, updated
  time, open items - all as the Gateway sends them), the viewer (the frame plus a slot for the conversation), and
  the conversation panel (queued items from host state, sent items with the Gateway's labels, replies). Mount no
  routing or page chrome in these; shells supply that.
- **The tray looks like the app** (handoff ruling 6). CSS variables from the app do not cross into the frame, and
  a report's own CSS can set variables too, so do not rely on inheritance: pass the theme on the injected script
  element (for example a `data-dev-report-theme` attribute) and have `dev-report-notes.js` bake it into its
  shadow-root stylesheet, with the app's dark palette and type as the default. Colours and type from
  `docs/VisualStyle.md` and `apps/cockpit/src/styles.css`. Update CONTRACT.md for the new attribute in the same
  commit. Keep `devReportNotes.test.ts` and the browser proof in `browser-tests/dev-report-notes-proof` passing.

**2. The Cockpit (`apps/cockpit`)**: a **Reports** tab beside Terminal / Chat / Voice / Source Control in
`src/sessions/SessionDetail.tsx`, showing the list; opening a report shows the viewer with the conversation beside
it. Frame only - no logic that is not in client-core.

**3. The phone (`apps/mobile`)**: a Reports entry on the session screen (add it to `ViewTabs.tsx`), routes
`/session/:sessionId/reports` (the list) and `/session/:sessionId/reports/:reportId` (the report full screen), and
the conversation as a bottom sheet over the report. Frame only.

**Ended sessions** (handoff ruling 5): the report stays readable; Send just shows the Gateway's refusal sentence,
which arrives as the item's `statusLabel`.

## Tests you write

- Unit tests (vitest) for the host: token accepted once; a wrong token, a second ready, a message on the window
  after ready, a port-less or two-port ready all refused; restore after ready; a load the host did not cause
  closes the port and later messages are ignored; state persists across a new host instance (app reload); a new
  version seeds from the old; send maps Gateway updates verbatim and a failed request answers `refused`.
- Component tests: the list and the conversation render Gateway status, labels and replies VERBATIM (use
  deliberately odd strings, so a client-authored label would fail).
- Run and report: `npm test` for client-core, cockpit and mobile (look at the root `package.json` for the
  workspace commands), their typecheck, and both apps' production build. Also `node run-proof.mjs` in the notes
  proof folder if you touched the script (see its README for `PLAYWRIGHT_PATH`).

The real-browser hostile-report test on the built apps and the end-to-end run on a local Gateway are a SEPARATE
Worker's job after you. Make them possible: stable `data-testid`s on the Reports tab, list rows, the frame, the
conversation panel and its Send state.

## Rules

- Plain English, ASCII only, no abbreviations in text you write. No attribution of any kind in commits.
- Enterprise logging style is for C#; in TypeScript follow the surrounding code.
- No fallbacks: if something the design needs does not exist, stop and tell the Manager.
- Foreground only. Do not start a Gateway or a Director - you do not need one.
- When done: everything committed and pushed on `mission/dev-reports-p3-viewer`, then ONE line to the Manager
  naming the final commit and the test counts.
