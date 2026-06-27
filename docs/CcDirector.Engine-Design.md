# CcDirector.Engine - Background Engine Design

**Status:** Final Draft
**Date:** 2026-02-28
**Replaces:** Python scheduler (`scheduler/cc_director/`)

---

## Problem Statement

The cc-director project currently has a separate Python background service (`cc_director_service`) that handles job scheduling and communication dispatch. This creates several problems:

1. **Admin rights required** - Installing a Windows Service requires administrative access, making it unusable on company-managed laptops
2. **Two technology stacks** - Python service + C#/.NET WPF app means two build systems, two sets of dependencies, two deployment stories
3. **No visibility** - The Director (where the user spends all their time) has no awareness of whether the background service is running or what it's doing
4. **Process management** - Two separate processes that need to stay in sync, with no coordination mechanism between them

## Solution

**Eliminate the separate service entirely.** Build `CcDirector.Engine` as a .NET class library (DLL) that runs inside the Director process. The Director becomes the always-running host.

### Key Principles

- **No admin rights** - Everything runs as the current user, no service installation
- **Single process** - One application to start, one to keep running
- **Director is home** - The user lives in the Director; the Engine lives in the Director
- **Vault integration** - Meaningful outcomes go into the Vault; operational plumbing stays in the Engine DB
- **No default jobs** - Engine ships empty; jobs are added by the user as features are built

---

## Decisions

These decisions were made during the design interview and are final:

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Tech stack | Rewrite in C#/.NET | Single stack, no Python dependency |
| Close behavior | Minimize to system tray | Engine keeps running; right-click tray -> Exit to quit |
| Component name | CcDirector.Engine | Professional, clear |
| HTTP Gateway | Dropped entirely | Director UI replaces it; YAGNI |
| Run history | Auto-purge after 30 days | Keeps DB lean (~2,900 runs/month for 15-min jobs) |
| Orphaned runs | Mark failed, don't retry | Safe, no duplicate risk for side-effect jobs |
| Database location | `%LOCALAPPDATA%\cc-myvault\engine.db` | Co-located with vault.db in one data directory |
| Communications DB | Merged into engine.db | One operational database; communications table lives alongside jobs/runs. cc-comm-queue and Communication Manager updated to point at engine.db. |
| Vault integration | Hybrid approach | engine.db for plumbing, vault.db for knowledge (contacts, interactions, decisions) |
| Vault writes | Via cc-vault.exe CLI | Respects all Vault logic, FTS5 indexing, validation |
| Auto-start | At Windows login, minimized to tray | User-level startup shortcut, no admin required |
| Claude access | Engine references CcDirector.Core | Reuse ClaudeClient directly; pragmatic, refactor later if needed |
| Default jobs | None | Engine starts empty; features suggest adding jobs as they're built |

---

## Architecture

```
+--------------------------------------------------+
|  CcDirector.Wpf (Host)                           |
|                                                   |
|  +--------------------------------------------+  |
|  |  CcDirector.Engine (DLL)                    |  |
|  |                                             |  |
|  |  +-----------+  +------------------------+ |  |
|  |  | Scheduler |  | Communication          | |  |
|  |  | (Cron)    |  | Dispatcher             | |  |
|  |  +-----------+  +------------------------+ |  |
|  |                                             |  |
|  |  +-----------+  +------------------------+ |  |
|  |  | SQLite    |  | IJob implementations   | |  |
|  |  | Store     |  | (added by user)        | |  |
|  |  +-----------+  +------------------------+ |  |
|  |                                             |  |
|  |  +-----------+  +------------------------+ |  |
|  |  | Engine    |  | Vault Bridge           | |  |
|  |  | Events    |  | (cc-vault.exe CLI)     | |  |
|  |  +-----------+  +------------------------+ |  |
|  +--------------------------------------------+  |
|                                                   |
|  System Tray Icon (minimize-to-tray on close)     |
+--------------------------------------------------+

Data:
  %LOCALAPPDATA%\cc-myvault\
    vault.db      -- Personal knowledge (contacts, tasks, goals, ideas, documents)
    engine.db     -- Operational (jobs, runs, communications, media)
```

### Project Structure

```
src/
  CcDirector.Engine/              <-- NEW: .NET class library
    CcDirector.Engine.csproj
    EngineHost.cs                 -- Top-level start/stop, wires everything together
    Scheduling/
      Scheduler.cs                -- Main loop: poll due jobs, execute, update next_run
      CronExpression.cs           -- Wrapper around Cronos NuGet package
      JobExecutor.cs              -- Runs a job (process or in-proc), captures output
    Storage/
      EngineDatabase.cs           -- SQLite: jobs, runs, and communications tables
      JobRecord.cs                -- Data model for a scheduled job
      RunRecord.cs                -- Data model for a job execution
      CommunicationRecord.cs      -- Data model for a communication item
    Jobs/
      IJob.cs                     -- Interface for in-process jobs
      ProcessJob.cs               -- Runs an external command (shell-out)
    Dispatcher/
      CommunicationDispatcher.cs  -- Polls communications table, dispatches approved items
      EmailSender.cs              -- Sends via cc-outlook / cc-gmail CLI
      LinkedInSender.cs           -- Sends via cc-browser connections + LinkedIn skill
      SenderConfig.cs             -- Account routing (mindzie, personal, consulting)
    Vault/
      VaultBridge.cs              -- Writes outcomes to vault.db via cc-vault.exe CLI
    Events/
      EngineEvent.cs              -- Event types (JobStarted, JobCompleted, etc.)

  CcDirector.Core/                <-- Existing (Engine references this for ClaudeClient)
  CcDirector.Wpf/                 <-- Existing (adds Engine integration + system tray)
  CcDirector.Engine.Tests/        <-- NEW: unit tests
```

### Dependency Direction

```
CcDirector.Wpf --> CcDirector.Engine --> CcDirector.Core
                                     --> Cronos (NuGet)
                                     --> Microsoft.Data.Sqlite (NuGet)
```

The Engine references Core for `ClaudeClient` access. It does NOT reference any WPF/UI assemblies. If the Engine ever needs to move to a standalone host, the Core dependency comes with it (Core is also UI-free).

---

## Component Details

### 1. EngineHost

The single entry point that the WPF app calls.

```csharp
public class EngineHost : IDisposable
{
    public EngineHost(EngineOptions options);

    public void Start();           // Start scheduler loop + dispatcher
    public Task StopAsync();       // Graceful shutdown, drain running jobs

    public bool IsRunning { get; }
    public EngineStatus GetStatus();  // Job counts, uptime, next scheduled run

    // Events for the WPF app to observe
    public event Action<EngineEvent> OnEvent;
}

public class EngineOptions
{
    public string DatabasePath { get; set; }        // Default: %LOCALAPPDATA%/cc-myvault/engine.db
    public string LogDirectory { get; set; }        // Default: %LOCALAPPDATA%/cc-myvault/logs/
    public int CheckIntervalSeconds { get; set; }   // Default: 60
    public int ShutdownTimeoutSeconds { get; set; } // Default: 30
    public int RunRetentionDays { get; set; }       // Default: 30
}
```

### 2. Scheduler

Runs on a background thread. Every `CheckIntervalSeconds`:

1. Query `jobs` table for rows where `enabled = 1 AND next_run <= NOW()`
2. For each due job, submit to a `ThreadPool` (max 10 concurrent)
3. Skip jobs already running (tracked by a `ConcurrentHashSet<int>`)
4. After execution, calculate and store `next_run` from the cron expression

Uses the `Cronos` NuGet package for cron parsing (standard 5-field format).

**On startup:**
- Find any runs with `ended_at = NULL` (orphaned from previous session)
- Mark them as failed with message "Interrupted by shutdown"
- Recalculate `next_run` for all enabled jobs

**Daily maintenance:**
- Auto-purge runs older than `RunRetentionDays` (default 30 days)

### 3. Job System

Two kinds of jobs:

**Process jobs** (existing behavior) - Run an external command, capture stdout/stderr, record exit code. This is what the Python scheduler does today.

**In-process jobs** (new) - Implement `IJob` for jobs that run inside the Director process. No subprocess overhead, direct access to .NET APIs and ClaudeClient.

```csharp
public interface IJob
{
    string Name { get; }
    Task<JobResult> ExecuteAsync(CancellationToken cancellationToken);
}

public record JobResult(
    bool Success,
    string Output,
    string? Error = null
);
```

**No default jobs ship with the Engine.** Jobs are added by the user through the Director UI as features are built. When a new feature (like email triage) is implemented, it can suggest adding itself as a scheduled job.

### 4. Communication Dispatcher

Polls the `communications` table in engine.db for approved items and dispatches them.

**Polling interval:** 5 seconds
**Status flow:** `approved` -> dispatch -> `posted` (with `posted_by = 'cc_director'`)

Dispatches by platform:
- `email` -> `EmailSender` (shells out to `cc-outlook` or `cc-gmail`)
- `linkedin` -> `LinkedInSender` (uses cc-browser connections + LinkedIn navigation skill)

**Account routing** (ported from Python config):

| Persona | Email | Tool |
|---------|-------|------|
| work | user@company.com | cc-outlook |
| personal | user@personal.com | cc-gmail (personal) |
| consulting | user@consulting.com | cc-gmail (consulting) |

**Timing logic:**

| send_timing | Behavior |
|-------------|----------|
| immediate | Dispatch now |
| asap | Dispatch now |
| scheduled | Dispatch when `scheduled_for <= now` |
| hold | Skip (requires manual action) |

### 5. Vault Bridge

Writes meaningful outcomes to the Vault via `cc-vault.exe` CLI:

- **Contact interactions** - When the email triage agent processes an email, log it as an interaction on the contact: `cc-vault contacts memory add ...`
- **Tasks created** - When an agent decides follow-up is needed: `cc-vault tasks add ...`
- **Communication records** - When a message is dispatched, record the interaction

The CLI approach is slower than direct SQLite but respects all Vault logic (FTS5 indexing, vector embeddings, validation). Since Engine jobs run on multi-minute cycles, the per-call overhead is negligible.

### 6. SQLite Storage

Single database file at `%LOCALAPPDATA%\cc-myvault\engine.db`.

**Schema:**

```sql
-- Scheduled jobs
CREATE TABLE jobs (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT UNIQUE NOT NULL,
    cron            TEXT NOT NULL,
    command         TEXT NOT NULL,
    working_dir     TEXT,
    enabled         INTEGER DEFAULT 1,
    timeout_seconds INTEGER DEFAULT 300,
    tags            TEXT,
    created_at      TEXT DEFAULT (datetime('now')),
    updated_at      TEXT DEFAULT (datetime('now')),
    next_run        TEXT
);

-- Job execution history
CREATE TABLE runs (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id           INTEGER NOT NULL,
    job_name         TEXT NOT NULL,
    started_at       TEXT NOT NULL,
    ended_at         TEXT,
    exit_code        INTEGER,
    stdout           TEXT,
    stderr           TEXT,
    timed_out        INTEGER DEFAULT 0,
    duration_seconds REAL,
    FOREIGN KEY (job_id) REFERENCES jobs(id)
);

-- Communications (migrated from external communications.db)
CREATE TABLE communications (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_number    TEXT UNIQUE NOT NULL,
    platform         TEXT NOT NULL,
    type             TEXT,
    status           TEXT NOT NULL DEFAULT 'pending_review',
    subject          TEXT,
    body             TEXT,
    send_from        TEXT,
    persona          TEXT,
    recipient        TEXT,
    email_specific   TEXT,
    linkedin_specific TEXT,
    send_timing      TEXT DEFAULT 'immediate',
    scheduled_for    TEXT,
    created_at       TEXT DEFAULT (datetime('now')),
    approved_at      TEXT,
    posted_at        TEXT,
    posted_by        TEXT,
    tags             TEXT
);

-- Media attachments for communications
CREATE TABLE media (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    communication_id INTEGER NOT NULL,
    type             TEXT,
    filename         TEXT,
    alt_text         TEXT,
    file_size        INTEGER,
    mime_type        TEXT,
    data             BLOB,
    FOREIGN KEY (communication_id) REFERENCES communications(id)
);

-- Indexes
CREATE INDEX idx_runs_job_id ON runs(job_id);
CREATE INDEX idx_runs_started_at ON runs(started_at);
CREATE INDEX idx_jobs_next_run ON jobs(next_run);
CREATE INDEX idx_jobs_enabled ON jobs(enabled);
CREATE INDEX idx_comms_status ON communications(status);
CREATE INDEX idx_comms_timing ON communications(send_timing);
```

Uses `Microsoft.Data.Sqlite` NuGet package. No ORM -- direct SQL.

### 7. Engine Events

The Engine communicates with the WPF host through a simple event system:

```csharp
public record EngineEvent(
    EngineEventType Type,
    string? JobName = null,
    int? RunId = null,
    string? Message = null,
    DateTime Timestamp = default
);

public enum EngineEventType
{
    EngineStarted,
    EngineStopping,
    EngineStopped,
    JobStarted,
    JobCompleted,
    JobFailed,
    JobTimeout,
    CommunicationDispatched,
    Error
}
```

The WPF app subscribes to `OnEvent` and can show notifications, update status indicators, or log events.

---

## Director Integration (WPF Changes)

### System Tray (Minimize on Close)

When the user clicks X:
- Window hides (not closes)
- A `NotifyIcon` appears in the system tray with the Director icon
- Right-click menu: "Open Director" | "Engine Status" | "Exit"
- Double-click tray icon: restore window
- "Exit" actually shuts down the Engine and closes the app

Uses `Hardcodet.NotifyIcon.Wpf` NuGet package (mature, well-supported WPF tray icon library).

### Auto-Start at Login

On first run, the Director adds a user-level startup shortcut:
- Creates a shortcut in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\`
- The shortcut launches the Director with a `--minimized` flag
- Director starts minimized to tray, Engine begins processing immediately
- No admin rights required (user-level startup folder)
- Can be toggled on/off in Director settings

### Engine Status in Sidebar

Add a small status section to the Director sidebar (below the sessions list):

```
Engine: Running
  Next job: Email Triage (12 min)
  Jobs today: 47 OK / 2 failed
  Last dispatch: 3 min ago
```

Clicking it expands to show recent job runs.

### Engine Startup

In `App.xaml.cs`, after existing initialization:

```csharp
var myvaultDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "cc-myvault");

_engineHost = new EngineHost(new EngineOptions
{
    DatabasePath = Path.Combine(myvaultDir, "engine.db"),
    LogDirectory = Path.Combine(myvaultDir, "logs")
});
_engineHost.OnEvent += HandleEngineEvent;
_engineHost.Start();
```

On app exit:
```csharp
await _engineHost.StopAsync();
```

---

## What Gets Removed

Once the Engine is working and validated:

1. **`scheduler/` directory** - The entire Python scheduler codebase
2. **PyInstaller build** - `scheduler/build.ps1` and related
3. **Python dependencies** - `croniter`, `fastapi`, `uvicorn`, `click`, etc.
4. **`cc_director_service.exe`** - The compiled Python service executable
5. **`cc_scheduler.exe`** - The CLI tool (replaced by Director UI)

---

## Migration Plan

### Phase 1: Engine Infrastructure
- Create `CcDirector.Engine` project and add to solution
- Implement SQLite storage (jobs + runs + communications tables)
- Implement cron expression handling (Cronos NuGet)
- Implement scheduler loop (poll due jobs, execute, update next_run)
- Implement process job executor
- Implement orphaned run cleanup on startup
- Implement 30-day run history auto-purge
- Unit tests for all of the above

### Phase 2: Communication Dispatcher + Tool Migration
- Implement `EmailSender` (cc-outlook / cc-gmail shell-out)
- Implement `LinkedInSender` (cc-browser connections + LinkedIn skill)
- Implement `CommunicationDispatcher` (poll communications table, dispatch approved items)
- Implement account routing config
- Update dependent tools to point at engine.db (see "Dependent Tools Migration" below)
- Migrate existing communication records from old `communications.db` if available (nice-to-have)
- Unit tests

### Phase 3: Director Integration
- Add system tray with `Hardcodet.NotifyIcon.Wpf`
- Implement minimize-to-tray on close
- Implement auto-start at login (startup folder shortcut)
- Wire `EngineHost` into `App.xaml.cs` startup/shutdown
- Add engine status indicator to sidebar
- Subscribe to engine events for UI notifications
- Add `--minimized` launch flag support
- Unit tests

### Phase 4: Vault Bridge
- Implement `VaultBridge` (cc-vault.exe CLI wrapper)
- Wire Vault writes into communication dispatcher (log interactions on contacts)
- Design the pattern for future jobs to report outcomes to Vault

### Phase 5: Cleanup
- Verify all Python scheduler functionality is covered
- Remove `scheduler/` directory and all Python build scripts
- Update documentation

### Future Phases (Separate Work Items)
- **Email Triage Job** - IJob implementation, cc-outlook integration, Claude Code headless, memory files
- **Additional jobs** - Added by user as features are built; each feature suggests its own scheduled job

---

## Dependent Tools Migration

These external tools currently read from or write to `communications.db` at the old path (`D:\ReposFred\cc-consult\tools\communication_manager\content\`). They must be updated to point at `engine.db` in `%LOCALAPPDATA%\cc-myvault\`.

### 1. cc-comm-queue (WRITER -- queues new communications)

The `/write` skill calls this CLI to add emails and LinkedIn posts to the queue. It's the primary way communications enter the system.

**Files to change:**
- `D:\ReposFred\devthrottle\src\cc-comm-queue\src\queue_manager.py` line 66: change `self.db_path = queue_path / "communications.db"` to `self.db_path = queue_path / "engine.db"`
- `D:\ReposFred\devthrottle\src\cc_shared\config.py` line 173: change `CommManagerConfig.queue_path` default from `"D:/ReposFred/cc-consult/tools/communication_manager/content"` to the cc-myvault directory

### 2. Communication Manager WPF App (REVIEWER -- approve/reject queue)

The approval UI where communications are reviewed before sending.

**Files to change:**
- `D:\ReposFred\cc-consult\tools\communication_manager\src\CommunicationManager\Services\DatabaseService.cs` line 22: change `"communications.db"` to `"engine.db"` and update the content path

### 3. /write Skill (ORCHESTRATOR -- no change needed)

The skill at `~/.claude/skills/write/skill.md` calls `cc-comm-queue add ...` and does not reference the database path directly. As long as cc-comm-queue is updated, the skill works without changes.

### 4. Python Scheduler Dispatcher (REPLACED -- deleted)

The old dispatcher at `D:\ReposFred\devthrottle\scheduler\cc_director\` is entirely replaced by the C# Engine. No update needed -- it gets deleted in Phase 5.

---

## Data Migration

### Jobs and Runs

Existing jobs and run history from the Python scheduler's SQLite database:

```sql
ATTACH DATABASE 'old_cc_director.db' AS old;
INSERT INTO jobs SELECT * FROM old.jobs;
INSERT INTO runs SELECT * FROM old.runs;
DETACH DATABASE old;
```

### Communications (Nice-to-Have)

Existing records in the old `communications.db` can optionally be migrated. These are historical (already posted/rejected) so losing them is not critical. If the old DB is available:

```sql
ATTACH DATABASE 'old_communications.db' AS comm;
INSERT OR IGNORE INTO communications SELECT * FROM comm.communications;
INSERT OR IGNORE INTO media SELECT * FROM comm.media;
DETACH DATABASE comm;
```

A one-time migration command in the Director handles both.
