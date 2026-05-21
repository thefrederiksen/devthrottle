# Merged Session View — Plan (Phase 5.2)

**Status:** IN PROGRESS
**Date:** 2026-05-21
**Predecessor:** [SUPERVISOR_ASK_AND_LOG_PLAN.md](SUPERVISOR_ASK_AND_LOG_PLAN.md) (Phase 5 shipped)

## Goal

Original Phase 4c promised a merged "Session View" that combines the supervisor banner, the agent widget feed (what the agent has been doing), and voice TTS — replacing Raw as the default working view. Phase 5 shipped the supervisor banner + ask + persistent log. Phase 5.2 closes the loop by folding the agent widget feed into the same view, on both desktop and web.

Voice is web-only and stays its own tab — its walkie-talkie semantics are distinct.

## Slices

### 5.2.A — Web: agent widget feed inside the Session tab
- Extend `src/CcDirector.ControlApi/Web/session-view.html`: add an `#agentInSession` section between the last-turn block and the supervisor log. Reuse the existing `refreshAgent()` rendering by switching its target container when the active tab is Session.
- Mark the standalone Agent tab as `Agent (legacy)`. Keep it for one cycle; remove in Phase 5.3 if the merge is solid.

### 5.2.B — Desktop: rename Supervisor → Session, embed CleanView
- `MainWindow.axaml`: rename `SupervisorTabButton.Content` to "Session"; rename internal panel name `SupervisorPanel` → `SessionPanel`; the tab key in `SwitchLeftTab` becomes `"Session"`.
- `SupervisorView.axaml`: top of the DockPanel gets the agent widget feed (a slot the parent fills with `CleanView`). Banner moves below the widget feed OR stays at top — design choice; v1 keeps banner at top for at-a-glance scan.
- Make Session the default tab on session open (replaces Terminal as default in `SelectSession`/`SwitchLeftTab` bootstrapping).
- Agent tab gets `(legacy)` label.

### 5.2.C — Build + smoke
Build slot 6 via `local-build-avalonia.ps1 -Slot 6`, register the scheduled task, launch via `cc-director-launch`, create a session, click the Session tab, verify:
- Banner shows current color
- Agent widgets render as turns happen (or load from JSONL replay)
- Ask input still works
- Voice tab on web still works

### 5.2.D — Commit + push
Single commit straight to main per project rules.

## Out of scope

- Voice tab consolidation. Voice has its own input modality (record/send) and lives best as its own tab.
- Removing the Agent legacy tab. Keep for one cycle.
- Custom timeline / chronological interleaving of asks + widgets. v1 keeps them as separate sections.
