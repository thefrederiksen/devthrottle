# Brief - Developer, Talk (phase C)

You are a Developer on the Factories screen mission. Read `MISSION.md`, `PLAN.md`, the design report and
`BRIEF-developer-gateway.md` (what phases A and B built - the factory registry and the views) in this folder first.
The Implementation Lead (session b496c54d) opened you and is who you report to. Work only in your own worktree,
cut from origin/main (phase A, the registry, is merged as c4f8a481c; phase B, the views, is being built in parallel by session 35ca0a83). Follow CLAUDE.md, docs/CodingStyle.md, docs/axi-standard.md.

## Your task: one pull request - the Talk button's Gateway side

1. **`POST /gateway/factory-agents/factories/{factory}/seats/{seat}/talk`** (behind FactoryAgentsGate, tenant
   scoped). It resolves the seat in the registry, finds the account's Director on the seat's computer, and starts a
   new session there exactly the way a person's New Session does (`/directors/{id}/sessions`,
   `NewSessionRequest`; reuse the code path, do not copy it):
   - **top-level and owned by the person who pressed it** - never a child of any session, no controller;
   - in the factory's folder (`repoPath` = registry folder) with `factory` set to the factory id, so the factory's
     rules and memory load as they do for its scheduled runs;
   - named `<Factory title> - <Seat name> - talk with the owner`;
   - with the seed below as its first prompt.
   Answers with the new session's id and the Cockpit href that opens it. Clear refusals (400/404/409 with a
   sentence) for: unknown factory or seat, no Director online on that computer, the switch off. No fallback to
   another computer.
2. **The seed** (a Gateway-owned template, unit tested): it tells the agent it is `<Seat name>`, `<role>` of
   `<Factory>`; that the owner is present and talking to it now (this is the owner's own session); to read its brief
   (registry brief file) and to follow **the same rules as its scheduled runs** - include the seat's schedule seed
   text (the first schedule in the registry) as "the rules of your scheduled runs", and say plainly that the
   unattended-run steps in it (renaming the session to a dated name, the morning email, `session done`) do not
   apply in a talk; to read the factory's goal, its memory (`cc-devthrottle factory memory`) and its recent
   activity (`cc-devthrottle factory activity --factory <id> -n 30`); to open by saying what happened since the
   owner last talked with it and what it needs from the owner. And before the talk ends it MUST:
   - record one activity row: `cc-devthrottle factory record` with outcome **`talked`**, the factory, its agent
     id, and one line of what was decided;
   - write what was decided into the factory's memory (`cc-devthrottle factory memory ...`).
   In a talk the owner and the agent may change anything about the factory, the goal included: a goal change is
   written to the factory's GOAL.md with the date and this session's id, and re-registered
   (`cc-devthrottle factory register`) so the page shows it.
3. **The `talked` outcome**: add it to the activity outcomes (record accepts it, the fold words and tones it, it
   is not "waiting on you" and not a failure). The factory page's "Last talk with you" shows
   "Talked with you, <time> - <the line>" from the newest `talked` row, "None yet" without one. Phase B owns the
   page fold and folds `talked` rows; you add the outcome to the outcome list, the record command and the outcome
   word/tone. If B merges first, rebase and prove the page line end to end in a fold test.
4. Tests: the endpoint (refusals included, and a proof that the created session has no controller and carries the
   factory and the name), the seed template, the outcome.

## How you work

Same as the Gateway brief: the local gate (`.\scripts\test-local.ps1`, plus `-Parked` or the Gateway suites for
your area), commit `type(scope): description` with NO attribution, push, `gh pr create`, then
`cc-devthrottle message send b496c54d "<one line>"`. The Lead arranges the review and merges. Never deploy, never
start a real talk session on the live Gateway, never touch a factory's schedules.
