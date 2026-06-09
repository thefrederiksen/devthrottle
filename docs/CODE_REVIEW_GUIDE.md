# CC Director - Comprehensive Code Review Guide

**Version:** 1.1.0
**Target Framework:** .NET 10
**Platform:** Windows 10/11 (WPF Desktop Application)
**Last Updated:** 2026-02-21

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Architecture Overview](#2-architecture-overview)
3. [Project Structure](#3-project-structure)
4. [Core Domain Model](#4-core-domain-model)
5. [Key Components Deep Dive](#5-key-components-deep-dive)
6. [External Dependencies](#6-external-dependencies)
7. [Feature Inventory](#7-feature-inventory)
8. [Data Flow & IPC](#8-data-flow--ipc)
9. [Configuration](#9-configuration)
10. [Testing Strategy](#10-testing-strategy)
11. [Coding Standards](#11-coding-standards)
12. [Security Considerations](#12-security-considerations)
13. [Known Constraints](#13-known-constraints)
14. [Review Focus Areas](#14-review-focus-areas)

---

## 1. Executive Summary

**CC Director** is a Windows desktop application for managing multiple [Claude Code](https://docs.anthropic.com/en/docs/claude-code) CLI sessions simultaneously. It provides:

- **Multi-session management**: Run independent Claude Code instances side-by-side, each working on a different repository
- **Real-time activity tracking**: Color-coded status indicators showing Claude's cognitive state (Idle, Working, Waiting for Input, etc.)
- **Session persistence**: Sessions survive application restarts with automatic reconnection
- **Embedded console hosting**: Native Windows console windows overlaid onto the WPF application
- **Git integration**: Repository status display with staged/unstaged changes
- **Voice mode**: Speech-to-text and text-to-speech integration for voice interaction with Claude

### Core Problem Solved

Developers using Claude Code often need to work on multiple codebases simultaneously. CC Director eliminates the need to switch between terminal windows by providing a unified interface to spawn, monitor, and interact with multiple Claude Code sessions.

### Technical Approach

The application uses Windows Pseudo Console (ConPTY) for terminal hosting, Windows Named Pipes for IPC with Claude Code hooks, and standard WPF patterns for the UI layer.

---

## 2. Architecture Overview

### Layer Diagram

```
+------------------------------------------------------------------+
|                         WPF UI Layer                              |
|  (CcDirector.Wpf)                                                 |
|  - MainWindow, Dialogs, Controls, ViewModels                     |
|  - EmbeddedBackend (console overlay)                             |
+------------------------------------------------------------------+
                              |
                              v
+------------------------------------------------------------------+
|                      Core Services Layer                          |
|  (CcDirector.Core)                                               |
|  +------------+  +------------+  +------------+  +------------+  |
|  |  Sessions  |  |    Hooks   |  |   Pipes    |  |    Git     |  |
|  +------------+  +------------+  +------------+  +------------+  |
|  +------------+  +------------+  +------------+  +------------+  |
|  |  Backends  |  |  ConPTY    |  |   Voice    |  | Utilities  |  |
|  +------------+  +------------+  +------------+  +------------+  |
+------------------------------------------------------------------+
                              |
                              v
+------------------------------------------------------------------+
|                    Native Windows APIs                            |
|  - ConPTY (CreatePseudoConsole, ResizePseudoConsole)            |
|  - Named Pipes (NamedPipeServerStream)                           |
|  - Process Management (CreateProcessW)                           |
|  - Win32 Window Management (for console embedding)               |
+------------------------------------------------------------------+
```

### Key Architectural Patterns

| Pattern | Implementation | Purpose |
|---------|---------------|---------|
| **Strategy Pattern** | `ISessionBackend` interface with 3 implementations | Abstracts terminal hosting modes |
| **Observer Pattern** | Events (`StatusChanged`, `OnActivityStateChanged`) | UI updates on state changes |
| **State Machine** | `ActivityState` enum with `HandlePipeEvent()` | Claude's cognitive state tracking |
| **Producer-Consumer** | `BlockingCollection<string>` in `FileLog` | Thread-safe async logging |
| **Repository Pattern** | `RecentSessionStore`, `RepositoryRegistry` | Persistence abstraction |

### Design Principles

1. **No DI Containers**: Explicit construction with `new` for clarity
2. **No Web Dependencies**: Pure WPF desktop application
3. **Fail Fast**: Validate early, throw specific exceptions
4. **No Fallbacks**: Fix root causes, don't hide problems
5. **Responsive UI**: Immediate visual feedback for all user actions

---

## 3. Project Structure

### Solution Layout

```
cc-director.sln
|
+-- src/
|   +-- CcDirector.Core/              # Core business logic (no UI)
|   |   +-- Backends/                 # Session backend implementations
|   |   |   +-- ISessionBackend.cs    # Backend interface
|   |   |   +-- PipeBackend.cs        # Stateless pipe mode
|   |   +-- Claude/                   # Claude Code integration
|   |   |   +-- ClaudeSessionReader.cs
|   |   |   +-- ClaudeSessionMetadata.cs
|   |   +-- Configuration/            # App configuration
|   |   |   +-- AgentOptions.cs
|   |   |   +-- RepositoryRegistry.cs
|   |   |   +-- RepositoryConfig.cs
|   |   +-- ConPty/                   # Windows Pseudo Console
|   |   |   +-- NativeMethods.cs      # P/Invoke declarations
|   |   |   +-- ProcessHost.cs        # Process spawning
|   |   |   +-- PseudoConsole.cs      # ConPTY wrapper
|   |   +-- Git/                      # Git integration
|   |   |   +-- GitStatusProvider.cs
|   |   |   +-- GitSyncStatusProvider.cs
|   |   +-- Hooks/                    # Claude Code hook system
|   |   |   +-- HookInstaller.cs      # settings.json manipulation
|   |   |   +-- HookRelayScript.cs    # PowerShell relay source
|   |   +-- Memory/                   # Terminal buffer
|   |   |   +-- CircularTerminalBuffer.cs
|   |   +-- Pipes/                    # Named pipe IPC
|   |   |   +-- DirectorPipeServer.cs
|   |   |   +-- PipeMessage.cs
|   |   |   +-- EventRouter.cs
|   |   +-- Sessions/                 # Session management
|   |   |   +-- Session.cs            # Central session abstraction
|   |   |   +-- SessionManager.cs     # Lifecycle management
|   |   |   +-- ActivityState.cs      # Cognitive state enum
|   |   |   +-- RecentSessionStore.cs # Persistence
|   |   +-- Utilities/
|   |   |   +-- FileLog.cs            # Thread-safe logging
|   |   |   +-- NulFileWatcher.cs     # Windows nul file cleanup
|   |   +-- Voice/                    # Voice mode services
|   |       +-- VoiceModeController.cs
|   |       +-- WhisperSttService.cs
|   |       +-- OpenAiSttService.cs
|   |       +-- OpenAiTtsService.cs
|   |       +-- PiperTtsService.cs
|   |       +-- ClaudeSummarizer.cs
|   |
|   +-- CcDirector.Wpf/               # WPF desktop application
|   |   +-- App.xaml / App.xaml.cs    # Application entry point
|   |   +-- MainWindow.xaml/.cs       # Main window
|   |   +-- Backends/
|   |   |   +-- EmbeddedBackend.cs    # Console overlay implementation
|   |   +-- Controls/
|   |   |   +-- EmbeddedConsoleHost.cs
|   |   +-- Helpers/
|   |   |   +-- TerminalCell.cs
|   |   +-- Voice/
|   |   |   +-- [Voice UI components]
|   |   +-- *Dialog.xaml/.cs          # Modal dialogs
|   |   +-- appsettings.json          # Configuration file
|   |   +-- app.ico                   # Application icon
|   |
|   +-- CcDirector.Core.Tests/        # xUnit test suite
|   |   +-- ActivityStateTests.cs
|   |   +-- CircularTerminalBufferTests.cs
|   |   +-- DirectorPipeServerTests.cs
|   |   +-- EventRouterTests.cs
|   |   +-- GitStatusProviderTests.cs
|   |   +-- GitSyncStatusProviderTests.cs
|   |   +-- HookInstallerTests.cs
|   |   +-- RepositoryRegistryTests.cs
|   |   +-- SessionManagerTests.cs
|   |   +-- SessionPersistenceTests.cs
|   |   +-- SessionVerificationTests.cs
|   |   +-- TerminalVerificationIntegrationTests.cs
|   |   +-- VoiceModeControllerTests.cs
|   |   +-- Voice/
|   |       +-- [Voice-specific tests]
|   |
|   +-- CcDirector.TestHarness/       # Integration testing
|   +-- CcClick/                      # UI automation utility
|   +-- ConPtyTest/                   # ConPTY testing
|   +-- ConsoleDiagnostic/            # Diagnostic utility
|
+-- docs/                             # Documentation
|   +-- CodingStyle.md                # Coding standards
|   +-- VisualStyle.md                # UI design guide
|   +-- [Other design docs]
|
+-- local_builds/
|   +-- cc-director.exe               # Local build output
|
+-- images/                           # Screenshots for README
+-- CLAUDE.md                         # Project instructions for Claude
+-- README.md                         # Project overview
```

### Project Dependencies

```
CcDirector.Wpf  -->  CcDirector.Core
                         |
CcDirector.Core.Tests -->+
```

---

## 4. Core Domain Model

### Session State Model

```
+-------------------+     +------------------+
|    SessionStatus  |     |   ActivityState  |
+-------------------+     +------------------+
| Starting          |     | Starting         |  <- Session created
| Running           |     | Idle             |  <- Claude finished response (Stop hook)
| Exiting           |     | Working          |  <- User sent prompt / tool executing
| Exited            |     | WaitingForInput  |  <- Claude asking for input
| Failed            |     | WaitingForPerm   |  <- Permission prompt shown
+-------------------+     | Exited           |  <- Session ended
                          +------------------+

SessionStatus = Process lifecycle (is claude.exe running?)
ActivityState = Claude's cognitive state (what is Claude doing?)
```

### Session Entity

```csharp
public class Session
{
    // Identity
    public Guid Id { get; }
    public string RepoPath { get; }
    public string? ClaudeSessionId { get; set; }  // From hook events

    // State
    public SessionStatus Status { get; }
    public ActivityState ActivityState { get; }
    public VerificationStatus VerificationStatus { get; }

    // Display
    public string? CustomName { get; set; }
    public string? CustomColor { get; set; }

    // Backend
    public ISessionBackend Backend { get; }
    public CircularTerminalBuffer? Buffer { get; }

    // Events
    public event Action<SessionStatus, SessionStatus>? StatusChanged;
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    // Methods
    public void HandlePipeEvent(PipeMessage msg);  // State machine
    public Task SendTextAsync(string text);
    public Task SendEnterAsync();
}
```

### Backend Abstraction

```csharp
public interface ISessionBackend
{
    int ProcessId { get; }
    SessionStatus Status { get; }
    bool IsRunning { get; }
    bool HasExited { get; }
    CircularTerminalBuffer? Buffer { get; }

    void Start(string workingDirectory, string command, string arguments);
    void Write(byte[] data);
    Task SendTextAsync(string text);
    Task SendEnterAsync();
    void Resize(int width, int height);
    Task GracefulShutdownAsync(int timeoutMs);

    event Action<SessionStatus, SessionStatus>? StatusChanged;
    event Action<int>? ProcessExited;
}
```

**Implementations:**

| Backend | Description | Use Case |
|---------|-------------|----------|
| `EmbeddedBackend` | Native console window overlay | Primary mode - full terminal fidelity |
| `ConPtyBackend` | Windows Pseudo Console with WPF rendering | Alternative mode - custom rendering |
| `PipeBackend` | Stateless, spawns process per prompt | Headless/API mode |

---

## 5. Key Components Deep Dive

### 5.1 Session Management

**SessionManager.cs** - Orchestrates session lifecycle

```csharp
public class SessionManager
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions;
    private readonly ConcurrentDictionary<string, Guid> _claudeSessionMap;  // session_id -> Guid

    public Session CreateSession(string repoPath, string? claudeArgs);
    public Session? RestoreEmbeddedSession(SessionState state);
    public void RegisterClaudeSession(string claudeSessionId, Guid sessionId);
    public Session? GetSessionByClaudeId(string claudeSessionId);
    public Task KillSessionAsync(Guid sessionId);
}
```

### 5.2 Hook Infrastructure

**HookInstaller.cs** - Manages Claude Code hooks in `~/.claude/settings.json`

- Installs 12 hook event types
- Preserves existing user hooks (merge, not overwrite)
- Creates backups before modification
- All hooks are `async: true` (non-blocking)

**Hook Events Captured:**

| Event | Purpose |
|-------|---------|
| `SessionStart` | Detect new session |
| `UserPromptSubmit` | User sent prompt -> Working |
| `PreToolUse` | Tool about to execute -> Working |
| `PostToolUse` | Tool completed |
| `PostToolUseFailure` | Tool failed |
| `PermissionRequest` | Permission prompt shown -> WaitingForPerm |
| `Notification` | Generic notification |
| `SubagentStart` | Subagent spawned |
| `SubagentStop` | Subagent completed |
| `Stop` | Response complete -> Idle |
| `PreCompact` | Context compaction |
| `SessionEnd` | Session terminated |

### 5.3 Pipe IPC System

**DirectorPipeServer.cs** - Named pipe server

```csharp
public class DirectorPipeServer
{
    private const string PipeName = "CC_ClaudeDirector";

    public event Action<PipeMessage>? OnMessageReceived;

    public void Start();   // Begin async accept loop
    public void Stop();    // Shutdown server
}
```

**Data Flow:**

```
Claude Code Session
       |
       | (hook fires)
       v
PowerShell Relay Script (hook-relay.ps1)
       |
       | (reads stdin JSON, writes to named pipe)
       v
\\.\pipe\CC_ClaudeDirector
       |
       | (NamedPipeServerStream accepts connection)
       v
DirectorPipeServer
       |
       | (deserializes JSON to PipeMessage)
       v
EventRouter
       |
       | (maps session_id to Session)
       v
Session.HandlePipeEvent(PipeMessage)
       |
       | (state machine transition)
       v
UI Update (via INotifyPropertyChanged)
```

### 5.4 Terminal Buffer

**CircularTerminalBuffer.cs** - Thread-safe ring buffer for terminal output

```csharp
public class CircularTerminalBuffer
{
    private readonly byte[] _buffer;           // Ring buffer storage
    private readonly int _capacity;            // Default: 2 MB
    private long _totalBytesWritten;           // Monotonic counter
    private readonly ReaderWriterLockSlim _lock;

    public void Write(byte[] data);
    public byte[] ReadAll();
    public byte[] ReadFrom(long position);     // For streaming reads
}
```

### 5.5 Git Integration

**GitStatusProvider.cs** - Async git status polling

```csharp
public static class GitStatusProvider
{
    public static Task<GitStatusResult> GetStatusAsync(string repoPath);
}

public class GitStatusResult
{
    public bool Success { get; }
    public List<GitFileChange> StagedChanges { get; }
    public List<GitFileChange> UnstagedChanges { get; }
    public string? ErrorMessage { get; }
}

public enum GitFileStatus
{
    Modified, Added, Deleted, Renamed, Copied, Untracked, Unknown
}
```

### 5.6 Session Verification

**ClaudeSessionReader.cs** - Validates sessions against Claude's .jsonl files

The application can lose track of which Claude Code session corresponds to which Director session (e.g., after restart). Session verification:

1. Reads terminal content from the console
2. Extracts user prompts from Claude's `.jsonl` transcript files
3. Performs text matching (whitespace-insensitive)
4. Confirms session identity when sufficient matches found

```csharp
public class ClaudeSessionReader
{
    public VerificationResult VerifyWithTerminalContent(
        string terminalText,
        string repoPath);
}
```

### 5.7 Voice Mode

**VoiceModeController.cs** - Orchestrates voice interaction flow

```
State Machine:
Idle -> Recording -> Transcribing -> SendingToSession ->
WaitingForResponse -> SummarizingResponse -> Speaking -> Idle
```

**Services:**

| Service | Purpose |
|---------|---------|
| `WhisperSttService` | Local speech-to-text (Whisper.net) |
| `OpenAiSttService` | Cloud speech-to-text (OpenAI API) |
| `WhisperLocalStreamingService` | Streaming transcription |
| `OpenAiTtsService` | Text-to-speech (OpenAI API) |
| `PiperTtsService` | Local TTS (Piper) |
| `ClaudeSummarizer` | Summarizes Claude's response for speaking |

### 5.8 Logging

**FileLog.cs** - Thread-safe file logging

```csharp
public static class FileLog
{
    // Location: %LOCALAPPDATA%\CcDirector\logs\director-YYYY-MM-DD-{PID}.log

    public static void Start();
    public static void Write(string message);  // Thread-safe, async queue
    public static void Stop();
}
```

Log format:
```
2026-02-21 14:32:15.123 [SessionManager] CreateSession: repoPath=D:\Repos\myproject
2026-02-21 14:32:15.456 [SessionManager] Session created: id=abc123, pid=12345
```

---

## 6. External Dependencies

### NuGet Packages

| Package | Version | Project | Purpose |
|---------|---------|---------|---------|
| **Whisper.net** | 1.* | Core | Local speech-to-text transcription |
| **Whisper.net.Runtime** | 1.* | Core | Native Whisper binaries |
| **NAudio** | 2.* | Wpf | Audio recording/playback for voice mode |
| **Microsoft.NET.Test.Sdk** | 17.* | Tests | Test framework support |
| **xunit** | 2.* | Tests | Unit testing framework |
| **xunit.runner.visualstudio** | 2.* | Tests | Visual Studio test runner |

### System Dependencies

| Dependency | Purpose |
|------------|---------|
| **.NET 10 Desktop Runtime** | Application runtime |
| **Windows Console Host (conhost.exe)** | Required for embedded console mode |
| **Claude Code CLI** | The `claude` executable must be on PATH |
| **Git** | For git status integration |
| **PowerShell** | For hook relay script execution |

### Native Windows APIs Used

| API | Purpose |
|-----|---------|
| `CreatePseudoConsole` | ConPTY creation |
| `ResizePseudoConsole` | Terminal resize |
| `CreateProcessW` | Process spawning with ConPTY |
| `SetParent` | Console window embedding |
| `MoveWindow` | Console positioning |
| `NamedPipeServerStream` | IPC with hooks |

---

## 7. Feature Inventory

### Implemented Features

| Feature | Description | Key Files |
|---------|-------------|-----------|
| **Multi-session management** | Run multiple Claude sessions side-by-side | `SessionManager.cs`, `MainWindow.xaml` |
| **Embedded console** | Native console overlay in WPF | `EmbeddedBackend.cs`, `EmbeddedConsoleHost.cs` |
| **Activity indicators** | Color-coded status (Idle/Working/Waiting) | `ActivityState.cs`, `Session.HandlePipeEvent()` |
| **Hook integration** | Captures 12 Claude Code event types | `HookInstaller.cs`, `DirectorPipeServer.cs` |
| **Session persistence** | Survives app restarts | `RecentSessionStore.cs`, `SessionStateStore.cs` |
| **Terminal verification** | Matches terminal to .jsonl files | `ClaudeSessionReader.cs` |
| **Git integration** | Shows staged/unstaged changes | `GitStatusProvider.cs`, Source Control tab |
| **Repository management** | Register, clone, initialize repos | `RepositoryRegistry.cs`, Repositories tab |
| **Voice mode** | Record/transcribe/send/speak | `VoiceModeController.cs`, Voice/ |
| **Pipe message logging** | Debug view of hook events | Pipe Messages tab |
| **File logging** | Daily logs to %LOCALAPPDATA% | `FileLog.cs` |
| **Drag-and-drop sessions** | Reorder sessions in sidebar | `MainWindow.xaml.cs` |
| **Custom session names/colors** | Personalize session display | `RenameSessionDialog.xaml` |

### Pending/Future Features

| Feature | Status | Notes |
|---------|--------|-------|
| Permission prompt UI | Planned | Handle `permission_request` hook events |
| Prompt history | Planned | Recall previously sent prompts |
| Subagent visualization | Planned | Show subagent tree |
| Tool execution display | Planned | Show tools as they execute |
| Task tracking | Planned | Track todos from Claude |

---

## 8. Data Flow & IPC

### Hook Event Flow

```
+------------------+     stdin JSON     +---------------------+
|  Claude Code     | -----------------> | hook-relay.ps1      |
|  (claude.exe)    |                    | (PowerShell script) |
+------------------+                    +---------------------+
                                                  |
                                                  | Named Pipe Write
                                                  v
                                        +---------------------+
                                        | DirectorPipeServer  |
                                        | \\.\pipe\CC_        |
                                        | ClaudeDirector      |
                                        +---------------------+
                                                  |
                                                  | OnMessageReceived
                                                  v
                                        +---------------------+
                                        | EventRouter         |
                                        | (session_id -> Guid)|
                                        +---------------------+
                                                  |
                                                  | HandlePipeEvent
                                                  v
                                        +---------------------+
                                        | Session             |
                                        | (state machine)     |
                                        +---------------------+
                                                  |
                                                  | PropertyChanged
                                                  v
                                        +---------------------+
                                        | WPF UI              |
                                        | (data binding)      |
                                        +---------------------+
```

### Session Lifecycle

```
User clicks "New Session"
         |
         v
+-------------------+
| SessionManager    |
| .CreateSession()  |
+-------------------+
         |
         v
+-------------------+       +-------------------+
| EmbeddedBackend   | ----> | conhost.exe       |
| .Start()          |       | + claude.exe      |
+-------------------+       +-------------------+
         |
         v
+-------------------+
| Session created   |
| Status: Starting  |
| Activity: Starting|
+-------------------+
         |
         | (Hook: SessionStart)
         v
+-------------------+
| SessionManager    |
| .RegisterClaude   |
| Session()         |
+-------------------+
         |
         | (Claude sends welcome)
         | (Hook: Stop)
         v
+-------------------+
| Activity: Idle    |
+-------------------+
         |
         | User sends prompt
         v
+-------------------+
| Activity: Working |
+-------------------+
         |
         | (Claude working...)
         | (Hook: PreToolUse, PostToolUse, etc.)
         | (Hook: Stop)
         v
+-------------------+
| Activity: Idle    |
+-------------------+
```

### Persistence Storage

| File | Location | Contents |
|------|----------|----------|
| `sessions.json` | `~/Documents/CcDirector/` | Active session state |
| `repositories.json` | `~/Documents/CcDirector/` | Registered repositories |
| `settings.json` | `~/.claude/` | Claude Code hooks config |
| `director-*.log` | `%LOCALAPPDATA%\CcDirector\logs\` | Daily log files |

---

## 9. Configuration

### appsettings.json

```json
{
  "Agent": {
    "ClaudePath": "claude",
    "DefaultBufferSizeBytes": 2097152,
    "GracefulShutdownTimeoutSeconds": 5
  },
  "Repositories": [
    { "Name": "my-project", "Path": "D:\\Repos\\my-project" }
  ]
}
```

### AgentOptions.cs

| Property | Default | Description |
|----------|---------|-------------|
| `ClaudePath` | `"claude"` | Path to claude executable |
| `DefaultClaudeArgs` | `"--dangerously-skip-permissions"` | Default CLI args |
| `DefaultBufferSizeBytes` | 2097152 (2 MB) | Terminal buffer size |
| `GracefulShutdownTimeoutSeconds` | 5 | Shutdown wait time |

### Environment Requirements

- `claude` must be on PATH (or absolute path in config)
- Default terminal must be Windows Console Host (not Windows Terminal)
- PowerShell must be available for hook relay

---

## 10. Testing Strategy

### Test Project

- **Framework:** xUnit 2.x
- **Location:** `src/CcDirector.Core.Tests/`
- **Coverage areas:** All Core library public methods

### Test Organization

| Test File | Coverage |
|-----------|----------|
| `ActivityStateTests.cs` | Hook event state transitions |
| `CircularTerminalBufferTests.cs` | Ring buffer operations, wraparound |
| `DirectorPipeServerTests.cs` | Named pipe message handling |
| `EventRouterTests.cs` | Event routing logic |
| `GitStatusProviderTests.cs` | Git status parsing |
| `GitSyncStatusProviderTests.cs` | Branch sync status |
| `HookInstallerTests.cs` | settings.json merge logic |
| `RepositoryRegistryTests.cs` | Repo registry persistence |
| `SessionManagerTests.cs` | Session creation, lifecycle |
| `SessionPersistenceTests.cs` | Save/load state |
| `SessionVerificationTests.cs` | Terminal matching |
| `TerminalVerificationIntegrationTests.cs` | End-to-end verification |
| `VoiceModeControllerTests.cs` | Voice flow orchestration |

### Test Patterns

```csharp
[Fact]
public void MethodName_Scenario_ExpectedResult()
{
    // Arrange
    var sut = new SystemUnderTest();

    // Act
    var result = sut.MethodUnderTest(input);

    // Assert
    Assert.Equal(expected, result);
}
```

### Running Tests

```bash
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj
```

---

## 11. Coding Standards

### Philosophy

1. **Enterprise Quality** - Production software, not prototype
2. **Fail Fast, Fail Loud** - Validate early, throw specific exceptions
3. **No Fallbacks** - Fix root causes, don't add workarounds
4. **Responsive UI** - Immediate feedback for every action
5. **Test Everything** - Every feature needs unit tests
6. **Zero Warnings** - Treat warnings as errors

### Error Handling Rules

**Try-catch ONLY at boundaries:**
- Event handlers (`Button_Click`)
- Lifecycle methods (`Loaded`, `OnStartup`)
- Timer callbacks
- External event subscriptions

**Never in:**
- Helper methods
- Service layer methods
- Business logic

### Null Safety

- **Null-forgiving operator (`!`) is FORBIDDEN**
- Use explicit null checks with `throw`
- Use pattern matching: `if (x is null)`
- In tests: use `Assert.NotNull()`

### Logging Requirements

```csharp
public void PublicMethod(string param)
{
    FileLog.Write($"[ClassName] MethodName: param={param}");
    try
    {
        // work
        FileLog.Write($"[ClassName] MethodName completed: result={result}");
    }
    catch (Exception ex)
    {
        FileLog.Write($"[ClassName] MethodName FAILED: {ex.Message}");
        throw;
    }
}
```

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes | PascalCase + suffix | `SessionManager`, `ConPtyBackend` |
| Interfaces | IPascalCase | `ISessionBackend` |
| Methods | PascalCase, Verb+Noun | `CreateSession()`, `SendTextAsync()` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Async methods | Suffix Async | `KillSessionAsync()` |
| Tests | Method_Scenario_Result | `CreateSession_InvalidPath_Throws` |

### Project Configuration

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

---

## 12. Security Considerations

### Process Execution

- All paths validated before `Process.Start`
- No shell command construction from user input
- Process arguments passed as arrays, not concatenated strings

### Sensitive Data

- No API keys in source code
- Credentials read from environment variables
- Logs truncate file contents (~100 chars)
- Never log secrets (tokens, passwords)

### IPC Security

- Named pipe accessible only on local machine
- No network-exposed endpoints
- Pipe messages validated before processing

### File System

- Session state stored in user documents folder
- Logs stored in %LOCALAPPDATA%
- No privileged file system access required

---

## 13. Known Constraints

### Platform Constraints

| Constraint | Reason |
|------------|--------|
| **Windows only** | Uses ConPTY, Win32 window management |
| **Windows Console Host required** | Embedded mode needs conhost.exe, not Windows Terminal |
| **.NET 10 required** | Target framework |

### Claude Code Constraints

| Constraint | Reason |
|------------|--------|
| **Hooks snapshot at session start** | Existing sessions won't pick up settings.json changes |
| **Claude Code must be on PATH** | Or absolute path in config |
| **--resume flag behavior** | Required for session persistence |

### Technical Constraints

| Constraint | Reason |
|------------|--------|
| **Single instance per repo** | Multiple sessions on same repo would conflict |
| **2 MB terminal buffer** | Fixed size ring buffer |
| **Hook relay latency** | ~200ms PowerShell startup time |

---

## 14. Review Focus Areas

### High-Priority Areas

1. **Hook System (`Hooks/`, `Pipes/`)**
   - HookInstaller JSON merge logic
   - DirectorPipeServer concurrent connection handling
   - EventRouter session mapping
   - State machine transitions in `Session.HandlePipeEvent()`

2. **Session Verification (`Claude/`)**
   - Terminal content matching algorithm
   - .jsonl file parsing
   - Edge cases (word wrapping, normalization)

3. **ConPTY Integration (`ConPty/`)**
   - Native API P/Invoke correctness
   - Process lifecycle management
   - Terminal resize handling

4. **Thread Safety**
   - `CircularTerminalBuffer` locking
   - `ConcurrentDictionary` usage
   - UI thread dispatch (`Dispatcher.BeginInvoke`)
   - `ObservableCollection` modifications

5. **Error Handling**
   - Exception boundaries (UI entry points only)
   - Logging coverage
   - User-facing error messages

### Medium-Priority Areas

1. **Voice Mode (`Voice/`)**
   - State machine transitions
   - Audio resource cleanup
   - Streaming transcription

2. **Git Integration (`Git/`)**
   - Async process execution
   - Status parsing edge cases

3. **Session Persistence**
   - Save/load round-trip
   - State recovery after crash

### Code Quality Checks

- [ ] No `null!` (null-forgiving operator)
- [ ] No `.Result` or `.Wait()` (deadlock risk)
- [ ] No try-catch in helper methods
- [ ] All public methods logged
- [ ] All public methods have tests
- [ ] No compiler warnings
- [ ] No fallback logic (try X, fallback to Y)

---

## Appendix A: File Locations Reference

| Purpose | Location |
|---------|----------|
| Application logs | `%LOCALAPPDATA%\CcDirector\logs\` |
| Session state | `~/Documents/CcDirector/sessions.json` |
| Repository registry | `~/Documents/CcDirector/repositories.json` |
| Claude hooks config | `~/.claude/settings.json` |
| Claude transcripts | `~/.claude/projects/{folder}/{session-id}.jsonl` |
| Claude sessions index | `~/.claude/sessions-index.json` |

## Appendix B: Key Type Hierarchy

```
ISessionBackend (interface)
    +-- ConPtyBackend
    +-- EmbeddedBackend
    +-- PipeBackend

Session
    +-- uses ISessionBackend
    +-- has ActivityState
    +-- has SessionStatus

SessionManager
    +-- manages Collection<Session>
    +-- maps claudeSessionId -> Session

DirectorPipeServer
    +-- receives PipeMessage
    +-- fires OnMessageReceived

EventRouter
    +-- routes PipeMessage to Session
    +-- uses SessionManager

HookInstaller
    +-- modifies ~/.claude/settings.json
    +-- installs PowerShell relay
```

## Appendix C: Build & Run Commands

```bash
# Build
dotnet build src/CcDirector.Wpf/CcDirector.Wpf.csproj

# Run
dotnet run --project src/CcDirector.Wpf/CcDirector.Wpf.csproj

# Test
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj

# Publish single-file executable
dotnet publish src/CcDirector.Wpf/CcDirector.Wpf.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

---

*Document generated for CC Director v1.1.0 code review*
