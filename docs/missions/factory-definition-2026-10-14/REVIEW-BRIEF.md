# Review brief - Factory Definition mission document

You are a Reviewer. You read; you never build and never fix. You may return nothing: a finding must prove the harm
(what breaks, and why it must change). State your scope - what you read, what you ran, what you could not reach.

## What to review

`docs/missions/factory-definition-2026-10-14/MISSION.md` and its two diagrams in the same folder
(`diagram-where-it-is-saved.svg`, `diagram-today-and-after.svg`). It is a DESIGN, not code. Nothing is built yet.

## What to check, against origin/main code in this worktree (it is cut from origin/main)

1. **Claims about existing code are true.** In particular: `FactoryBriefVersionEntity` (append-only, key as
   concurrency control); `FactoryMemoryFiles` and the `SessionManager` download-before-start path;
   `InheritedSessionEnvironment`; `NewSessionRequest.Factory` (children inherit the factory), `.Agent`,
   `.PooledWorktree`; `CcWorktreesPool`; `CronJobEntity` / `TriggerEntity` having no agent or model; the registry.
2. **The design does not contradict** the approved rulings it cites: R7, R9, R13, R20 and section 5.2 of
   `D:/ReposFred/devthrottle_internal/docs/missions/teams-and-agents-2026-09-19/DESIGN.md`, and Factory Seats
   (`docs/missions/factory-seats-2026-10-13/MISSION.md`, especially P3 and P5).
3. **Gaps that would bite the build:** anything in the goal that no phase delivers; any phase whose QA cannot prove
   its goal line; the one-transaction publish across registry, schedules and triggers (are they in one database and
   one context?); the "one source of truth" refusal of direct schedule edits; secrets checked at start; child
   sessions getting the rules; the trigger check files; rules at launch for Claude Code (`--settings`) and Codex.
4. **The check commands in section 7 exist and would run** (project paths, filters, workspace names).
5. **The diagrams say the same as the text** and nothing on them is wrong.

## Where to write the answer

Write your review into `docs/missions/factory-definition-2026-10-14/REVIEW-1.md` in this worktree: scope, then each
finding with its evidence (file and line) and the harm, then anything you checked and found sound. Do NOT edit any
other file, do NOT commit, do NOT push, do NOT run `session done`. When finished, say "REVIEW WRITTEN" on screen and
stop.
