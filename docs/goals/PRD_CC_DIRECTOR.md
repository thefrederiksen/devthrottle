# PRD: CC Director - Enhanced Multi-Agent Management System

**Status:** ACTIVE
**Date:** 2026-05-20
**Author:** Soren
**Captured by:** claude (cc-director assistant)

## 1. Overview

CC Director is being evolved from a multi-session Claude Code (and other CLI) manager into a full **agent orchestration platform**.

The core problem it solves is the "meat computer bottleneck": the human (you) spending too much time babysitting messy terminal output, managing context, tracking progress across many parallel agents, and losing productivity due to interruptions and cognitive overload.

The new architecture introduces two major new concepts:
- **Session Supervisor** - intelligent wrapper around every individual agent session
- **Gateway** - central lightweight dashboard and control center

## 2. Core Goals

- Reduce time spent reading raw terminal junk
- Make agent state instantly visible at a glance (red/yellow/green)
- Enable effective management of 5-15+ parallel agents across multiple machines
- Support high-quality voice interaction while driving or doing other tasks (walkie-talkie style)
- Preserve progress and context even when sessions crash
- Centralize knowledge of what has been done across all projects (personal, consulting, Mindseed)
- Shift your role from constant babysitter to high-level decision maker and manager of agents

## 3. Key Architectural Concepts

### 3.1 Session Supervisor

Every session (Claude Code, Pi, Codex CLI, Gemini CLI, etc.) now runs with a dedicated **Session Supervisor** - a hidden parallel instance of the same CLI type (configurable per Director).

**Responsibilities of the Session Supervisor (in priority order):**

1. **Turn Detection** - Reliably detect when the main session has finished its turn and is waiting for input.
2. **Summarization** - Generate a clean, structured summary of each turn for the Agent View.
3. **Voice Interface** - Receive raw Whisper transcription, clean/improve the prompt, save both raw and refined versions, then inject the refined prompt into the main session.
4. **Status & Notification** - Determine if user input is needed and classify urgency (red = needs attention now).
5. **Memory & Rules Enforcement** - Read CLAUDE.md / memory files in the repo and actively flag when the main agent is violating established rules or forgetting constraints.
6. **Git Awareness** - Monitor git status and gently remind to commit/push when there are uncommitted changes (especially at end of turns).
7. **Resilience & Recovery** - Monitor for crashes (OOM, etc.), use terminal logs + git state to understand last known state, and assist in restoring sessions.
8. **Code Review Enforcement** - Ensure that code review tools/skills are run before commits/pushes (warning or blocking depending on implementation).

The Supervisor starts with **fresh context every turn** to keep token usage low. It outputs structured data (JSON block) directly in its terminal to signal completion.

Default configuration: Use Claude Code + Haiku (or equivalent cheap/fast model) for Supervisors, configurable per Director.

### 3.2 Gateway (Manager + Control Center)

The Gateway is a **lightweight dashboard** (Phase/Next: more intelligent management capabilities).

**Current Scope (Dumb Dashboard):**
- Unified view of all sessions across all running Directors/machines
- Red/Yellow/Green status indicators for each session
- Click/tap to jump directly into any session (with Agent View visible)
- Ability to start new sessions remotely:
  - Query target Director for list of pre-configured working directories
  - Choose directory and start session
- Central log aggregation and basic progress tracking

**Future Evolution:**
- Become a true Manager of Agents with its own session
- High-level coordination, daily/weekly summaries, project tracking across personal/consulting/Mindseed repos
- Remote Director control (start/kill/reboot machines)

## 4. User Interfaces

### 4.1 Agent View (per session)
- Replaces or sits alongside the raw terminal
- Clean, readable summary of current turn
- Structured history of previous turns (JSON-based)
- Clear list of questions/decisions needed from user, with context and recommendations

### 4.2 Gateway Dashboard
- Overview grid of all sessions (colored boxes)
- Filterable by machine, project type, status
- Quick jump to any session
- High-level progress metrics

### 4.3 Voice Mode (Walkie-Talkie Style)
- Push-to-talk (one tap starts recording, another sends)
- Designed for driving, cooking, walking, etc.
- Uses Whisper for transcription
- Goes through Session Supervisor for cleanup and routing
- Only available at **individual session level** (not on Gateway)
- Handles bad connections gracefully (records locally, uploads when possible)

## 5. Non-Functional Requirements

- Works with multiple CLIs (Claude Code, Pi, Codex CLI, Gemini CLI)
- Cross-platform (Windows + Mac support)
- Resilient to network issues and crashes
- Token-efficient (fresh context for Supervisor)
- Secure (especially for voice microphone access via Tailscale HTTPS)
- Logging: Raw terminal + structured Agent View logs preserved

## 6. Success Metrics

- Ability to comfortably manage 8+ parallel agents without constant monitoring
- Significant reduction in time spent reading raw terminal output
- Reliable "red box" notifications that pull you in only when truly needed
- Effective voice workflow while driving or multitasking
- Fewer lost sessions/context after crashes
- Ability to answer "what did we actually get done this week?" easily

## 7. Implementation Notes / Open Questions (for reference)

- Exact signaling mechanism for turn completion (structured terminal output)
- How aggressively the Supervisor should enforce rules/code review
- Depth of git and memory file integration
- Scheduler tool (cross-platform, independent of any one CLI)
- Future remote Director management agent per machine

---

This PRD captures the full architecture and vision developed in conversation. The implementation plan derived from it lives in `GOAL_CC_DIRECTOR_SUPERVISOR.md` (same directory).
