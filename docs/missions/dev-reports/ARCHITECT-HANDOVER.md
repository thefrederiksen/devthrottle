# Architect handover - Dev Reports mission (2026-09-17, 21:30)

The Architect seat was re-seated because `cc-devthrottle` stopped working inside it
("CC_DIRECTOR_API is not set"), which left it unable to list, read, start or stop sessions.

## Where the mission stands

- **Phases 1, 2 and 3 are MERGED to main and DEPLOYED** (the hosted Gateway was deployed twice today; the
  second deploy, at 14:01, carried the Reports view. Its measured external outage was 24.2 seconds against a
  10 second budget, so that deploy run is marked failed - the service came back and is healthy).
- **Phase 3b is IN FLIGHT** on `mission/dev-reports-p3b` (worktree `../devthrottle-dev-reports-p3b`), with worker
  branches `-apps`, `-gateway`, `-landing`, `-page`, `-proof`. Read `HANDOFF-phase-3b.md` - it is the mandate.
  It exists because the owner used the live Reports tab and found two conversations on one screen, a floating
  panel covering the report, and a second Send button that was disabled and read as dead.
- **Phase 4 (the Director pane) is PAUSED** on `mission/dev-reports-p4` with `HANDOFF-phase-4.md` written. It was
  paused so it would not inherit the phase 3b fix. Restart it after 3b merges.
- **Phase 5 (the tool, the skill, the global rule) has not started.** Note that `cc-dev-reports` is installed on the
  owner's machine from the `../devthrottle-dev-reports-p3` worktree; do not remove that worktree until phase 5 puts
  the tool in the installer.

## What the owner is owed, and it is the next thing

He asked for a QA report with proof, tested by the Architect and not by him: the fix working end to end on the
LIVE site - publish a report from a session, open the printed link, note a table cell, answer a question, Send,
show the session receiving exactly one prompt naming the cell and the option, and the agent's reply appearing.
Screenshots at phone and desktop width, before and after. He is unhappy with the quality so far; do not hand him
anything you have not driven yourself.

## Conditions to work around

- Fleet `message send` to child sessions fails with "unknown error" (issue #3009). Deliver instructions by seating
  a fresh session with them in its first prompt, and read terminals (`cc-devthrottle session buffer <id>`) instead
  of waiting for a session's message. A session that says it is Waiting may be finished, out of usage, or stuck.
- Reviewer families: Codex is out of usage until 22 September, Grok's free tier is exhausted, Gemini has no key,
  Copilot silently falls back to a light model. Pi with `--args "--provider deepinfra-mindzie --model zai-org/GLM-5.3"`
  has worked for two inspections.
- The inspection law still holds: a different agent family inspects before anything reaches main, and only the
  Architect merges.
