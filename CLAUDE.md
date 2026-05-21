# CC Director - Project Instructions

This is **enterprise-level software** requiring robust error handling, comprehensive logging, thorough testing, and responsive UI.

**Full coding standards:** [docs/CodingStyle.md](docs/CodingStyle.md)

**UI style guide:** [docs/VisualStyle.md](docs/VisualStyle.md) -- All UI changes must comply with this guide.

---

## Critical Rules

### 0. NEVER KILL RUNNING PROCESSES WITHOUT PERMISSION

**ABSOLUTELY NEVER use taskkill or any command to terminate cc-director.exe or any other running application without explicit user approval.**

The user runs multiple instances of cc-director simultaneously. Killing processes to "fix" build errors is NOT acceptable. If a build fails due to locked files:
- Tell the user the build failed because files are locked
- Ask the user if they want to close the application themselves
- NEVER automatically kill processes

This rule has NO exceptions.

### 0b. LAUNCH cc-director.exe VIA WINDOWS TASK SCHEDULER, NEVER DIRECTLY

**If you (the Claude agent) are running inside a Claude Code CLI session (you almost always are), DO NOT spawn `cc-director.exe` from your own process tree.** Use the `cc-director-launch` Windows scheduled task instead.

#### Why

When cc-director.exe is launched from inside your claude.exe ConPty, the child claude.exe processes IT spawns inherit a nested pseudo-console. Grandchild claudes detect this as a non-TTY environment and exit within ~3 seconds with:

> `Error: Input must be provided either through stdin or as a prompt argument when using --print`

This is claude.exe 2.1.143+ behavior on nested ConPty, not a CC Director bug.

#### The fix: Task Scheduler

Processes launched by Task Scheduler run under `svchost.exe` (the Schedule service), completely outside your ConPty. Grandchild claudes spawned by such a Director have clean stdio and survive.

**One-time setup** (idempotent, safe to re-run):

```powershell
# Point the task at your current test build. The WorkingDirectory must be set, or
# Avalonia's first-time resource resolution may fail with exit -1.
$exe = "D:\ReposFred\cc-director\local_builds\cc-director-avalonia5.exe"
$wd  = "D:\ReposFred\cc-director\local_builds"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $wd
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)  # far future, on-demand only
Register-ScheduledTask -TaskName "cc-director-launch" -Action $action -Trigger $trigger -Force
```

**To launch on demand:**

```powershell
Start-ScheduledTask -TaskName "cc-director-launch"
```

The Director boots with parent = `svchost.exe`, port-allocates a fresh Control API port (check the log at `%LOCALAPPDATA%\cc-director\logs\director\director-YYYY-MM-DD-<PID>.log` for the line `[ControlApiHost] Kestrel listening on http://0.0.0.0:<port>`), and you can drive it via REST normally.

#### Slot convention to avoid colliding with the user's running Directors

The user keeps long-lived Director processes (`cc-director-avalonia1.exe`, `cc-director-avalonia2.exe`, etc.) and you MUST NOT kill them. Reserve **slot 4 or higher** for your own test Directors. Build to that slot with `scripts\local-build-avalonia.ps1 -Slot 4` and point `cc-director-launch` at that path.

#### Cleaning up your own test Director

Only kill processes whose path matches the slot YOU launched (e.g. `cc-director-avalonia4.exe`). Confirm via `Get-Process | Select-Object Id, ProcessName, Path` before sending `Stop-Process`. Never use a blanket `Stop-Process -Name cc-director-avalonia*` — that would kill the user's working sessions.

For non-session-creating tests (HTML rendering, REST endpoint smoke, build-only verification) launching from your context is still fine. Only session-creation tests need the Task Scheduler path.

### 1. Responsive UI - MANDATORY

**Every user action MUST provide immediate visual feedback (<100ms).**

- Show dialogs/panels immediately, even if empty
- Display "Loading..." text or spinner for any operation >200ms
- Load expensive data (file I/O, API calls) asynchronously in background
- Use INotifyPropertyChanged to update UI when data arrives
- NEVER block the UI thread with synchronous I/O

```csharp
// BAD - Blocks UI
public MyDialog()
{
    InitializeComponent();
    var items = LoadFromDisk();  // FREEZES UI!
    ListBox.ItemsSource = items;
}

// GOOD - Immediate response
public MyDialog()
{
    InitializeComponent();
    LoadingText.Text = "Loading...";

    Loaded += async (_, _) =>
    {
        var items = await Task.Run(() => LoadFromDisk());
        ListBox.ItemsSource = items;
        LoadingText.Visibility = Visibility.Collapsed;
    };
}
```

### 2. Enterprise Logging - MANDATORY

**Every public method must log entry, exit, and errors.**

```csharp
public Session CreateSession(string repoPath)
{
    FileLog.Write($"[SessionManager] CreateSession: {repoPath}");
    try
    {
        var session = CreateSessionInternal(repoPath);
        FileLog.Write($"[SessionManager] Session created: {session.Id}");
        return session;
    }
    catch (Exception ex)
    {
        FileLog.Write($"[SessionManager] CreateSession FAILED: {ex.Message}");
        throw;
    }
}
```

### 3. No Fallback Programming

**Fix root causes, don't add fallbacks that hide problems.**

```csharp
// BAD
try { return GetValue(); }
catch { return "Unknown"; }  // Hides the real problem!

// GOOD
var value = GetValue();
if (value is null)
    throw new InvalidOperationException("Value not available");
return value;
```

### 4. Try-Catch at Entry Points Only

Put try-catch ONLY in:
- Event handlers (button clicks)
- Lifecycle methods (Loaded, Initialized)
- External event subscriptions

Do NOT put try-catch in helper methods or service methods.

### 5. Testing Required

- All public methods need unit tests
- All bug fixes need regression tests
- Use Arrange-Act-Assert pattern
- Name tests: `MethodName_Scenario_ExpectedResult`

### 6. UI Thread Safety

```csharp
// ALWAYS dispatch to UI thread for ObservableCollection changes
Dispatcher.BeginInvoke(() =>
{
    _sessions.Add(newSession);
});
```

---

## Naming Conventions

| Type | Convention | Example |
|------|------------|---------|
| Classes | PascalCase + suffix | `SessionManager`, `ConPtyBackend` |
| Methods | Verb + Noun | `CreateSession()`, `SendTextAsync()` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Async methods | Suffix Async | `KillSessionAsync()` |
| Tests | Method_Scenario_Result | `CreateSession_InvalidPath_Throws` |

---

## Logging Format

```
FileLog.Write($"[ClassName] MethodName: context={value}, result={result}");
FileLog.Write($"[ClassName] MethodName FAILED: {ex.Message}");
```

---

## CC Director CLI Tools

**Reference:** [docs/cli-reference.md](docs/cli-reference.md)

When using any cc-* tool, check `docs/cli-reference.md` for exact flags before calling. Key gotcha: use `--count` / `-n` for result limits, NOT `--limit`.

---

## Posting to LinkedIn (text + image from cc-comm-queue)

**Use the script. Do not improvise or build a new flow each time.**

```bash
# 1. Make sure cc-browser's "linkedin" connection is closed (Chrome locks the user-data-dir).
# 2. Start cc-playwright on the linkedin connection (auto-allocates port, uses cc-browser's profile):
cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/
# 3. Run the canonical script with the queue id prefix:
python scripts/linkedin-post-from-queue.py <queue-id-prefix>
# 4. Visually verify the screenshot the script prints, then mark posted:
cc-comm-queue mark-posted <queue-id> --by cc_playwright
```

**Full writeup:** [tools/cc-playwright/LINKEDIN_POSTING.md](tools/cc-playwright/LINKEDIN_POSTING.md) — covers the shadow-DOM Quill editor, the OS-file-dialog trap (DO NOT click "Upload from computer"), the click-intercept overlay workaround, and the verification gates.

**Connection README:** the LinkedIn user-data-dir at `%LOCALAPPDATA%\cc-director\connections\linkedin\README.md` repeats this so it lives next to the cookies it depends on.

---

## When in Doubt

1. Log more, not less
2. Fail explicitly, not silently
3. Show UI feedback immediately
4. Write a test
5. Read [docs/CodingStyle.md](docs/CodingStyle.md)
