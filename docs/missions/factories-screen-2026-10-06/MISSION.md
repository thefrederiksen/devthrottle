# Mission: the Factories screen (2026-10-06)

**Owner:** soren@centerconsulting.com. **Started by:** the Factory Manager session (708e71ec).
**Build issue:** thefrederiksen/devthrottle#3585. **Design (settled, every question answered):** dev report
dcdcdec9 version 4, copied here as `DESIGN-report-dcdcdec9-v4.html`. The owner: "This is now ready ... use this
as the mission document."

## Why

The Cockpit's "Factory Agents" screen opens on one factory and lists its factory agents one by one (14 for
ClickFunnels, every row "IDLE", "No trigger names it"). The owner: "having all of the employees in the factory
is not relevant when we look at an overview of all our factories." And every morning he walks the factories one
at a time with the Floor Manager and talks to each factory's CEO (ruling on devthrottle_internal#2177, 6 Oct),
so he needs a way to talk to any agent from the screen: "there should be a way to click on any of the agents and
start a session as that agent and talk to that agent."

## The goal

The owner opens **Factories** in the Cockpit on the live Gateway, sees every factory as one simple row, opens a
factory, sees its goal and goal number and its seats, and presses **Talk** on the CEO or any seat and lands in a
top-level session seated as that agent. Proven by a QA report with screenshots of the live screens.

## What to build (the owner's rulings, from the design report)

1. **Sidebar:** "Factory Agents" becomes **Factories**. Routes may keep working under the old address (redirect).
2. **The list** (Factories tab): one row per factory with **name, status, waiting on you** - nothing more
   (owner chose "Fewer"). Plus a **"Talk to <CEO name>"** button per row; "No CEO" when the factory has none.
   Sorted worst first: FAILING, NEEDS YOU, PAUSED, RUNNING. Status is exactly one of those four words.
   Tabs at top level: Factories, Activity, Reports. **The "All factory agents" tab is removed.**
3. **A factory's page:** header with name, status, CEO, seat count, the computer it runs on, and
   "Talk to <CEO>". Overview: the **goal** (marked "only you change the goal"), the **goal number** as last
   posted by the CEO (with when and a link to how it is measured; "no number" when none), what is waiting on
   the owner, the CEO's latest reports, and "Last talk with you". Tabs: Overview, **Seats (n)**, Activity,
   Reports, Memory (the existing memory tab), Documents (may be a placeholder until definitions live on the
   Gateway - say so on the tab, do not fake content).
4. **The Seats tab:** every seat of that factory: name, role, when it runs, last run and how it ended, computer,
   and a **Talk** button. Rows that are not seats (activity rows from owner sessions, e.g. "Owner Session",
   "Certifier 19") are not listed.
5. **Talk** (both buttons): opens a **new top-level session owned by the owner** - never a child of another
   session - on that agent's computer, in its factory's start folder, seated as that agent (its brief, the
   factory's goal and memory, its recent runs and reports), named `<Factory> - <Agent> - talk with the owner`.
   - It runs under **the same rules as the agent's scheduled runs** (owner's answer).
   - Before it ends it writes **a line in the factory's activity record** and **what was decided into the
     factory's memory** (owner's answer); the factory page then shows "Talked with you, <time>".
   - In a talk the owner and the CEO may change anything about the factory, **the goal included** - the owner
     is present to approve it; a goal change records the date and the talk session id (owner, 6 Oct).
6. **The goal number:** one command a CEO runs on every run to post it (the number, its unit, the date, a link
   to how it was measured), shown on the factory's page. Name it in the terminology of the existing
   `cc-devthrottle factory` commands.
7. **Computer per factory and per seat** (#3585): shown and changeable on the factory page and Seats tab. If
   moving schedules between computers turns out to be larger than this mission, ship it read-only with the
   change control marked as coming, and say so in the report - never a control that does nothing silently.

## What is NOT in scope

- Writing GOAL.md files for the factories (the Factory Manager's standardisation work does that). Where a
  factory has no goal yet, the page says "No goal set yet".
- Moving factory definitions onto the Gateway (ruling R20, epic #2177) - Documents tab may be a placeholder.
- Changing any factory's schedules, briefs or grants.

## Rules that bind this mission

- Trunk development: one worktree (this one, `D:/ReposFred/_wt/factories-screen`, branch
  `factories-screen/cockpit`, or further worktrees off origin/main per workstream). Done = merged to main.
- Every pull request has a **separate review** before merge: Codex first; if Codex is out (usage limit,
  update prompt), a Claude Code session on the Fable model. Never Pi, Grok or Gemini. Never name the reviewer's
  model in GitHub.
- **No attribution anywhere**: no Co-Authored-By, no "Generated with", no assistant name.
- **Deploying the Gateway/Cockpit is allowed for this mission** (owner, 6 Oct: "you're allowed to deploy to the
  gateway along the way so you can test it live") - **only through the `deploy-hosted-gateway` skill**. Never a
  hand-rolled `az` command. A deploy is an in-place restart with about a minute of outage, accepted.
- The factory area is behind a per-account switch (`factoryAgents.enabled`, FactoryAreaGate). It is ON for the
  owner's account; do not change it for anyone else.
- Foreground only. No background agents.
- Never contact a user. Never change a factory's schedule while testing Talk; a test talk session is closed
  when the test is done.

## Done means

1. Every item above merged to main (or, for item 7's change control, explicitly marked as coming), tests green.
2. Deployed to the hosted Gateway through the skill, and the deployed commit verified.
3. A **QA report** to the owner (a dev report, `cc-dev-reports open`), walking the LIVE screens with
   screenshots: the list, a factory page, the Seats tab, the phone width, a Talk session opened from the CEO
   button and from a seat, the activity line and memory note it left behind, and a goal number posted by the
   command and shown on the page. It states plainly anything not proved.
