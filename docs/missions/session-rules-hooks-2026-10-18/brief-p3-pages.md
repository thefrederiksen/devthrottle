# Brief - phase 3 Developer B: the Rules pages in client-core, the Cockpit and the phone

You are "Session Rules - Developer - phase 3 pages". One task. You report to the phase 3 Tech Lead (the
session that opened you), never to the owner.

Worktree: /home/soren/ReposFred/devthrottle-session-rules-p3-pages, branch
mission/session-rules-hooks-p3-pages (cut from the phase branch mission/session-rules-hooks-p3). Work only there.

Read first: `phase-3-plan.md` in this folder (rulings T1-T10, wire P1-P3 - build to them EXACTLY; the
Gateway routes are being built at the same time by another Developer, so you work against the contract
with test fakes). Then CLAUDE.md rules 1, 7 and 8, `docs/VisualStyle.md`, `docs/CockpitVisualStyle.md`.
Model the shape on the Workflows and Skills pages (`apps/cockpit/src/workflows/`,
`packages/client-core/src/workflows/`, `packages/client-core/src/skills/`) and on the Settings shape
(`packages/client-core/src/settings/` - one component, two shells).

Build:
- `packages/client-core/src/rules/`: a client for P1-P3 (sending `tz` from
  `Intl.DateTimeFormat().resolvedOptions().timeZone`), the Rules list with the six tabs, the rule page
  (fields list, firing record, changes), and the preview (machine, repo, optional command; suggestions
  offered; results with overrides and the decision). Shared CSS. No component decides what a state means
  (T1): render labels and map `tone` to a style token, nothing else.
- Cockpit: a "Rules" rail item in NAV_SETUP directly after Workflows, its icon in NavIcon, routes `/rules`,
  `/rules/:id`, and the preview (a route or a panel on `/rules`, your call, stated in your report).
- Phone: routes for the list and the rule page, read-only, and an entry point where the phone reaches its
  other setup pages. Re-tune layout for touch the way settings does; content stays shared.
- States on every view: loading at once, the Gateway's error text (T9), and each empty text from the Gateway.

Tests: vitest for client-core, cockpit and mobile covering list/tabs/empty/error/loading, the rule page with
and without firings and with an override, the preview with an override and a decision, and the rail order
(Rules right after Workflows). Run the type check of each package. Node is at ~/.local/share/node/bin; vitest
needs --minWorkers=1 --maxWorkers=2.

House rules: CLAUDE.md, plain English, no abbreviations in UI text or comments. Commit with
`git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com`, no assistant attribution anywhere. Push
your branch. No sub-agents. Do not merge, do not open a pull request. Screenshots against a real Gateway are
seat C's job after merge, not yours - but run the Cockpit dev server against fakes once and look at it.

When done: `cc-devthrottle session report` (or message the Tech Lead) with ONE line: your commit and the test
counts for each package.
