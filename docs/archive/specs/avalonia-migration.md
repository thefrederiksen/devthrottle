# Avalonia Migration Plan

Tracking document for migrating CC Director from WPF to Avalonia.

**Goal:** Single cross-platform codebase (Windows + Mac) replacing WPF entirely.

---

## Phase 1: Core Shell (MVP - Run Sessions)

Get the app to a usable state: load sessions, show terminal, switch between them.

| # | Component | WPF Source | Avalonia Status | Notes |
|---|-----------|-----------|-----------------|-------|
| 1.1 | App icon | app.ico in Wpf/ | DONE | Copied ico, configured .csproj |
| 1.2 | Remove mutex/ReadOnlyMode | App.axaml.cs | DONE | Removed mutex, ReadOnlyMode, RestoredPersistedData |
| 1.3 | MainWindow shell | 1426 XAML, 5056 C# | DONE (MVP) | Sidebar + header + terminal + prompt bar |
| 1.4 | Session sidebar | Part of MainWindow | DONE | Session list with activity border, +New Session |
| 1.5 | Terminal tab | TerminalControl | DONE | Uses Terminal.Avalonia TerminalControl (Attach/Detach) |
| 1.6 | Session creation | CreateSession in MainWindow | DONE | Wired to SessionManager |
| 1.7 | App menu (hamburger) | Part of MainWindow | DONE (basic) | Save/Load/Clear Workspace, Open Logs |
| 1.8 | Prompt bar | Part of MainWindow | DONE | Send button, Enter to send, Shift+Enter for newline |
| 1.9 | Workspace load at startup | LoadWorkspaceDialog | DONE | Shows picker with Skip button at startup |
| 1.10 | Workspace save/load/clear | Menu handlers in MainWindow | DONE | Via app menu context menu |
| 1.11 | WorkspaceProgressDialog | 46 XAML, 33 C# | DEFERRED | Using inline loading for now |
| 1.12 | SplashScreen | 76 XAML, 18 C# | STUB (19 C#) | Low priority |
| 1.13 | NewSessionDialog | 465 XAML, 945 C# | DONE | Wired to +New Session button |
| 1.14 | CloseDialog | 80 XAML, 60 C# | DONE | Used in close flow |

**Exit criteria:** Can launch app, pick workspace, see sessions in sidebar, interact with terminal, send prompts.

---

## Phase 2: Essential Dialogs

Dialogs needed for daily use.

| # | Component | WPF Source | Avalonia Status | Notes |
|---|-----------|-----------|-----------------|-------|
| 2.1 | RenameSessionDialog | 57 XAML, 118 C# | PORTED (120 C#) | Already done |
| 2.2 | RepositoryManagerDialog | 339 XAML, 628 C# | PORTED (618 C#) | Already done |
| 2.3 | RootDirectoryDialog | 82 XAML, 133 C# | PORTED (138 C#) | Already done |
| 2.4 | AccountsDialog | 182 XAML, 631 C# | PORTED (635 C#) | Already done |
| 2.5 | ClaudeConfigDialog | 280 XAML, 436 C# | PORTED (458 C#) | Already done |
| 2.6 | SaveWorkspaceDialog | 108 XAML, 151 C# | PORTED (155 C#) | Already done |
| 2.7 | StatusDialog | 109 XAML, 112 C# | PORTED (111 C#) | Already done |
| 2.8 | HelpDialog | 279 XAML, 19 C# | STUB (20 C#) | Needs AXAML layout |
| 2.9 | ResumeDialog | 107 XAML, 174 C# | PORTED (176 C#) | Already done |
| 2.10 | RelinkSessionDialog | 124 XAML, 207 C# | PORTED (209 C#) | Already done |
| 2.11 | MemoryDialog | 126 XAML, 142 C# | PORTED (146 C#) | Already done |
| 2.12 | StatsDialog | 109 XAML, 132 C# | PORTED (135 C#) | Already done |
| 2.13 | OutputStyleDialog | 55 XAML, 95 C# | PORTED (98 C#) | Already done |
| 2.14 | ThemeDialog | 100 XAML, 150 C# | PORTED (154 C#) | Already done |
| 2.15 | InputDialog (control) | 52 XAML, 50 C# | PORTED (55 C#) | Already done |

**Exit criteria:** All management dialogs functional. Can configure repos, accounts, rename sessions, etc.

---

## Phase 3: Right Panel & Secondary Views

Inspector panel, screenshots, queue, hooks.

| # | Component | WPF Source | Avalonia Status | Notes |
|---|-----------|-----------|-----------------|-------|
| 3.1 | Right panel tabs | Part of MainWindow | NOT DONE | Screenshots, Queue, Sessions, Usage, Hooks |
| 3.2 | Screenshots panel | Part of MainWindow | NOT DONE | Thumbnail list, view/copy/delete |
| 3.3 | Queue panel | Part of MainWindow | NOT DONE | Queued prompts list |
| 3.4 | Hook events panel | Part of MainWindow | NOT DONE | Event log display |
| 3.5 | SessionBrowserView | 74 XAML, 332 C# | NOT DONE | Browse past sessions |
| 3.6 | UsageDashboardView | 47 XAML, 523 C# | NOT DONE | Usage stats |
| 3.7 | Notification bar | Part of MainWindow | NOT DONE | Info messages |
| 3.8 | Slash command autocomplete | Part of MainWindow | NOT DONE | Popup with command list |

**Exit criteria:** Full right panel working. Screenshots, queue, usage visible.

---

## Phase 4: Advanced Views & Controls

Agent view, source control, file viewers, connections.

| # | Component | WPF Source | Avalonia Status | Notes |
|---|-----------|-----------|-----------------|-------|
| 4.1 | CleanView (Agent tab) | 700 XAML, 459 C# | NOT DONE | Clean conversation UI |
| 4.2 | GitChangesControl | 314 XAML, 460 C# | NOT DONE | Source control tab |
| 4.3 | ConnectionsView | 152 XAML, 762 C# | NOT DONE | Browser connections |
| 4.4 | SettingsView | 63 XAML, 566 C# | NOT DONE | App settings |
| 4.5 | SkillsConfigView | 297 XAML, 809 C# | NOT DONE | Skills/tools config |
| 4.6 | QuickActionsView | 175 XAML, 414 C# | NOT DONE | Quick actions chat |
| 4.7 | SimpleChatView | 164 XAML, 241 C# | NOT DONE | Simple chat interface |
| 4.8 | CodeViewerControl | 123 XAML, 345 C# | NOT DONE | Syntax-highlighted code |
| 4.9 | TextViewerControl | 126 XAML, 237 C# | NOT DONE | Plain text viewer |
| 4.10 | MarkdownViewerControl | 133 XAML, 255 C# | NOT DONE | Markdown renderer |
| 4.11 | ImageViewerControl | 176 XAML, 242 C# | NOT DONE | Image display |
| 4.12 | PdfViewerControl | 69 XAML, 133 C# | NOT DONE | PDF viewer |

**Exit criteria:** All views and file viewers working.

---

## Phase 5: Remaining Dialogs & Polish

Workflow system, MCP, agents, misc dialogs.

| # | Component | WPF Source | Avalonia Status | Notes |
|---|-----------|-----------|-----------------|-------|
| 5.1 | McpServersDialog | 204 XAML, 408 C# | NOT DONE | MCP server management |
| 5.2 | AgentTemplatesDialog | 219 XAML, 366 C# | NOT DONE | Agent template editor |
| 5.3 | WorkflowRecorderWindow | 116 XAML, 710 C# | NOT DONE | Record/replay workflows |
| 5.4 | WorkflowConfirmDialog | 53 XAML, 23 C# | PORTED (24 C#) | Already done |
| 5.5 | WorkflowParametersDialog | 61 XAML, 65 C# | NOT DONE | Workflow params |
| 5.6 | WorkflowParameterizeDialog | 92 XAML, 131 C# | NOT DONE | Parameterize workflow |
| 5.7 | WorkflowRunsDialog | 129 XAML, 232 C# | NOT DONE | View workflow runs |
| 5.8 | WorkflowVariableNameDialog | 45 XAML, 38 C# | NOT DONE | Variable name input |
| 5.9 | CloneRepoDialog | 87 XAML, 103 C# | PORTED (106 C#) | Already done |
| 5.10 | AddConnectionDialog | 72 XAML, 79 C# | PORTED (81 C#) | Already done |
| 5.11 | GitHubIssuesDialog | 30 XAML, 28 C# | STUB (34 C#) | Needs layout |
| 5.12 | GitHubRepoPickerDialog | 161 XAML, 189 C# | PORTED (196 C#) | Already done |
| 5.13 | WindowsTerminalWarningDialog | 68 XAML, 75 C# | NOT DONE | Windows-only |
| 5.14 | Voice/TextInputDialog | 59 XAML, 63 C# | NOT DONE | Voice input |

**Exit criteria:** Full feature parity with WPF.

---

## Phase 6: Deprecate WPF

| # | Task | Status |
|---|------|--------|
| 6.1 | Remove CcDirector.Wpf from solution | NOT DONE |
| 6.2 | Remove CcDirector.Terminal (WPF-specific) | NOT DONE |
| 6.3 | Update CI/CD to build Avalonia only | NOT DONE |
| 6.4 | Update local build scripts (keep only Avalonia) | NOT DONE |
| 6.5 | Mac build + test | NOT DONE |
| 6.6 | Remove WPF-only dependencies (VoskStt, etc.) | NOT DONE |

---

## Stats Summary

| Phase | Items | Already Ported | Remaining |
|-------|-------|---------------|-----------|
| 1. Core Shell | 14 | 3 | 11 |
| 2. Essential Dialogs | 15 | 14 | 1 |
| 3. Right Panel | 8 | 0 | 8 |
| 4. Advanced Views | 12 | 0 | 12 |
| 5. Remaining Dialogs | 14 | 5 | 9 |
| 6. Deprecate WPF | 6 | 0 | 6 |
| **Total** | **69** | **22** | **47** |

---

## Terminal Strategy (Critical Path)

The terminal is the core feature and the hardest to port. Options:

**Option A: Process-based terminal (cross-platform)**
- Launch `claude` as a child process
- Capture stdout/stderr, pipe stdin
- Render output in a TextBlock/RichTextBlock with ANSI parsing
- Pros: Cross-platform, simpler
- Cons: No native terminal feel, must handle ANSI escape codes

**Option B: Avalonia.Terminal / xterm.js**
- Use a terminal emulator library
- CcDirector.Terminal.Avalonia already exists (11 files)
- Pros: Real terminal experience
- Cons: More complex, library maturity

**Option C: Embedded native terminal (Windows ConPTY / Mac PTY)**
- Similar to current WPF approach
- Platform-specific implementations behind interface
- Pros: Full native terminal
- Cons: Per-platform work

**Recommendation:** Start with whatever CcDirector.Terminal.Avalonia already has, evaluate from there.
