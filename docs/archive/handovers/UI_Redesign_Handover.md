# CC Director UI Redesign -- Handover Document

## What This Project Is

CC Director is a WPF desktop application that manages multiple Claude Code CLI sessions simultaneously. It provides a sidebar with session cards, an embedded terminal overlay (ConPTY-based), a prompt input bar, a queue/hooks inspector panel, and session lifecycle management (create, resume, kill, relink). It targets .NET 10 on Windows.

## What We Did: 7-Phase UI Redesign

We executed a complete UI redesign across 7 phases, transforming the app from a VS Code-inspired dark theme (hardcoded `#007ACC` blue, `#1E1E1E` backgrounds, system fonts) to a refined design system with tokenized colors, embedded fonts, custom window chrome, and consistent component styling.

The approved design spec driving all changes: `docs/CC_Director_WPF_Design_Spec.md`

### Phase 0: Style Guide (docs/VisualStyle.md)

Rewrote `docs/VisualStyle.md` from scratch as the single source of truth for all UI decisions. Contains 13 sections covering color tokens, typography, spacing, buttons, tabs, session cards, badges, icons, text inputs, scrollbars, dialogs, interaction states, and a code review checklist.

**CLAUDE.md** was updated to reference it: "UI changes must comply with `docs/VisualStyle.md`"

### Phase 1: Design Token Foundation

Created the resource dictionary infrastructure with zero visual change to existing UI:

| File | Purpose |
|------|---------|
| `Themes/ColorTokens.xaml` | 19 Color + SolidColorBrush pairs (backgrounds, borders, text, accents, special) |
| `Themes/Typography.xaml` | FontHeading (Inter) + FontMono (JetBrains Mono) font family resources |
| `Themes/Icons.xaml` | 13 Lucide SVG icon path data as string resources |
| `Fonts/*.ttf` | 8 embedded font files (Inter Regular/Medium/SemiBold/Bold + JetBrains Mono same 4 weights) |

**App.xaml** was rewritten to use `MergedDictionaries` loading all theme files. The `.csproj` was updated with `<Resource Include="Fonts\*.ttf" />` entries.

### Phase 2: Extract ViewModels

Extracted 4 ViewModels and 1 helper from the monolithic `MainWindow.xaml.cs` (3270 lines):

| File | Extracted From |
|------|---------------|
| `ViewModels/SessionViewModel.cs` | Session card data + activity state brushes |
| `ViewModels/QueueItemViewModel.cs` | Queue item DTO (Id, Index, Preview, FullText) |
| `ViewModels/HookEventViewModel.cs` | Hook event data + event type color brushes |
| `ViewModels/TurnSummaryViewModel.cs` | Turn summary DTO (Header, Summary) |
| `Helpers/DragDropAdorner.cs` | Drag-drop insertion line adorner |

XAML UserControl extraction (SidebarControl, SessionBarControl, etc.) was **deferred** due to 50+ cross-referenced named elements with deep code-behind coupling. The risk of breaking terminal overlay positioning (`TerminalArea.PointToScreen()`) and drag-drop was too high.

### Phase 3: Layout Restructure + Custom Title Bar

Complete rewrite of `MainWindow.xaml`. This was the highest-risk phase.

**Window chrome:**
- `WindowStyle="None"`, `ResizeMode="CanResize"`
- `WindowChrome` with `CaptionHeight="36"`, custom min/max/close buttons using Lucide icons
- Title bar: hamburger icon, "CC DIRECTOR" text (Inter 13px SemiBold), READ-ONLY badge

**New grid structure:**
```
Row 0: TitleBar (36px)
Row 1: Body (*) -> Sidebar(240) | Center(*) | Inspector(300)
Row 2: StatusBar (28px)
```

**Center panel inner grid:**
```
Row 0: App Bar (36px)
Row 1: Session Bar (Auto, collapses when no session)
Row 2: Tab Bar (36px)
Row 3: Terminal Area (*) -- TerminalArea Border stays here
Row 4: Notification Bar (28px)
Row 5: Prompt Input (120px)
```

**Added handlers** in `MainWindow.xaml.cs`: `BtnMinimize_Click`, `BtnMaximize_Click`, `BtnClose_Click`

Default window size changed from 1400x700 to 1440x900. `TextOptions.TextFormattingMode="Display"` and `TextOptions.TextRenderingMode="ClearType"` added at window level.

### Phase 4: Button + Badge Styles

Created reusable component style dictionaries:

| File | Styles |
|------|--------|
| `Themes/ButtonStyles.xaml` | PrimaryButton, SecondaryButton, IconButton, GhostButton |
| `Themes/BadgeStyles.xaml` | StatusBadge, CountBadge, IdTag, UncommittedBadge, ReadOnlyBadge |

All button styles include hover/press/disabled ControlTemplate triggers with 4px corner radius. Both files loaded via `App.xaml` MergedDictionaries.

### Phase 5: Notification Bar + Prompt Input

The notification bar and prompt input redesigns were incorporated directly into the Phase 3 MainWindow.xaml rewrite. The sidebar usage footer redesign with progress bars and a separate UsageViewModel was **not implemented** -- this is potential future work.

### Phase 6: Dialog Windows + Polish + Final Audit

Updated all 13 dialog XAML files and 1 UserControl to replace hardcoded hex colors with `{StaticResource ...}` references, apply `FontMono`/`FontHeading` fonts, and use `PrimaryButton`/`SecondaryButton`/`GhostButton` styles:

| Dialog | Key Changes |
|--------|-------------|
| CloseDialog.xaml | Background, text, progress bar, buttons |
| NewSessionDialog.xaml | Tab control, repo list, session list, checkboxes, buttons |
| RenameSessionDialog.xaml | Labels, text input, color swatches area, buttons |
| RestoreSessionsDialog.xaml | Progress bar, session list, checkboxes, buttons |
| AccountsDialog.xaml | Account cards, active/tier badges, action buttons |
| RelinkSessionDialog.xaml | Search box, session list items, message badges, buttons |
| RootDirectoryDialog.xaml | Form fields, Azure DevOps panel, provider combo, buttons |
| CloneRepoDialog.xaml | URL/destination inputs, browse buttons |
| GitHubRepoPickerDialog.xaml | Filter, repo list with ListBoxItem templates, status text |
| GitHubIssuesDialog.xaml | URL display textbox, copy/open buttons |
| RepositoryManagerDialog.xaml | Root panel, repo list, action bar, status bar, splitter |
| WindowsTerminalWarningDialog.xaml | Warning title, explanation text, buttons |
| Voice/TextInputDialog.xaml | Label, input textbox, send/cancel buttons |
| Controls/GitChangesControl.xaml | Source control header, branch bar, badges, tree styles |

**Legacy brush aliases removed** from `ColorTokens.xaml` after confirming zero remaining references: `PanelBackground`, `SidebarBackground`, `ButtonBackground`, `ButtonHover`, `TextForeground`, `AccentBrush`, `SelectedItemBrush`.

---

## Current State

### Build Status
**0 warnings, 0 errors** -- `dotnet build` passes clean.

### Git Status
- Branch: `main`
- 22 modified files (not staged, not committed)
- Several untracked directories: `Fonts/`, `Themes/`, `ViewModels/`, `docs/Screenshots/`, `docs/CC_Director_WPF_Design_Spec.md`
- Nothing has been committed yet -- all changes are local

### Remaining Hardcoded Colors (Intentional)

These colors are intentionally hardcoded and should NOT be tokenized:

| Location | Why |
|----------|-----|
| `Helpers/AnsiParser.cs` | Standard 16-color ANSI terminal palette |
| `Controls/TerminalControl.cs` | Terminal background rendering (Color.FromRgb(30,30,30)) |
| `ViewModels/SessionViewModel.cs` | Activity state indicator brushes (static readonly) |
| `ViewModels/HookEventViewModel.cs` | Hook event type color brushes (static readonly) |
| `Controls/GitChangesControl.xaml.cs` | Git status char colors (Modified=yellow, Added=green, etc.) |
| `Controls/GitChangesControl.xaml` | Git ahead/behind badge backgrounds (#1B3A2A, #3A2A1B) |
| `MainWindow.xaml` | Verification status dot DataTrigger colors |
| `App.xaml` | Scrollbar thumb hover/drag state colors |
| `AccountsDialog.xaml` | Remove button danger background (#5A1D1D) |
| `RestoreSessionsDialog.xaml.cs` | Progress status indicator colors |
| `GitHubRepoPickerDialog.xaml.cs` | Private/Public repo badge colors |
| `MainWindow.xaml.cs` | Verified/Warning badge brushes |
| `Helpers/DragDropAdorner.cs` | Drag insertion line color |

---

## File Structure (UI-Relevant)

```
src/CcDirector.Wpf/
  App.xaml                          # MergedDictionaries for all themes
  MainWindow.xaml                   # Main UI (custom chrome, 3-row layout)
  MainWindow.xaml.cs                # ~2850 lines code-behind (still monolithic)
  CcDirector.Wpf.csproj            # Font resources, .NET 10

  Themes/
    ColorTokens.xaml                # 19 color/brush pairs
    Typography.xaml                 # FontHeading (Inter), FontMono (JetBrains Mono)
    Icons.xaml                      # 13 Lucide icon SVG paths
    ButtonStyles.xaml               # PrimaryButton, SecondaryButton, IconButton, GhostButton
    BadgeStyles.xaml                # StatusBadge, CountBadge, IdTag, UncommittedBadge, ReadOnlyBadge

  Fonts/
    Inter-Regular.ttf               # Inter font family (4 weights)
    Inter-Medium.ttf
    Inter-SemiBold.ttf
    Inter-Bold.ttf
    JetBrainsMono-Regular.ttf       # JetBrains Mono font family (4 weights)
    JetBrainsMono-Medium.ttf
    JetBrainsMono-SemiBold.ttf
    JetBrainsMono-Bold.ttf

  ViewModels/
    SessionViewModel.cs             # Session card data + ActivityBrushes
    QueueItemViewModel.cs           # Queue item DTO
    HookEventViewModel.cs           # Hook event data + EventBrushes
    TurnSummaryViewModel.cs         # Turn summary DTO

  Helpers/
    AnsiParser.cs                   # Terminal ANSI escape code parser
    TerminalCell.cs                 # Terminal cell data structure
    DragDropAdorner.cs              # Drag-drop insertion line adorner

  Controls/
    GitChangesControl.xaml/.cs      # Source control changes panel
    TerminalControl.cs              # ConPTY terminal renderer

  # All 13 dialog files (updated with design tokens)
  AccountsDialog.xaml/.cs
  CloneRepoDialog.xaml/.cs
  CloseDialog.xaml/.cs
  GitHubIssuesDialog.xaml/.cs
  GitHubRepoPickerDialog.xaml/.cs
  NewSessionDialog.xaml/.cs
  RelinkSessionDialog.xaml/.cs
  RenameSessionDialog.xaml/.cs
  RepositoryManagerDialog.xaml/.cs
  RestoreSessionsDialog.xaml/.cs
  RootDirectoryDialog.xaml/.cs
  WindowsTerminalWarningDialog.xaml/.cs
  Voice/TextInputDialog.xaml/.cs
```

---

## Key Design Decisions

1. **WindowStyle="None"** -- Custom title bar required WindowChrome. The existing `WndProc` hook for `WM_NCACTIVATE` may need attention if window activation visual issues appear.

2. **TerminalArea must stay in MainWindow** -- The `TerminalArea` Border is used for `PointToScreen()` calculations to position the terminal overlay window. It cannot be moved into a UserControl without breaking positioning.

3. **UserControl extraction deferred** -- The XAML UserControl extraction planned in Phase 2 (SidebarControl, SessionBarControl, InspectorControl, PromptBarControl) was not done because of tight coupling with 50+ named elements and event handlers in code-behind. This is the biggest remaining refactoring opportunity.

4. **Usage footer progress bars not implemented** -- Phase 5 planned a sidebar usage footer redesign with 3px progress bars, XAML-bound layout, and a `UsageViewModel`. The current implementation still uses the procedural `CreateUsageBadge()` approach.

5. **Static readonly brushes in C# are acceptable** -- Activity state colors, git status colors, and hook event colors are defined as `static readonly SolidColorBrush` fields in their respective ViewModels/controls. These are semantic colors tied to data state, not UI theme colors, so they don't need to be in ResourceDictionary.

---

## What Has NOT Been Done Yet

1. **Nothing committed** -- All changes are unstaged local modifications. The user needs to review and commit.

2. **UserControl extraction** -- MainWindow.xaml is still ~968 lines. Extracting Sidebar, SessionBar, Inspector, and PromptBar into UserControls would improve maintainability but requires careful handling of named element references.

3. **Usage footer redesign** -- Progress bars for 5h/7d usage, XAML-bound layout with `UsageViewModel` and `INotifyPropertyChanged`.

4. **Visual testing** -- The app has not been launched to verify the visual result. The build compiles clean but the actual rendering needs human verification against `docs/Screenshots/` and the design spec.

5. **Tab styles** -- A `Themes/TabStyles.xaml` was mentioned in the plan but was not created as a separate file. Tab styling was done inline in MainWindow.xaml using DataTriggers on the tab items.

6. **Unit tests for ViewModels** -- The extracted ViewModels don't have unit tests yet. Per CLAUDE.md rule 5, all public methods need unit tests.

---

## Critical Things to Know

- **NEVER kill cc-director.exe** -- User runs multiple instances. If build fails due to locked files, tell the user.
- **NEVER commit without explicit permission** -- Per CLAUDE.md rules.
- **No Unicode/emoji anywhere** -- ASCII only per global CLAUDE.md rules.
- **No fallback programming** -- Fix root causes, don't add try/catch workarounds.
- **Enterprise logging required** -- Every public method must log entry/exit/errors via `FileLog.Write`.
- **UI thread safety** -- Always use `Dispatcher.BeginInvoke()` for ObservableCollection changes.
- **Design spec is the source of truth** -- `docs/CC_Director_WPF_Design_Spec.md` contains the complete pixel-accurate specification.
- **VisualStyle.md is the style guide** -- `docs/VisualStyle.md` is the developer-facing guide derived from the spec.

---

## Key Resource Mappings (Quick Reference)

| Old Hardcoded | New Token | Usage |
|---------------|-----------|-------|
| `#1E1E1E` | `BgPageBrush` | Page/window backgrounds |
| `#252526` | `BgSurfaceBrush` | Panels, dialogs, sidebar |
| `#2C313B` | `BgElevatedBrush` | Cards, elevated surfaces, inputs |
| `#3C3C3C` | `BorderBrush` | Borders, separators |
| `#007ACC` | `AccentBlueBrush` | Primary actions, links, active indicators |
| `#CCCCCC` | `TextPrimaryBrush` | Primary text |
| `#888888` | `TextSecondaryBrush` | Secondary/meta text |
| `#666666` | `TextMutedBrush` | Muted/placeholder text |
| `#22C55E` | `AccentGreenBrush` | Success, idle status |
| `#CC4444` | `AccentErrorBrush` | Errors, destructive actions |
| `#FFA500` | `AccentWarningBrush` | Warnings, notifications |
