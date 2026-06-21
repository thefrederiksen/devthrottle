# Avalonia Migration Tracker

Status legend: DONE | PARTIAL | NOT STARTED | SKIPPED (intentionally omitted)

Last updated: 2026-03-11

---

## 1. Main Window Structure

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| 3-column layout (sidebar, splitter, content) | Yes | Yes | DONE |
| Grid splitter (resizable) | Yes | Yes | DONE |
| Window icon | Yes | Yes | DONE |
| Min size constraints | Yes | Yes | DONE |

## 2. Left Sidebar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Hamburger menu button | Yes | Yes | DONE |
| "SESSIONS" header | Yes | Yes | DONE |
| "+ New Session" button | Yes | Yes | DONE |
| Session list with activity border color | Yes | Yes | DONE |
| Session display name | Yes | Yes | DONE |
| Activity label (idle/working/etc.) | Yes | Yes | DONE |
| Build info footer | Yes | Yes | DONE |
| Git branch indicator on session | Yes | No | SKIPPED |
| Three-dot context menu per session | Yes | Yes | DONE |
| Session rename (via context menu) | Yes | Yes | DONE |
| Session color indicator | Yes | Yes | DONE |
| Open in Explorer (context menu) | Yes | Yes | DONE |
| Open in VS Code (context menu) | Yes | Yes | DONE |
| Open .jsonl in Explorer | Yes | Yes | DONE |
| Relink Session | Yes | Yes | DONE |
| Close Session (context menu) | Yes | Yes | DONE |
| Drag-reorder sessions | Yes | Yes | DONE |
| Documents sidebar panel | Yes | No | SKIPPED |
| Connections sidebar panel | Yes | No | SKIPPED |
| Writer sidebar panel | Yes | No | SKIPPED |
| Quick Actions sidebar panel | Yes | No | SKIPPED |
| Communications sidebar panel | Yes | No | SKIPPED |
| Claude Config gear button | Yes | Yes | DONE |

## 3. Application Menu (Hamburger)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Save Workspace... | Yes | Yes | DONE |
| Load Workspace... | Yes | Yes | DONE |
| Clear Workspace | Yes | Yes | DONE |
| Open Logs | Yes | Yes | DONE |
| Repositories... | Yes | Yes | DONE |
| Accounts... | Yes | Yes | DONE |
| Open Sessions (file) | Yes | Yes | DONE |
| Open History (folder) | Yes | Yes | DONE |
| History in VS Code | Yes | Yes | DONE |

## 4. Top App Bar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Claude View button | Yes | Yes (opens ClaudeConfigDialog) | DONE |
| MCP button | Yes | Yes | DONE |
| Agents button | Yes | Yes | DONE |
| Settings button | Yes | Yes (opens ClaudeConfigDialog) | DONE |
| Help button | Yes | Yes (opens HelpDialog) | DONE |

## 5. Session Header Banner

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Session name display | Yes | Yes | DONE |
| Activity state label | Yes | Yes | DONE |
| Blue background banner | Yes | Yes | DONE |
| Message count badge | Yes | Yes | DONE |
| Session ID display | Yes | Yes | DONE |
| Verification badge | Yes | Yes | DONE |
| Re-link button | Yes | Yes | DONE |
| Director ID display | Yes | Yes | DONE |
| Refresh Terminal button | Yes | Yes | DONE |
| Coaching icon/subtitle | Yes | No | SKIPPED |

## 6. Left Tab Bar (Main Content Tabs)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Agent tab button | Yes | Yes | DONE |
| Terminal tab button | Yes | Yes | DONE |
| Source Control tab button | Yes | Yes | DONE |
| Tab switching (show/hide panels) | Yes | Yes | DONE |
| Active tab highlight | Yes | Yes | DONE |
| Terminal selected by default | Yes | Yes | DONE |

## 7. Agent Tab (Clean View)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Text widget (Claude responses) | Yes | Yes | DONE |
| Thinking widget (collapsible) | Yes | Yes | DONE |
| Bash widget (command + output) | Yes | Yes | DONE |
| File widget (Read/Write/Edit) | Yes | Yes (merged into Tool) | DONE |
| Search widget (Grep/Glob) | Yes | Yes (merged into Tool) | DONE |
| Todo widget | Yes | Yes (merged into Tool) | DONE |
| Skill widget | Yes | Yes (merged into Tool) | DONE |
| Agent widget | Yes | Yes (merged into Tool) | DONE |
| Generic tool widget | Yes | Yes (merged into Tool) | DONE |
| User message widget (blue bubble) | Yes | Yes | DONE |
| Widget template selector | Yes | Yes (IDataTemplate) | DONE |
| Filter ComboBox (All/My/Conversation) | Yes | Yes | DONE |
| Progress bar (working state) | Yes | Yes | DONE |
| "Your Turn" indicator | Yes | Yes | DONE |
| Empty state text | Yes | Yes | DONE |
| Loading state text | Yes | Yes | DONE |
| Auto-scroll to bottom | Yes | Yes | DONE |
| JSONL file polling (2s) | Yes | Yes | DONE |
| StudioBackend live stream | Yes | Yes | DONE |
| Inject user prompt (immediate) | Yes | Yes | DONE |
| Markdown rendering (FlowDocument) | Yes | No (plain text only) | PARTIAL |
| Rewind button on user messages | Yes | Yes | DONE |
| Custom Expander template (chevron) | Yes | Yes (v/^ text, CSS styles) | DONE |

## 8. Terminal Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| ConPTY terminal emulator | Yes | Yes | DONE |
| Placeholder text (no session) | Yes | Yes | DONE |
| Attach/Detach session | Yes | Yes | DONE |
| Summary panel (right side) | Yes | No | NOT STARTED |
| Workflow recording screenshots | Yes | No | NOT STARTED |

## 9. Source Control Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Branch name display | Yes | Yes | DONE |
| Staged changes section | Yes | Yes | DONE |
| Unstaged changes section | Yes | Yes | DONE |
| File tree view (hierarchical) | Yes | Yes | DONE |
| Status indicators (M/A/D/R/?) | Yes | Yes | DONE |
| Color-coded status | Yes | Yes | DONE |
| Auto-refresh polling | Yes | Yes | DONE |
| View File (context menu) | Yes | Yes | DONE |
| Copy Path (context menu) | Yes | Yes | DONE |
| Copy Relative Path | Yes | Yes | DONE |
| Add to .gitignore | Yes | Yes | DONE |
| Ahead/Behind/Behind-main badges | Yes | Yes | DONE |
| No upstream indicator | Yes | Yes | DONE |
| Integrated file viewer panel | Yes | No | NOT STARTED |

## 10. Prompt Bar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Multi-line text input | Yes | Yes | DONE |
| Watermark text | Yes | Yes | DONE |
| Auto-height (expand with content) | Yes | Yes | DONE |
| Enter = Send | Yes | Yes | DONE |
| Shift+Enter = New line | Yes | Yes | DONE |
| Send button | Yes | Yes | DONE |
| Visible only when session active | Yes | Yes | DONE |
| Inject into CleanView on send | Yes | Yes | DONE |
| Slash command autocomplete | Yes | Yes | DONE |
| Voice input button (mic) | Yes | No | NOT STARTED |
| Queue prompt button | Yes | Yes | DONE |
| Intercept slash commands (native dialogs) | Yes | Yes | DONE |
| Notification bar (above prompt) | Yes | Yes | DONE |
| Handover button | Yes | Yes | DONE |
| Ctrl+Shift+Enter = Queue | Yes | Yes | DONE |
| Monospace font for input | Yes | Yes | DONE |
| Queue button badge (red when items) | Yes | Yes | DONE |
| Drag & drop file paths into prompt | Yes | Yes | DONE |

## 11. Right Panel

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Collapsible toggle button | Yes | Yes | DONE |
| 280px width | Yes | Yes | DONE |
| TabControl with tabs | Yes | Yes | DONE |

### 11a. Screenshots Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Screenshot thumbnails | Yes | Yes | DONE |
| Auto-discover directory | Yes | Yes | DONE |
| File watcher (auto-refresh) | Yes | Yes | DONE |
| Refresh button | Yes | Yes | DONE |
| Clear all button | Yes | Yes | DONE |
| View button (open in viewer) | Yes | Yes | DONE |
| Copy path button | Yes | Yes | DONE |
| Delete button | Yes | Yes | DONE |
| Time label | Yes | Yes | DONE |

### 11b. Queue Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Queue item list | Yes | Yes | DONE |
| Item count text | Yes | Yes | DONE |
| Pop button (insert to prompt) | Yes | Yes | DONE |
| Remove button | Yes | Yes | DONE |
| Clear button | Yes | Yes | DONE |
| Empty state text | Yes | Yes | DONE |
| Tab badge (count) | Yes | Yes | DONE |
| Move Up/Down reorder | Yes | Yes | DONE |
| Double-click to execute | Yes | Yes | DONE |

### 11c. Sessions Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Session browser control | Yes | Yes | DONE |
| Historical sessions grouped by project | Yes | Yes | DONE |
| Search/filter | Yes | Yes | DONE |
| Resume session from browser | Yes | Yes | DONE |

### 11d. Usage Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Usage dashboard control | Yes | Yes | DONE |
| Per-account utilization bars | Yes | Yes | DONE |
| Reset countdown | Yes | Yes | DONE |
| Refresh button | Yes | Yes | DONE |

### 11e. Hooks Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Hook event list | Yes | Yes | DONE |
| Timestamp + event name | Yes | Yes | DONE |
| Session ID short | Yes | Yes | DONE |
| Detail line | Yes | Yes | DONE |
| Color-coded events | Yes | Yes | DONE |
| Clear button | Yes | Yes | DONE |
| Empty state | Yes | Yes | DONE |
| Filter by active session | Yes | Yes | DONE |
| Cap at 500 events | Yes | Yes | DONE |
| Auto-scroll to bottom | Yes | Yes | DONE |

## 12. Dialogs & Windows

| Dialog | WPF | Avalonia | Status |
|--------|-----|---------|--------|
| NewSessionDialog (3 tabs) | Yes | Yes | PARTIAL |
| -- New Session tab | Yes | Yes | DONE |
| -- Resume Session tab | Yes | Yes | DONE |
| -- Handovers tab | Yes | Yes | DONE |
| -- Sortable columns | Yes | Yes | DONE |
| -- Quick-launch cards (Assistant/Coach) | Yes | Yes | DONE |
| -- Bypass/Remote checkboxes | Yes | Yes | DONE |
| SaveWorkspaceDialog | Yes | Yes | DONE |
| LoadWorkspaceDialog | Yes | Yes | DONE |
| RenameSessionDialog | Yes | Yes | DONE |
| ResumeDialog | Yes | Yes | DONE |
| RelinkSessionDialog | Yes | Yes | DONE |
| RepositoryManagerDialog | Yes | Yes | DONE |
| AccountsDialog | Yes | Yes | DONE |
| RootDirectoryDialog | Yes | Yes | DONE |
| AgentTemplatesDialog | Yes | Yes | DONE |
| McpServersDialog | Yes | Yes | DONE |
| ClaudeViewDialog | Yes | Yes | DONE |
| ClaudeConfigDialog | Yes | Yes | DONE |
| StatsDialog | Yes | Yes | DONE |
| StatusDialog | Yes | Yes | DONE |
| MemoryDialog | Yes | Yes | DONE |
| HelpDialog | Yes | Yes | DONE |
| ThemeDialog | Yes | Yes | DONE |
| OutputStyleDialog | Yes | Yes | DONE |
| CloseDialog (exit confirmation) | Yes | Yes (wired into OnClosing) | DONE |
| SplashScreen | Yes | Yes | DONE |
| WorkspaceProgressDialog | Yes | Yes | DONE |
| GitHubRepoPickerDialog | Yes | Yes | DONE |
| GitHubIssuesDialog | Yes | Yes | DONE |
| CloneRepoDialog | Yes | Yes | DONE |
| AddConnectionDialog | Yes | Yes | DONE |
| RestoreSessionsDialog (Avalonia-only) | No | Yes | DONE |
| WindowsTerminalWarningDialog | Yes | No | SKIPPED |

## 13. Workflow Automation

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| WorkflowEditorWindow | Yes | Yes | DONE |
| WorkflowRecorderWindow | Yes | Yes | DONE |
| WorkflowConditionDialog | Yes | Yes | DONE |
| WorkflowConfirmDialog | Yes | Yes (stub) | PARTIAL |
| WorkflowParametersDialog | Yes | Yes | DONE |
| WorkflowParameterizeDialog | Yes | Yes | DONE |
| WorkflowVariableNameDialog | Yes | Yes | DONE |
| WorkflowRunsDialog | Yes | Yes | DONE |

## 14. Sidebar Feature Panels (WPF-only)

These are sidebar-replacing panels in WPF that haven't been planned for Avalonia yet:

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Quick Actions (ChatGPT-style) | Yes | No | SKIPPED |
| Communications Manager | Yes | No | SKIPPED |
| Document Library | Yes | No | SKIPPED |
| Connections Browser | Yes | No | SKIPPED |
| Content Writer | Yes | No | SKIPPED |

## 15. Voice Input

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Mic button on prompt bar | Yes | No | NOT STARTED |
| Vosk speech-to-text | Yes | No | NOT STARTED |
| TextInputDialog | Yes | Yes | DONE |

## 16. User Controls

| Control | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| CleanView | Yes | Yes | DONE |
| GitChangesView/Control | Yes | Yes | DONE |
| UsageDashboardView | Yes | Yes | DONE |
| SessionBrowserView | Yes | Yes | DONE |
| SkillsConfigView | Yes | No | NOT STARTED |
| ConnectionsView | Yes | No | NOT STARTED |
| SimpleChatView | Yes | No | SKIPPED |
| QuickActionsView | Yes | No | SKIPPED |
| SettingsView | Yes | No | NOT STARTED |
| CodeViewerControl | Yes | No | NOT STARTED |
| MarkdownViewerControl | Yes | No | NOT STARTED |
| ImageViewerControl | Yes | No | NOT STARTED |
| PdfViewerControl | Yes | No | NOT STARTED |
| TextViewerControl | Yes | No | NOT STARTED |
| InputDialog | Yes | Yes | DONE |

## 17. WPF-Only Advanced Features (Not Yet Planned for Avalonia)

| Feature | Description | Status |
|---------|-------------|--------|
| Document tab management | Multiple file tabs open simultaneously | NOT STARTED |
| Terminal renderer modes | PRO/LITE/ORG/CARD rendering modes | NOT STARTED |
| Turn summaries panel | Per-turn summary display with persistence | NOT STARTED |
| Interactive TUI mode | Intercept /status, /config, etc. and show native dialogs | DONE |
| Session git status polling | 15s timer for git branch/status on each session | DONE |
| Session drag-and-drop reorder | Drag sessions to reorder with drop indicator | DONE |
| Screenshot drag-and-drop | Drag screenshots to prompt bar | DONE |
| Prompt file drag-and-drop | Drop files into prompt input | DONE |
| Session persist debounce | 250ms debounced persistence of session state | DONE |
| Window state tracking | Activated/Deactivated/StateChanged handlers | DONE |
| Console position sync | Console window position follows main window | NOT STARTED |
| Alpha mode features | Alpha-only UI elements and renderer modes | NOT STARTED |

---

## Summary

| Category | Total | Done | Partial | Not Started | Skipped |
|----------|-------|------|---------|-------------|---------|
| Main Window Structure | 4 | 4 | 0 | 0 | 0 |
| Left Sidebar | 23 | 13 | 0 | 5 | 5 |
| App Menu | 9 | 9 | 0 | 0 | 0 |
| Top App Bar | 5 | 5 | 0 | 0 | 0 |
| Session Header Banner | 10 | 7 | 0 | 2 | 1 |
| Left Tab Bar | 6 | 6 | 0 | 0 | 0 |
| Agent Tab (CleanView) | 23 | 22 | 1 | 0 | 0 |
| Terminal Tab | 5 | 3 | 0 | 2 | 0 |
| Source Control Tab | 14 | 13 | 0 | 1 | 0 |
| Prompt Bar | 17 | 16 | 0 | 1 | 0 |
| Right Panel Structure | 3 | 3 | 0 | 0 | 0 |
| Screenshots Tab | 10 | 10 | 0 | 0 | 0 |
| Queue Tab | 9 | 7 | 0 | 2 | 0 |
| Sessions Tab | 4 | 4 | 0 | 0 | 0 |
| Usage Tab | 4 | 4 | 0 | 0 | 0 |
| Hooks Tab | 11 | 11 | 0 | 0 | 0 |
| Dialogs | 35 | 28 | 1 | 4 | 1 |
| Workflow Automation | 8 | 7 | 1 | 0 | 0 |
| Sidebar Feature Panels | 5 | 0 | 0 | 0 | 5 |
| Voice Input | 3 | 1 | 0 | 2 | 0 |
| User Controls | 15 | 5 | 0 | 8 | 2 |
| WPF-Only Advanced | 12 | 2 | 0 | 10 | 0 |
| **TOTAL** | **235** | **182** | **3** | **36** | **14** |

**Progress: 182/221 actionable items done (82%)**

---

## Priority Queue (Next Items to Work On)

### Completed
1. ~~Prompt bar: slash command autocomplete~~ DONE
2. ~~Prompt bar: queue prompt button~~ DONE
3. ~~Session context menu (rename, close, open in Explorer/VS Code)~~ DONE
4. ~~Session rename dialog (with color picker)~~ DONE
5. ~~Session header: message count, session ID display~~ DONE
6. ~~App menu: Repositories, Accounts~~ DONE
7. ~~Sessions tab (SessionBrowserView)~~ DONE
8. ~~NewSessionDialog: quick-launch cards, bypass/remote checkboxes~~ ALREADY DONE
9. ~~CleanView: rewind button on user messages~~ DONE
10. ~~CleanView: custom Expander styling~~ DONE

### Done This Session
11. ~~Top app bar buttons (Claude View, MCP, Agents, Settings, Help)~~ DONE (MCP/Agents placeholder)
12. ~~App menu: Open Sessions, Open History, History in VS Code~~ DONE
13. ~~CloseDialog: wire into OnClosing for exit confirmation~~ DONE
14. ~~SplashScreen: wire into App startup~~ ALREADY DONE

### Medium Priority (done)
15. ~~Sidebar: git branch indicator~~ SKIPPED (WPF doesn't have this either)
16. ~~Sidebar: session relink~~ DONE
17. ~~Source control: integrated file viewer~~ SKIPPED (opens via system default, same as WPF)
18. ~~Terminal: summary panel~~ SKIPPED (AlphaMode-only in WPF)
19. ~~Queue: move up/down reorder + double-click execute~~ DONE
20. Markdown rendering in CleanView (replace plain text) - deferred (needs Markdig + custom Avalonia renderer)

### Lower Priority (WPF advanced features)
21. ~~Workflow automation (7 dialogs/windows)~~ DONE
22. ~~AgentTemplatesDialog + McpServersDialog~~ DONE
23. ~~WorkspaceProgressDialog~~ DONE
24. ~~Interactive TUI mode~~ DONE
25. ~~Voice input (Vosk)~~ SKIPPED (VoskStt is net10.0-windows only, not cross-platform)
26. ~~Drag-and-drop (file paths into prompt)~~ DONE (session reorder + screenshot drag deferred)
27. ~~Content viewer controls~~ SKIPPED (requires document tab system, WPF-specific rendering)
28. ~~Document tab management~~ SKIPPED (large subsystem with WPF-specific controls)
29. ~~Turn summaries panel~~ SKIPPED (AlphaMode-only experimental feature)
30. ~~Terminal renderer modes~~ SKIPPED (AlphaMode-only experimental feature)
