# Mail Desk phase 7: the Mail Desk page

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 7, decision D21, goal 8. Contract:
[`CONTRACT.md`](CONTRACT.md). Written by the phase 7 Tech Lead on 20 October 2026, on `mail-desk-phase-7` rebased onto
origin/main `66658a098` (Mail Desk phases 3 and 5 in).

## What the phase built

- **One Mail Desk page in shared client code** (`packages/client-core/src/maildesk/`), mounted at `/mail-desk` by the
  Cockpit (inside its pane; a "Mail Desk" row in the rail's Work section right after Factories) and by the phone (a
  screen with Back, like Settings; a "Mail Desk" entry in the menu after Repos). It lists every active mailbox with its
  address, provider, mode, machines, signed-in state, last check and today's counts by decision. It has no control of
  any kind: it lists, it never edits (mission 5.11).
- **Every word and colour on it is the Gateway's** (repository rule 7). The channel fold
  (`src/CcDirector.Gateway/MailDesk/MailDeskChannelFold.cs`) gained, additively: `providerLabel`, `machinesLabel`,
  `modeTone`, a `tone` on `signedIn` and `lastCheck`, `today.label` and `today.counts`; and `GET
  /gateway/mail-desk/channels` gained `title`, `summary` and `emptyLabel`. No existing field changed, so the
  `cc-devthrottle maildesk` command's `--json` keeps its shape. A tone or label the fold does not know throws, never
  falls back to a colour.
- **Hidden with the switch off.** The page and its entry read the same route; its 404 (the Mail Desk gate) means off,
  and the page is then the shell's ordinary "Page not found" with no entry in the rail or the menu. The entry is drawn
  ONLY after a read that succeeded: any other failure (offline, a timeout, a 5xx, a broken answer) draws no entry,
  reports it and asks again, and the page itself, reached by its address, shows the error (corrected after the phase
  review, finding F1; `reviews/phase-review-answers.md`).

## The proof

| What | Result |
|---|---|
| Screenshots, desktop 1440 and phone 390, against a throwaway Gateway on devlinux with test data: switch on (a mailbox signed in with counts, one NOT signed in, two in observe mode, one in live mode on the throwaway only), on with the factory unpublished, on with no mailbox, and off | 16 pictures in [`shots/`](shots/), each read by the Tech Lead; what each proves is below |
| Client tests, `vitest --minWorkers=1 --maxWorkers=2` | client-core 2,297, Cockpit 1,334, phone 183 - all passed |
| `npm run typecheck` (every workspace) | clean |
| Mail Desk Gateway unit tests (`--filter FullyQualifiedName~CcDirector.Gateway.Tests.MailDesk`) | 536 passed, run by the Tech Lead on `a2cbfdc29` (540 with the wider `~MailDesk` filter) |
| `cc-devthrottle maildesk` command tests (`test_maildesk_ops.py` and both help-and-errors suites) | 1,001 passed, 1 skipped (the Developer's run on `a2cbfdc29`; the command's code is unchanged) |
| The repository gate, `scripts/test-local.ps1 -Parked`, on this branch (`a2cbfdc29`) AND on unchanged origin/main (`66658a098`), on devlinux | see below |

**The gate on Linux (handover ruling 8).** `-Parked` cannot be clean on Linux, so it ran twice on devlinux - on this
branch and on unchanged origin/main - and the failed tests were compared one by one from the result files. Both runs
end "NEVER RAN", for the same reasons on both: the Windows-only installer suite `cc-director-setup.Tests` runs no
test here, and `cc-director-setup-engine.Tests` aborts (after 0 tests on main, after 9 on the branch; this phase does
not touch the installer). Every other suite ran to the end. **The failed tests are identical: the same 107 tests fail on
main and on the branch, and no test fails on the branch that does not fail on main** (Launcher 24, Reclaim 56, Core 8,
Core unit 5, Gateway host tests 10, Gateway unit 3, Terminal 1 - Windows behaviour and the known Linux failures). The
Gateway unit suite ran 15,222 tests on the branch against 15,186 on main: the new tests, all passing. The `-Parked` run
contains every suite of the default run. **No Windows gate ran.** The gate runs no web and no Python tests; those are
the client and command rows above.

**Reviews** (a separate Reviewer seat for each piece of work, on the reviewer agent handover ruling 9 names), in [`reviews/`](reviews/):

| Work | Review | Findings and answers |
|---|---|---|
| The Gateway fold | [`gateway-fold-review.md`](reviews/gateway-fold-review.md) | F1 - the machines sentence claimed a failover order a channel's machine list does not decide: **accepted**; the contract was wrong and was corrected, the sentence now claims no order. F2 - every last-check outcome but "failed" showed green: **accepted**; every outcome is named and an unknown one throws. [`gateway-fold-answers.md`](reviews/gateway-fold-answers.md) |
| The page and both shells | [`page-review.md`](reviews/page-review.md) | No findings. One note: the contract named only the 404 as "off", while the client also reads a pre-phase-1 Gateway's web page as off (true: that Gateway has no Mail Desk). The contract now says so. |

**Rulings the Tech Lead made** (both in the contract or below): the machines sentence claims no order (review F1);
"last check" is per account by design, and the screenshot seed publishes the factory rather than showing kept checks
without one (below).

## What is NOT proven

- No real mailbox, no hosted Gateway, no Director: the pictures are a throwaway Gateway with rows written the way a
  screening run writes them.
- No light variant: neither shell has a light theme today.
- No Windows gate, and no browser but headless Chromium.
- The phase does not deploy; the page reaches the owner only with phase 6's deploy (D16, D21).

## The screenshots

Contract: `docs/qa/mail-desk/phase-7/CONTRACT.md`, section 4. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md`
(issue #4213).

Taken on devlinux on 19 October 2026 at 23:33 by the machine's clock (03:33 UTC on the 20th), from `origin/mail-desk-phase-7` at
`a2cbfdc29`. That commit is phase 7 rebased onto main with Mail Desk phase 3 in, on the branch `mail-desk-p7-shots`.
The pictures and what each screen showed are in [`shots/`](shots/).

### What each picture proves

The Cockpit is 1440 wide; the phone app is 390 wide, drawn at twice the pixels. `seen-<scenario>.json` holds what
each screen showed, read from the page itself. `answer-<scenario>.json` holds the Gateway's answer to
`GET /gateway/mail-desk/channels` at that moment.

| Picture | What it proves |
|---|---|
| [`desktop-empty.png`](shots/desktop-empty.png), [`phone-empty.png`](shots/phone-empty.png) | Switch on, no mailbox. The page shows the Gateway's title, summary and empty sentence verbatim. The Cockpit rail's Work section has "Mail Desk" directly after Factories, and it is the active row. |
| [`phone-drawer-empty.png`](shots/phone-drawer-empty.png), [`phone-drawer-unpublished.png`](shots/phone-drawer-unpublished.png), [`phone-drawer-on.png`](shots/phone-drawer-on.png) | Switch on. The phone's menu has "Mail Desk" after Repos. The three pictures are the same image (the same checksum): the menu does not change with the mailboxes. |
| [`desktop-unpublished.png`](shots/desktop-unpublished.png), [`phone-unpublished.png`](shots/phone-unpublished.png) | Three mailboxes, the mail-desk factory still a draft. Every card is drawn in the Gateway's order with every field. Nothing has checked a mailbox yet, and both lines say so: "Not known yet - no check has read the mailbox" and "Not checked yet - the mail-desk factory is not published", both neutral. Every decision count is listed, zeros included. Live mode is amber on `letters@example.org` only. |
| [`desktop-on.png`](shots/desktop-on.png), [`phone-on.png`](shots/phone-on.png) | The factory published and one screening run done. Each card shows its own signed-in state. A signed-in card's last check names the account's run: "The Mail Desk check last ran 2026-10-20 00:45: the mail it read was screened" (green). `email@devthrottle.com` is signed in (green), in observe mode, with 7 emails decided today: Route to a seat 2, Archive 3, Junk 1, Keep for you 1. `desk@example.com` is NOT signed in (red), with the check's own sentence naming the fix, and its last check says so too, in red: "The Mail Desk check last ran 2026-10-20 00:45. This mailbox has not been read since its check failed at 2026-10-20 00:45" - the card no longer says "Failing" in red beside a green "screened" (the ruling of 20 October 2026, contract section 2). `letters@example.org` is signed in and live, with 1 email decided today. The machines sentences read one, two and three machines. Every card is shown whole, and the third is not cut off. |
| [`desktop-off.png`](shots/desktop-off.png), [`phone-off.png`](shots/phone-off.png) | Switch off. `/mail-desk` is the shell's ordinary "Page not found", exactly as an unknown address shows it, and the Cockpit rail has no Mail Desk row. |
| [`phone-drawer-off.png`](shots/phone-drawer-off.png) | Switch off. The phone's menu has no Mail Desk entry: Repos is followed directly by the separator and Account. |

In no picture does the page have a button, a field or any other control (`controls: 0` in every `seen-*.json`). The
only addresses anywhere are `email@devthrottle.com` and the reserved example domains.

### Reproduce

On devlinux (.NET 10 at `~/.dotnet`, Node 22 at `~/.local/share/node/bin`), from the repository root of a checkout of
`origin/mail-desk-phase-7`:

```
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$HOME/.local/share/node/bin:$PATH
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
(cd apps/cockpit && npx vite build) && (cd apps/mobile && npx vite build)

# Once per machine: the web-site skill's tools (Playwright 1.60.0).
T=$HOME/.local/share/devthrottle/qa-tools; mkdir -p $T && cd $T && echo '{"name":"web-qa-tools","private":true}' > package.json \
  && npm install --no-audit --no-fund playwright@1.60.0 @axe-core/playwright@4.14.0 && cd -

PYTHONDONTWRITEBYTECODE=1 NODE_PATH=$HOME/.local/share/devthrottle/qa-tools/node_modules \
  python3 docs/qa/mail-desk/phase-7/capture.py <a rig folder outside the checkout> docs/qa/mail-desk/phase-7/shots
```

The times on the cards are the time of the run, so a new run differs from these pictures there and nowhere else. The
pictures may be taken at any hour: the page counts decisions since local midnight in the account's time zone (on the
throwaway Gateway, this machine's), so `seed.py` places the run and every decision between local midnight and now -
taken shortly after midnight, it squeezes their "minutes ago" into the time since midnight, keeping their order. The
pictures here were taken at 00:45, which is why both the run and the failed check read 2026-10-20 00:45.

### The tests behind the page

Run on the tree with the last-check and entry fixes (`5f2c4c47e`, branch `mail-desk-p7-lastcheck`):

| Command | Result |
|---|---|
| `npm run typecheck` at the repository root (client-core, cc-assistant, cockpit, mobile) | clean, exit 0 |
| `npx vitest run --minWorkers=1 --maxWorkers=2` in `packages/client-core` | 2,302 passed of 2,302 (182 files) |
| the same in `apps/cockpit` | 1,335 passed of 1,335 (126 files) |
| the same in `apps/mobile` | 186 passed of 186 (37 files) |
| `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk"` (with `DOTNET_USE_POLLING_FILE_WATCHER=1` and a scratch `LOCALAPPDATA`) | 549 passed of 549 |
| `python3 -m pytest tests/test_maildesk_ops.py` in `tools/cc-devthrottle` | 122 passed of 122 |

### What is NOT proven

- **No real mailbox.** Nothing here read or screened mail. The last check, the kept checks and the decisions are rows
  written the way a screening run writes them (below), not a screening run's own output.
- **No hosted Gateway.** Every picture is of a throwaway self-hosted Gateway on devlinux, built from this tree.
- **No light variant.** Neither shell has a light theme today; both define dark tokens only. The tones map to the
  shells' own tokens, so a light theme would carry them, but nothing here shows one.
- **Last check is per account, by design** (below). The pictures cannot show a mailbox's own last-check time, because
  there is no such time.
- **No other browser.** The pictures are headless Chromium only; no Safari, Firefox or real phone.

### How the pictures are made

Everything runs on devlinux against a **throwaway Gateway** built from this tree. Never the hosted Gateway, never a
real mailbox, never Docker.

| File | What it does |
|---|---|
| `capture.py` | Starts the Gateway (`CcDirector.Gateway.dll`, Debug) with the Cockpit and the phone app built from this tree, its own empty storage root and home under a rig folder outside the checkout, SQLite, a free local port, Tailscale off, authentication on and an administrator token made for the run. It enrolls one browser device, takes the four scenarios in order, and stops the Gateway whether the run passes or fails. |
| `seed.py` | Puts one scenario on that Gateway (below). Refuses any address but `127.0.0.1` or `localhost`. |
| `shots.cjs` | Takes the pictures with Playwright 1.60.0 (the web-site skill's tools): the Cockpit at 1440 wide, the phone app at 390 wide, and the phone's menu. Beside them it writes `seen-<scenario>.json`, what each screen showed: headings, cards, the rail's Work list, the menu's links. |

Run from the repository root:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
(cd apps/cockpit && npx vite build) && (cd apps/mobile && npx vite build)
NODE_PATH=<the web-site skill's qa-tools>/node_modules python3 docs/qa/mail-desk/phase-7/capture.py <rig folder> <out folder>
```

### The scenarios

One Gateway and one account, `email@devthrottle.com`, in this order:

| Scenario | What is on the Gateway | What the page must show |
|---|---|---|
| `empty` | The Mail Desk switched on, no mailbox | The Gateway's empty sentence; the entry in the rail and the menu |
| `unpublished` | Three mailboxes; the mail-desk factory still the draft the switch offered | Three cards, each "Not known yet - no check has read the mailbox" and "Not checked yet - the mail-desk factory is not published" |
| `on` | The factory published, one screening run's last check, its kept checks, today's decisions | Each card its own signed-in state; the last check names the account's run ("The Mail Desk check last ran <time>: ..."), and on the failing mailbox says it has not been read since its check failed, in red |
| `off` | The switch off again | "Page not found" on both shells; no entry in the rail or the menu |

The three mailboxes:

| Mailbox | Machines | Mode | After the screening run |
|---|---|---|---|
| `email@devthrottle.com` | devlinux | observe | signed in, 7 emails decided today |
| `desk@example.com` | devlinux, qa-north | observe | NOT signed in: its check failed with "desk@example.com is not signed in on devlinux: run cc-outlook -a desk-example auth" |
| `letters@example.org` | qa-north, qa-south, devlinux | live (on this throwaway Gateway only) | signed in, 1 email decided today |

Live mode is on an example address, never on `email@devthrottle.com`: in a picture the owner reads, that address shown
as live would read as the real mailbox running live, which the mission forbids in production.

How each part is written:

- **The account.** A self-hosted Gateway records no email for its one account (`local`), so its `tenants` row is
  written with `email@devthrottle.com`, as phase 1's proof did.
- **The switch.** The administrator route `POST /gateway/admin/mail-desk`, the one way it is ever switched. Switching
  on offers the built-in template as a draft of the account's `mail-desk` factory.
- **The mailboxes and live mode.** The page's own routes, `POST /gateway/mail-desk/channels` and `/channels/mode`, as a
  person.
- **The factory.** Published as a person through its own route, `POST /factory-definitions/mail-desk/publish`, which
  creates the Mail Desk start.
- **The last check, the kept checks and the decisions** are written straight into the throwaway database, because
  only a screening run writes them, and a screening run needs a real mailbox and a Director. They are written the way
  that run would leave them. The start's trigger gets the run's time, the outcome `screened`, and the run's summary as
  its reason, worded as `MailDeskService` words it. Each mailbox gets a kept check at that same time: a first good
  check for the two that read, and a failure for the one that did not.

### Last check is per account, by design

Every card's "Last check" is taken from the account's Mail Desk start, and its words say so: "The Mail Desk check last
ran <time>: ...". "Signed in" is the mailbox's own. That is deliberate (the phase 7 Tech Lead's ruling, 19 October 2026):

- **One run of the Mail Desk start checks every mailbox.** So the start's last check is each mailbox's last check;
  there is no per-mailbox time that could differ from it.
- **Phase 3's kept checks cannot say when a mailbox was last checked.** They hold only failures and the first good
  check after one. A good check after a good check writes nothing.
- **A kept check with no published factory cannot happen.** An early dry run seeded kept checks with no factory
  published. Its cards read "Signed in - the last check read the mailbox" beside "Not checked yet - the mail-desk
  factory is not published" - a contradiction the seed made, not one the product can. The seed now publishes the
  factory first, and the `unpublished` scenario shows the honest state before any check: neither line knows anything
  yet.
- **A failing mailbox's last check says it has not been read since** (ruling of 20 October 2026, contract section 2).
  The first pictures showed `desk@example.com` as "Failing" in red beside a green "Checked ...: the mail it read was
  screened" - two contradictory statements about one mailbox. Now, when a mailbox's `signedIn` is `failing`, its last
  check gives both times - the run's, and its own failed check's from phase 3's kept checks - and its tone is bad,
  whatever the run's outcome. The run's time and outcome are unchanged in the answer; only the words and the tone
  are the mailbox's. Before the run has a time (not published, or never checked) the label stays as it was.

### What the dry runs found about the tools

- Playwright's slimmed-down headless browser crashed on the phone's session list at every width on devlinux. The full
  Chromium build, also headless, does not, so `shots.cjs` uses it. Neither build crashed on the Mail Desk page.
- The Cockpit scrolls inside its own pane, so a full-page picture stopped at the window's height and cut off the third
  card. `shots.cjs` makes the window as tall as the scrolled content first.
