# CcDirector.Engine - Handover Document

**Date:** 2026-02-28
**Context:** Phase 1 of 5 is complete. This document tells you everything you need to continue.

---

## What We Built and Why

CC Director is a WPF app that manages Claude Code sessions. It had a separate Python background service (`scheduler/`) for job scheduling and communication dispatch. This was problematic: required admin rights to install as a Windows Service, two tech stacks (Python + C#), and the Director had no visibility into the service.

**The decision:** Kill the Python service entirely. Build `CcDirector.Engine` as a C# class library that runs inside the Director process. No admin rights, single process, single tech stack.

The full design document with all decisions is at: `docs/CcDirector.Engine-Design.md`

---

## Current State: Phase 1 Complete

Phase 1 (Engine Infrastructure) is done. The solution builds with zero warnings, zero errors, and all 44 unit tests pass.

### What Exists Now

```
src/CcDirector.Engine/                   <-- NEW class library
  CcDirector.Engine.csproj               NuGet: Cronos 0.8.4, Microsoft.Data.Sqlite 9.0.2
  EngineHost.cs                          Top-level Start/StopAsync, events, status
  EngineOptions.cs                       Config: DB path, log dir, intervals, retention
  EngineStatus.cs                        Status model (uptime, job counts, today's stats)
  Scheduling/
    CronHelper.cs                        Cronos wrapper: IsValid, GetNextOccurrence, Describe
    JobExecutor.cs                       Runs a ProcessJob, records results to DB, updates next_run
    Scheduler.cs                         Background loop: poll due jobs, execute, concurrency limit (10)
  Storage/
    EngineDatabase.cs                    SQLite: jobs, runs, communications, media tables + CRUD
    JobRecord.cs                         Data model for scheduled jobs
    RunRecord.cs                         Data model for job execution records
  Jobs/
    IJob.cs                              Interface + JobResult record
    ProcessJob.cs                        Runs external commands via cmd.exe with timeout
  Events/
    EngineEvent.cs                       Event types + EngineEventType enum

src/CcDirector.Engine.Tests/             <-- NEW test project (44 tests, all passing)
  EngineHostTests.cs                     6 tests: start/stop, events, status, database exposure
  Scheduling/
    CronHelperTests.cs                   10 tests: validation, next occurrence, describe
    SchedulerTests.cs                    3 tests: orphan cleanup, next_run init, event raising
  Storage/
    EngineDatabaseTests.cs               25 tests: full CRUD for jobs/runs, due jobs, cleanup, purge
```

### Dependencies

```
CcDirector.Wpf --> CcDirector.Engine --> CcDirector.Core (for FileLog via CcDirector.Core.Utilities)
                                     --> Cronos 0.8.4
                                     --> Microsoft.Data.Sqlite 9.0.2
```

### Key Design Patterns in the Code

- **Logging:** Every public method uses `FileLog.Write($"[ClassName] MethodName: context")` -- this is a project requirement (see CLAUDE.md)
- **No ORM:** Direct SQL with `Microsoft.Data.Sqlite`, parameterized queries
- **SQLite WAL mode:** Enabled per-connection for concurrent access
- **DateTime handling:** All dates stored as ISO 8601 strings (`ToString("o")`), all in UTC
- **Concurrency:** `ConcurrentDictionary<int, byte>` tracks running jobs, `SemaphoreSlim(10)` limits concurrency
- **Shutdown:** 1-second sleep intervals in the scheduler loop for responsive cancellation
- **Orphan cleanup:** On startup, any runs with `ended_at = NULL` get marked as failed with exit_code -1

### Database Location

All data lives in `%LOCALAPPDATA%\cc-myvault\`:
- `vault.db` -- Vault 2.0 personal knowledge (contacts, tasks, goals, ideas) -- existing, do not touch
- `engine.db` -- Engine operational data (jobs, runs, communications, media) -- created by EngineDatabase

The Engine's `EngineOptions.DatabasePath` defaults to this location.

### What is NOT Built Yet

These folders from the design doc do not exist yet -- they are in later phases:
- `Dispatcher/` (CommunicationDispatcher, EmailSender, LinkedInSender, SenderConfig) -- Phase 2
- `Vault/` (VaultBridge) -- Phase 4
- `Storage/CommunicationRecord.cs` -- Phase 2 (the communications table exists in the DB schema but has no C# model yet)

---

## What Comes Next: Phase 2

**Communication Dispatcher + Tool Migration**

### 2a. Build the Dispatcher (in CcDirector.Engine)

Port the Python dispatcher to C#. The Python source to reference:
- `{repo}\scheduler\cc_director\dispatcher\email_sender.py`
- `{repo}\scheduler\cc_director\dispatcher\linkedin_sender.py`
- `{repo}\scheduler\cc_director\dispatcher\sqlite_watcher.py`
- `{repo}\scheduler\cc_director\dispatcher\config.py`

Create these files:
- `src/CcDirector.Engine/Dispatcher/CommunicationDispatcher.cs` -- Polls communications table every 5 seconds for `status='approved'` items with `send_timing IN ('immediate','asap')` or `scheduled_for <= now`. Dispatches by platform, marks as `posted`.
- `src/CcDirector.Engine/Dispatcher/EmailSender.cs` -- Shells out to `cc-outlook` or `cc-gmail` CLI. Accounts selected by `send_from` / `persona` field.
- `src/CcDirector.Engine/Dispatcher/LinkedInSender.cs` -- Uses `cc-browser` with LinkedIn connection. Routes by type (post/comment/message).
- `src/CcDirector.Engine/Dispatcher/SenderConfig.cs` -- Account routing table.
- `src/CcDirector.Engine/Storage/CommunicationRecord.cs` -- Data model matching the communications table schema.

Key behavior from the Python code:
- In-flight tracking: a `Set<int>` of ticket_numbers prevents double-dispatching
- Email body URLs are linkified (plain URLs wrapped in `<a href>` tags)
- Media BLOBs are extracted to temp files before passing to CLI tools
- After dispatch, update: `status='posted', posted_at=now, posted_by='cc_director'`

Wire the dispatcher into EngineHost.Start() as a second background loop (alongside the Scheduler).

### 2b. Update External Tools to Point at engine.db

Three tools currently write to/read from `communications.db` at the old path. They need to point at `engine.db` in `%LOCALAPPDATA%\cc-myvault\`.

**1. cc-comm-queue** (Python CLI -- WRITER)
- `{cc-director}\src\cc-comm-queue\src\queue_manager.py` line 66
  - Change: `self.db_path = queue_path / "communications.db"` -> `self.db_path = queue_path / "engine.db"`
- `{cc-director}\src\cc_shared\config.py` line 173
  - Change: `queue_path` default from the old communication_manager content path to `%LOCALAPPDATA%/cc-myvault`

**2. Communication Manager WPF App** (C# -- REVIEWER)
- `{cc-consult}\tools\communication_manager\src\CommunicationManager\Services\DatabaseService.cs` line 22
  - Change: `"communications.db"` -> `"engine.db"` and update content path to cc-myvault directory

**3. /write Skill** -- No change needed. It calls `cc-comm-queue`, which handles the path.

**IMPORTANT:** The communications table schema in engine.db must match what cc-comm-queue expects. Compare `queue_manager.py`'s `_COMMUNICATIONS_COLUMNS` (lines 22-57) against the schema in `EngineDatabase.cs`. There may be columns in cc-comm-queue's schema that are not in the Engine's schema (e.g., `persona_display`, `created_by`, `posted_url`, `post_id`, `rejected_at`, `rejected_by`, `rejection_reason`, `context_url`, `context_title`, `context_author`, `destination_url`, `campaign_id`, `notes`, `recipient`, `thread_content`). The Engine's communications table needs to be expanded to include ALL columns that cc-comm-queue writes, or the inserts will fail.

### 2c. Optional: Migrate Old Data

If `{cc-consult}\tools\communication_manager\content\communications.db` has data worth keeping, it can be migrated with:

```sql
ATTACH DATABASE 'old_communications.db' AS comm;
INSERT OR IGNORE INTO communications SELECT * FROM comm.communications;
INSERT OR IGNORE INTO media SELECT * FROM comm.media;
DETACH DATABASE comm;
```

This is nice-to-have -- the old records are mostly historical (posted/rejected).

---

## Phases 3-5 Summary

### Phase 3: Director Integration
- System tray (minimize-to-tray on close) using `Hardcodet.NotifyIcon.Wpf` NuGet
- Auto-start at Windows login via user-level startup folder shortcut
- Wire EngineHost into `App.xaml.cs` startup/shutdown
- Engine status indicator in sidebar
- `--minimized` launch flag
- See design doc "Director Integration" section for details

### Phase 4: Vault Bridge
- `VaultBridge.cs` -- wrapper around `cc-vault.exe` CLI calls
- Write contact interactions, create tasks, log dispatch outcomes to vault.db
- Vault DB is at `%LOCALAPPDATA%\cc-myvault\vault.db`
- CLI tool is at `%LOCALAPPDATA%\cc-director\bin\cc-vault.exe`

### Phase 5: Cleanup
- Verify all Python scheduler functionality is ported
- Delete `scheduler/` directory entirely
- Remove Python build scripts

### Future: Email Triage Job
- IJob implementation that calls `cc-outlook list --unread` every 15 minutes
- Uses Claude Code headless to triage each email (archive, flag, draft reply, escalate)
- This was the original motivation for the Engine but comes after infrastructure is solid

---

## Key Files to Read

| File | Why |
|------|-----|
| `docs/CcDirector.Engine-Design.md` | Full design with all decisions, schema, architecture |
| `CLAUDE.md` | Project coding standards (logging, error handling, responsive UI, no fallbacks) |
| `docs/CodingStyle.md` | Detailed coding style guide |
| `src/CcDirector.Engine/EngineHost.cs` | Entry point -- understand how Start/Stop works |
| `src/CcDirector.Engine/Storage/EngineDatabase.cs` | All DB operations -- understand the data layer |
| `src/CcDirector.Engine/Scheduling/Scheduler.cs` | The core loop -- understand job execution flow |
| `scheduler/cc_director/dispatcher/` | Python source to port for Phase 2 |

## Build and Test Commands

```bash
# Build entire solution
cd {repo}
dotnet build cc-director.sln

# Build Engine only
dotnet build src/CcDirector.Engine/CcDirector.Engine.csproj

# Run Engine tests only
dotnet test src/CcDirector.Engine.Tests/CcDirector.Engine.Tests.csproj

# Run all tests
dotnet test cc-director.sln
```

## Critical Rules (from CLAUDE.md)

1. NEVER kill running processes without permission
2. Every public method must log entry, exit, and errors via `FileLog.Write`
3. No fallback programming -- fix root causes
4. Try-catch at entry points only (event handlers, lifecycle methods)
5. All public methods need unit tests
6. Never commit without being asked
7. No Unicode/emojis anywhere in code or output
