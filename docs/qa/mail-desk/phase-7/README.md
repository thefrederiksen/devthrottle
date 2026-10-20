# Mail Desk phase 7: the screenshots of the Mail Desk page

Contract: `docs/qa/mail-desk/phase-7/CONTRACT.md`, section 4. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md`
(issue #4213).

**The final pictures are not taken yet.** They wait for Mail Desk phase 3 to be merged into this phase. Until then the
scripts below have been run as dry runs only, on a local branch that merges `origin/mail-desk-phase-3` into this one.
This file gains the pictures and what each one shows when they are taken.

## How the pictures are made

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

## The scenarios

One Gateway and one account, `email@devthrottle.com`, in this order:

| Scenario | What is on the Gateway | What the page must show |
|---|---|---|
| `empty` | The Mail Desk switched on, no mailbox | The Gateway's empty sentence; the entry in the rail and the menu |
| `unpublished` | Three mailboxes; the mail-desk factory still the draft the switch offered | Three cards, each "Not known yet - no check has read the mailbox" and "Not checked yet - the mail-desk factory is not published" |
| `on` | The factory published, one screening run's last check, its kept checks, today's decisions | Each card its own signed-in state beside the account's "Checked <time>: ..." |
| `off` | The switch off again | "Page not found" on both shells; no entry in the rail or the menu |

The three mailboxes:

| Mailbox | Machines | Mode | After the screening run |
|---|---|---|---|
| `email@devthrottle.com` | devlinux | live (on this throwaway Gateway only) | signed in, 7 emails decided today |
| `desk@example.com` | devlinux, qa-north | observe | NOT signed in: its check failed with "desk@example.com is not signed in on devlinux: run cc-outlook -a desk-example auth" |
| `letters@example.org` | qa-north, qa-south, devlinux | observe | signed in, 1 email decided today |

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

## Last check is per account, by design

Every card shows the same "Last check", taken from the account's Mail Desk start. "Signed in" is the mailbox's own.
That is deliberate (the phase 7 Tech Lead's ruling, 19 October 2026):

- **One run of the Mail Desk start checks every mailbox.** So the start's last check is each mailbox's last check;
  there is no per-mailbox time that could differ from it.
- **Phase 3's kept checks cannot say when a mailbox was last checked.** They hold only failures and the first good
  check after one. A good check after a good check writes nothing.
- **A kept check with no published factory cannot happen.** An early dry run seeded kept checks with no factory
  published. Its cards read "Signed in - the last check read the mailbox" beside "Not checked yet - the mail-desk
  factory is not published" - a contradiction the seed made, not one the product can. The seed now publishes the
  factory first, and the `unpublished` scenario shows the honest state before any check: neither line knows anything
  yet.

## What the dry runs found about the tools

- Playwright's slimmed-down headless browser crashed on the phone's session list at every width on devlinux. The full
  Chromium build, also headless, does not, so `shots.cjs` uses it. Neither build crashed on the Mail Desk page.
- The Cockpit scrolls inside its own pane, so a full-page picture stopped at the window's height and cut off the third
  card. `shots.cjs` makes the window as tall as the scrolled content first.
