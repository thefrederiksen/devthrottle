# Active goals and PRDs

Planning docs for the next chunks of CC Director work.  Anything an LLM agent should pick up cold lives here.

| Doc | Status | Hook |
|---|---|---|
| [PRD_CC_DIRECTOR.md](PRD_CC_DIRECTOR.md) | ACTIVE | The product vision: Session Supervisor + Gateway dashboard, agent orchestration platform.  Source of truth for the "why". |
| [GOAL_CC_DIRECTOR_SUPERVISOR.md](GOAL_CC_DIRECTOR_SUPERVISOR.md) | ACTIVE | Phased implementation plan derived from the PRD.  8 phases.  Phase 1 (voice transcript cleanup) is sized for one short session.  Each phase has a self-test gate and a "stop and report" protocol so an LLM can execute it autonomously. |
| [GOAL_VOICE_MANAGER.md](GOAL_VOICE_MANAGER.md) | SUPERSEDED | An earlier version that scoped just the chat-first Manager UI for talking to the private repo.  The Manager-chat surface it described has been folded into the session-view's Voice tab (already built today).  Anything still useful from this doc has been pulled into `GOAL_CC_DIRECTOR_SUPERVISOR.md`. |

## How to use these

- Read `PRD_CC_DIRECTOR.md` first if you want the why.
- Read `GOAL_CC_DIRECTOR_SUPERVISOR.md` if you are about to write code.  Pick up Phase 1; do not skip ahead.
- Ignore `GOAL_VOICE_MANAGER.md` unless you are doing historical research.

When a goal is finished or no longer applies, move it to `docs/goals/archive/` rather than deleting (so we have a record of what we shipped and why).
