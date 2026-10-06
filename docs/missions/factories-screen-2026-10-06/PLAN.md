# Plan and decisions - Factories screen (Implementation Lead, 6 Oct 2026)

## What exists today (read on origin/main e8f673ab9 and the live Gateway)

- The Gateway knows factories only through activity rows (`factory record`) and triggers. It has no list of
  factories, no CEO, no seats, no goal, no computer.
- Factory definitions live on disk on SOREN_NORTH in mixed layouts (`cc-consult/ideas/warmforward-factory/agents/*.yaml`,
  `cc-consult/ideas/website-factory/factory/...`, `cc-clickfunnels`, ...). The Factory Manager is standardising them.
- Factory schedules exist on the Gateway (e.g. `cj_a721e6` "WarmForward Factory - Nora Hale - morning run": cron,
  time zone, `target.machine`, `action.repoPath`, `action.seed`, `lastFiredUtc`, `lastStatus`). Their `factory`
  field is null on every one.

## Decisions (mine, inside the mandate)

1. **A factory registry on the Gateway.** One row per factory: id, title, folder, computer, CEO seat, the goal text
   and the date it was approved, and its seats (id, name, role, brief file, the schedule ids that run it). Written by a
   new command `cc-devthrottle factory register --manifest <file>`. This is an INDEX, not the definitions (R20 stays
   out of scope): the briefs stay on disk; the registry says where they are. The design report itself lists
   "every factory registered" as needed.
2. **Manifests for today's ten factories** are written from the live schedules and the folders on disk, kept in this
   mission folder (`manifests/`), and registered on the owner's account. The Factory Manager can take them over in
   its standardisation; no factory repository is changed by this mission.
3. **Goal text** comes from the factory's GOAL.md when the manifest names one; the register command reads the file
   and sends its text. No GOAL.md -> the page says "No goal set yet".
4. **Goal number:** `cc-devthrottle factory goal-number post` (value, unit, date, link to how it was measured,
   posted by), and `factory goal-number show`. The newest post is what the page shows.
5. **Status, one of four words, folded on the Gateway:** FAILING (a run of one of its seats failed in the last 24
   hours - a `failed` activity row or a schedule whose last status is failed), NEEDS YOU (open waiting items),
   PAUSED (every schedule of its seats is disabled), RUNNING (otherwise). Worst first in that order.
6. **Seats tab** lists only registry seats, never activity-row names.
7. **Computer change ships read-only.** Moving a seat's schedule to another computer IS changing a factory's
   schedule, which the mission puts out of scope. The page shows the computer and a "change - coming" label that
   is visibly not a control. Said so in the report.
8. **Talk** is a Gateway endpoint the Cockpit calls; the Gateway starts a top-level session owned by the person who
   pressed it, on the seat's computer, in the seat's folder, named `<Factory> - <Agent> - talk with the owner`, with
   a seed that seats it as the agent under its scheduled run's rules and requires the activity line (outcome
   `talked`) and the memory note before it ends. A goal change in a talk is written to GOAL.md with the date and the
   talk's session id.

## Phases (each one pull request, separately reviewed, merged)

- **A - Gateway data:** registry + goal number (store, migration, endpoints, `factory register` and
  `factory goal-number` commands, tests).
- **B - Gateway views:** the new list, factory page and Seats folds and their endpoints (DTOs in Contracts).
- **C - Talk:** the Talk endpoint, the seed, the `talked` outcome, "Last talk with you" on the page.
- **D - Cockpit:** sidebar "Factories", the list, the factory page and its tabs, the Seats tab, Talk buttons,
  phone width; old routes redirect; "All factory agents" tab removed.
- **E - Live:** register the ten factories, deploy through the skill, QA the live screens, report.

A then B on one Developer; C and D start once B's DTOs are merged.

## Decisions added during the build

- **6 Oct, after the manifests:** FAILING counts ANY `failed` activity row the factory wrote in the last 24 hours
  (or a seat schedule whose last status is failed), not only a seat's run - ClickFunnels' Facebook Reader, Badge
  Runner and Sales Sender are runner scripts, not seats, but their failures are the factory's failures. PAUSED
  includes a factory with no enabled schedule at all (Tallyhand has none): nothing of it runs.
- **Factory ids** for factories that never wrote an activity row are taken from their folders (`warmforward`,
  `tallyhand`, `devthrottle`, `business-research`, `cc-factory`); see `manifests/README.md`. They become the ids
  Talk sessions and future runs use.
- **6 Oct, live QA:** the first live Talk (session e1e8fac9) opened correctly but in no factory. Root cause: the only
  Director online on SOREN_NORTH is v2.12.0 (running since 1 Oct; its launcher already holds 2.16.0), older than
  factory membership (v2.13.0), so it drops the factory from every create - scheduled factory runs too. Updating it
  restarts the owner's sessions, so it is the owner's action (raised to him). The Gateway will refuse a create that
  names a factory to a Director older than 2.13.0, with a sentence, instead of the factory vanishing silently.
- **6 Oct, live QA:** the Seats tab showed a run time in UTC beside a schedule in Toronto time; fixed in the same
  pull request as the refusal.
