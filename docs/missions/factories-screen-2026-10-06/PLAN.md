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

## Round 2 - the owner's feedback on the live list (6 Oct, 23:39; MANDATE-round-2-owner-feedback.md)

Two tracks, each its own worktree, pull request and review:

- **Track R2-A, the status explains itself** (mandate items 1, 2, 5, 6): a one-line reason under every
  non-RUNNING status, folded on the Gateway, opening the items on the factory page; FAILING clears when a later
  successful row exists for the same seat and subject, or when the owner marks the failure handled; PAUSED says
  "Nothing scheduled" when a factory has no schedule at all; the factory's head may carry any title (the registry's
  `ceoSeat` is the head; the page shows that seat's role, e.g. "CFO Ruth Calder").
- **Track R2-B, act on it** (items 3, 4): each waiting item with its text, time, seat, evidence link and Handled;
  an owner-only bulk "mark everything older than 7 days as handled" with a confirm that states the count, recorded
  as the owner's act; owner-only Archive factory (confirm lists exactly what happens: off the list, its own named
  schedules disabled, history kept), recorded; a Show archived view with Restore.
- **Data, by the Lead after deploy:** re-register Center Consulting with Ruth Calder as head; archive Tallyhand
  (closed 4 Oct, moved into mindzie AI Reports; registered by mistake) as the archive action's first use. The bulk
  clear is NOT used until the owner says so in the report.

### Round 2 - the rule for when FAILING clears (track R2-A, checked on the live record 7 Oct)

**What "subject" and "successful" mean on real rows.** Read with `cc-devthrottle factory activity --factory
website-business --json`: a seat's rows name the business or the thing they are about in `subject` (the Sender's
four "keep.page (failed): https://centerconsulting.com/websites/keep/<slug> answers 404 after 20 min" rows at
12:02 UTC carry subjects "All Types Fence & Deck", "Sooner Excavation LLC", "Reynolds Pumping and Septic Services",
"Alderson and Sons Tree Service"); a trigger's check rows carry the trigger's name ("website-new-mail"). The same
seat then wrote "keep.recorded (done)" for each of the same four subjects at 12:11, and the Front Desk trigger's
failed checks of 5 Oct were followed by "nothing to do" checks of the same subject. Steps a seat only intends are
`started` ("draft.outreach (intended)"), never success.

**The rule.** A `failed` row of a factory, in the last 24 hours, counts toward FAILING until it is over. It is over when
EITHER

- **the owner marks it handled**: "Handled" on the failures card writes a NEW row (outcome `done`, `correctsId` = the
  failed row, actor = the owner) through `POST /gateway/factories/{factory}/failures/{id}/handled` - the same
  correction mechanism as an escalation's "I have handled it"; the failed row is never edited; OR
- **a later successful row exists from the same seat about the same subject**: same factory; same factory agent
  (trimmed, ignoring case); same subject (trimmed, ignoring case - a row with no subject matches only another row
  with no subject); outcome `done` or `nothing-to-do`; a later time; and not itself a correction of another row (a
  "Marked handled" row says an older row is over, not that the work succeeded).

`started`, `escalated` and `asked` rows never clear a failure, nor does another seat's success or a success about
another subject. A schedule whose last firing could not start its run is not a row: it cannot be marked handled and
clears when the schedule's next firing starts its run. Tested in `FactoriesScreenRound2Tests` (both clearing paths,
a partial clear, and six rows that must NOT clear a failure) and, on a real host, in `FactoryRegistryRouteTests`.

**The other R2-A rulings.** The status line under each word is folded on the Gateway (`StatusLine`, `StatusHref`,
`WaitingHref`); RUNNING has none. FAILING links to `#failing` on the factory page, NEEDS YOU and the waiting count to
`#waiting`, PAUSED to the Seats tab. PAUSED says "Nothing scheduled" when no seat names a schedule the Gateway has and
no trigger runs one; otherwise it counts ("2 schedules switched off", "1 trigger paused", "1 named schedule no longer
exists"). The head: the manifest key `ceoSeat` is KEPT (registered data depends on it) and now means "the factory's
head, whatever its title"; the page says the seat's own role and name ("CFO Ruth Calder"), the button its name ("Talk
to Ruth Calder"), and a factory with none says "No head named". Center Consulting still needs re-registering with
`"ceoSeat": "cfo"` (the Lead's data step).
