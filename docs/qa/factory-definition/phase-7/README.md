# Factory Definition, phase 7 (first half): the DevThrottle factory as a folder

Mission: `docs/missions/factory-definition-2026-10-14/MISSION.md`, goals 13 and 14, section 7 row 7, D7.

The folder itself is NOT in this repository: the repository is public and the factory's briefs and documents are
private. It lives with the mission's private briefs. This note carries only counts, seat names, the choices made and
the proofs.

## What was written

The DevThrottle factory as a definition folder, from today's files:

- the factory folder at `devthrottle_internal` origin/main (`factory/`, commit `4ba9adf6`, the newest on
  2026-10-15; the separate clone on disk was four days behind it, so it was not used),
- the factory's registry entry and its 20 schedules, as the hosted Gateway answered on 2026-10-15.

| Part | Count |
|---|---|
| Seats | 19 (20 registered; see choice 3) |
| Starts | 20, one per live schedule |
| Grants | 8 (G1 to G8) |
| Rule sets | 2: `factory` (216 deny rules), `outreach` (175 deny rules) |
| Files | 8: AUTONOMY.md, BOSS-BRIEF.md, EXPANSION.md, FACTORY.md, GOAL.md, MAP.html, README.md, outreach/outreach_send.py |
| Total | 31 files, 208,333 bytes |

Seats: blog-writer, boss, code-signing-check, creator-outreach, defect-checker, defects, dictionary-digest,
health-watch, mail-desk, merge-review, motivation-videos, onboarding, outreach, reddit-manager, reminders,
sales-desk, turn-log-labelling, usage-watch, youtube-channel.

## Choices, where today's parts disagree

Each keeps today's behaviour; nothing is written that today's files do not say.

1. **Times.** Every live schedule of every factory holds the cron `0 0 31 2 *` (31 February: never fires), where the
   owner parked them (D7). Each start takes its real time from that schedule's own run history, and each agrees with
   FACTORY.md, EXPANSION.md's schedule table and the schedule's name (for example Nightly Defects 01:30, Health Watch
   07:00 and 19:00, Mail Desk 08:00, 12:30 and 16:30). Reddit Manager (a random-time schedule) and Reminders (a
   one-off, 18 October) cannot be a repeating cron and keep `0 0 31 2 *`.
2. **Switches and prompts.** Each start's prompt is its schedule's prompt verbatim. A publish pairs each start with
   the live schedule it came from and keeps that schedule's id and its enabled or paused switch: a publish never
   turns a schedule on or off. 8 enabled, 12 paused, exactly as today.
3. **Two Boss seats.** `ceo` and `boss` are both registered with the role Boss. `agents/ceo.yaml` was removed and
   `agents/boss.yaml` added by the rename of 11 October, so the `ceo` seat's schedule names a file that no longer
   exists. The definition has one seat, `boss`, with the Boss brief and the `ceo` seat's Monday 08:30 start (prompt
   verbatim). A publish therefore removes seat `ceo` and replaces its one schedule with a new, enabled `boss` schedule
   at the same time.
4. **Briefs** are each seat's `agents/<seat>.yaml` file, whole and unchanged.
5. **Agent and model.** `claude-code` is ClaudeCode; Defect Checker's "codex (first)" is Codex. "automatic" is the
   machine's default model, so no model is written.
6. **Rules.** A seat takes the rule set of the folder it starts in today: the factory folder's `.claude/settings.json`
   for nine seats, the outreach folder's own for Outreach, none for seats that start in `devthrottle` or
   `devthrottle_internal`.
7. **Repository**, by the 5.5 resolve rule on the folder each schedule starts in: `devthrottle-factory` (the clone,
   nine seats), `devthrottle_internal` (seven), `devthrottle` (three). Defect Checker has no schedule; its brief says
   it works in a worktree of `thefrederiksen/devthrottle`, so `devthrottle`.
8. **Grants** G1 to G8 from AUTONOMY.md, each held by the seats whose brief names it. G10 is held only by the
   Marketing brief, which is not a registered seat, so neither is in the definition.
9. **No goal text** (the registry has none); GOAL.md is a file. **No secrets, no skills** declared: today's files
   declare none, and a declared secret would refuse a start.

## Proof, local, before the hosted Gateway saw it

There is no offline check command on main, so the check is an import into a throwaway Gateway built from this tree's
main (`b4176aabf`), on its own port and empty storage root, with a person enrolled, seeded first with today's registry
entry and the 20 schedules (each at `0 0 31 2 *`, each switched as live):

| Step | Result |
|---|---|
| Import (the definition check, exactly as a save) | exit 0: "version 1 of the definition of devthrottle is a draft (31 files, 208333 bytes)". 20 notices, no error: no Director connected to confirm the machine and the 19 repositories. |
| What changes | "First publish: 9 factory settings set, 19 seats added, 20 starts created, rules tightened on factory, outreach" and the nine seats that take them; 8 grants and 8 files added. Plan: 17 schedules moved from 31 February to their real time, 2 changed (name and folder only), 1 new (boss), 1 deleted (ceo's). |
| Export of version 1 against the folder | 31 files identical, 0 differ (SHA-256 of every file). |
| Publish on the throwaway | exit 0. Switches kept: 20 of 20. Schedule ids kept: 19 of 20 (the 20th is choice 3). |

## Every start's time, its switch and its source

What a publish of this draft turns on is exactly the rows marked "on": it moves each schedule from 31 February to the
time below and never changes a switch. All times are America/Toronto except Dictionary Digest (America/New_York).

| Seat | Time | Switch | Source |
|---|---|---|---|
| boss (from seat ceo) | Mondays 08:30 | on (new schedule) | run history: fired Mon 12 Oct 08:30; name "weekly CEO memo" |
| defects | daily 01:30 | on | run history 8-12 Oct; name; FACTORY.md |
| health-watch | daily 07:00 and 19:00 | on | run history 8-12 Oct; name; FACTORY.md |
| usage-watch | daily 07:30 | on | run history 8-12 Oct; FACTORY.md |
| mail-desk | daily 08:00 | on | run history 8-12 Oct; name; FACTORY.md; EXPANSION.md |
| code-signing-check | daily 09:00 | on | run history 6-12 Oct; FACTORY.md |
| onboarding | daily 09:30 | on | run history 6-12 Oct; FACTORY.md |
| reminders | 31 February (never) | on | a one-off for 18 Oct in its brief; cannot be a repeating time |
| mail-desk | daily 12:30 | paused | run history 8-11 Oct; name; EXPANSION.md |
| mail-desk | daily 16:30 | paused | run history 7-10 Oct; name; EXPANSION.md |
| merge-review | daily 05:00 | paused | run history 8-11 Oct; name |
| outreach | weekdays 08:15 | paused | run history 7-9 Oct; name "weekdays" |
| creator-outreach | Wednesdays 08:00 | paused | run history 16 Sep to 7 Oct; name |
| dictionary-digest | daily 09:00 | paused | run history, 50 runs to 11 Oct |
| youtube-channel | daily 06:00 | paused | run history, 25 runs to 11 Oct |
| turn-log-labelling | daily 07:20 | paused | run history, 27 runs to 11 Oct |
| blog-writer | Sundays 09:00 | paused | run history 13 Sep to 11 Oct |
| motivation-videos | Sundays 08:00 | paused | run history 6 Sep to 11 Oct |
| sales-desk | Fridays 10:00 | paused | run history 9 Oct; name |
| reddit-manager | 31 February (never) | paused | a random-time schedule; cannot be a cron |

## Step 4: the owner imports, not this session

The hosted import was tried and refused, as the Gateway words it: "this definition belongs to factory 'devthrottle',
so only a person or a session of 'devthrottle' may change or run it". By the Delivery Lead's ruling the owner imports
it as a person: a zip of the folder for the factory page's Definition tab, Import. The zip holds the 31 files at
their folder paths with `factory.yaml` at its root; every entry was compared with the folder, 31 of 31 identical.
After his import he sees the draft and its "what changes" against today in the same tab, and he publishes. The draft
version, its "what changes" sentence and an export round trip on the hosted Gateway are recorded after his import.

## What the second half needs

Version 1 is faithful to today, so it still reads the separate clone: nine seats name its repository, and the
prompts and briefs of those seats update and read it. The second half writes a version 2 that changes only those
lines (the repository to `devthrottle_internal`, the brief and documents read from the run kit), the owner publishes
it, and the day is proven from each run's record: one run per due fire, each with brief version 2 of kind
"definition" and a session started from definition version 2, with the clone renamed away for the whole day.
