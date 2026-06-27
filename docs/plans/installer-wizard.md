# CC Director Installer Wizard (WPF Application)

## Context

Running `cc-transcribe` failed because FFmpeg wasn't installed. This exposed a larger problem: the CC Director installation experience is fragmented -- users must manually install prerequisites, run the setup command, configure PATH, and hope they didn't miss anything. We need a proper installer wizard: a single self-contained C# WPF application that walks users through every prerequisite one screen at a time, checking what's already installed and helping install what's missing.

## What We're Building

A **self-contained WPF wizard application** (`cc-director-setup.exe`) that:
- Ships as a single exe (self-contained, no .NET needed to run it)
- Walks through each prerequisite one screen at a time
- Checks if each dependency is already installed
- For Claude Code: guides user to install it themselves (they must see/accept terms)
- For everything else: offers to install automatically via winget
- Downloads all cc-director tools from GitHub releases
- Configures PATH
- Shows a final summary of what's installed and what needs attention

## Project Structure

```
src/CcDirector.Setup/
  CcDirector.Setup.csproj
  App.xaml / App.xaml.cs
  MainWindow.xaml / MainWindow.xaml.cs
  ViewModels/
    SetupViewModel.cs          -- Main wizard state machine
  Models/
    PrerequisiteInfo.cs        -- Describes a single prerequisite
    PrerequisiteStatus.cs      -- Enum: NotChecked, Checking, Installed, Missing, Installing, Failed
    InstallResult.cs           -- Result of an install attempt
  Services/
    PrerequisiteChecker.cs     -- Checks if each prerequisite is installed
    WingetInstaller.cs         -- Installs packages via winget
    ToolDownloader.cs          -- Downloads cc-director tools from GitHub releases
    PathManager.cs             -- Adds directories to user PATH via registry
    GitHubApi.cs               -- Fetches release info from GitHub API
  Views/
    WelcomePage.xaml            -- Step 0: Welcome + what will be installed
    PrerequisitePage.xaml       -- Step 1-6: Reusable template for each prerequisite
    ToolsDownloadPage.xaml      -- Step 7: Download cc-director tools from GitHub
    ApiKeyPage.xaml             -- Step 8: Optional OpenAI API key
    SummaryPage.xaml            -- Step 9: Final status report
```

## Wizard Flow (Screens)

Each screen is large, clean, dark-themed (matching CC Director style). One prerequisite per screen.

### Screen 0: Welcome

- CC Director logo/title
- Brief description: "This wizard will install CC Director and all its tools"
- List of what will be checked/installed (preview)
- [Get Started] button

### Screen 1: Claude Code CLI (USER MUST INSTALL)

- **Why**: "Claude Code is the AI engine that powers CC Director"
- **Check**: Run `where claude` or `claude --version`
- **If installed**: Show version, green status, [Next] enabled
- **If not installed**:
  - Explain: "Claude Code requires you to accept Anthropic's terms of service"
  - Show link to install guide: https://docs.anthropic.com/en/docs/claude-code/overview
  - [Open Install Guide] button that opens browser
  - [Re-check] button to verify after user installs
  - [Next] stays disabled until installed (this is required)

### Screen 2: .NET 10 Desktop Runtime

- **Why**: "Required to run the CC Director desktop application"
- **Check**: `dotnet --list-runtimes` and look for `Microsoft.WindowsDesktop.App 10.x`
- **If installed**: Show version, green status
- **If not installed**: [Install via winget] button runs `winget install Microsoft.DotNet.DesktopRuntime.10`
- Show progress during install

### Screen 3: Node.js 18+

- **Why**: "Required for browser automation tools (cc-browser, cc-reddit)"
- **Check**: `node --version` and parse version >= 18
- **If installed**: Show version, green status
- **If not installed**: [Install via winget] button runs `winget install OpenJS.NodeJS.LTS`

### Screen 4: Git

- **Why**: "Required for repository management and cloning"
- **Check**: `git --version`
- **If installed**: Show version, green status
- **If not installed**: [Install via winget] button runs `winget install Git.Git`

### Screen 5: FFmpeg

- **Why**: "Required for audio/video transcription and processing (cc-transcribe, cc-video)"
- **Check**: `where ffmpeg` or `ffmpeg -version`
- **If installed**: Show version, green status
- **If not installed**: [Install via winget] button runs `winget install Gyan.FFmpeg`
- After install, add FFmpeg bin to PATH if needed

### Screen 6: Graphviz (Optional)

- **Why**: "Required for generating architecture diagrams (cc-docgen)"
- **Check**: `where dot` or `dot -V`
- **If installed**: Show version, green status
- **If not installed**: [Install via winget] button runs `winget install Graphviz.Graphviz`
- [Skip] button available (this is optional)

### Screen 7: Download CC Tools

- **Why**: "Downloading the CC Director command-line tools"
- Downloads all tool executables from GitHub releases to `%LOCALAPPDATA%\cc-director\bin\`
- Shows progress bar and per-tool status as each downloads
- Adds `%LOCALAPPDATA%\cc-director\bin\` to user PATH
- Installs Claude Code skill (SKILL.md)
- Full tool list (25+ tools)

### Screen 8: OpenAI API Key (Optional)

- **Why**: "Some tools use OpenAI for image generation, transcription, and text-to-speech"
- **Check**: Read `OPENAI_API_KEY` environment variable
- **If set**: Show masked key (sk-...xxxx), green status
- **If not set**:
  - Text input for API key
  - [Save] sets it as user environment variable
  - [Skip] button (can configure later)
- List which tools need it: cc-image, cc-voice, cc-whisper, cc-transcribe, cc-photos

### Screen 9: Summary

- Table showing every prerequisite with status (Installed / Missing / Skipped)
- Any warnings or items that need attention
- "Open a new terminal for PATH changes to take effect"
- [Launch CC Director] button (if main exe is available)
- [Close] button

## Key Design Decisions

### Self-Contained Build

The installer itself must be self-contained (bundles .NET runtime) so it runs without any prerequisites. It checks .NET for the *main app*, not for itself.

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

### No CcDirector.Core Reference

The installer should NOT reference CcDirector.Core -- it needs to be fully standalone with zero dependencies. Copy the few things needed (PATH management logic, GitHub API) directly into the project.

### winget for Package Installation

All automated installs use `winget` which is built into Windows 10/11. The installer runs winget as a child process and captures output to show progress.

### Window Size

Large window: approximately 900x650, centered on screen, not resizable. Dark theme matching CC Director (background #1E1E1E, text #CCCCCC, accent #007ACC).

### Each Screen Layout

Every prerequisite screen follows the same layout:

```
+----------------------------------------------------------+
|  [Step 3 of 9]                                           |
|                                                          |
|  [Icon]  Node.js 18+                                     |
|                                                          |
|  Required for browser automation tools                   |
|  (cc-browser, cc-reddit, cc-crawl4ai)                    |
|                                                          |
|  Status: [Checking...] / [Installed v22.5.0] / [Missing] |
|                                                          |
|  +----------------------------------------------------+  |
|  | Console output area showing check/install progress  |  |
|  |                                                     |  |
|  +----------------------------------------------------+  |
|                                                          |
|         [Install via winget]    [Skip]                   |
|                                                          |
|  [< Back]                              [Next >]         |
+----------------------------------------------------------+
```

## Files to Create

| File | Purpose |
|------|---------|
| `src/CcDirector.Setup/CcDirector.Setup.csproj` | Project file (self-contained WinExe, net10.0-windows) |
| `src/CcDirector.Setup/App.xaml` | App resources, dark theme brushes |
| `src/CcDirector.Setup/App.xaml.cs` | App startup |
| `src/CcDirector.Setup/MainWindow.xaml` | Wizard shell with navigation |
| `src/CcDirector.Setup/MainWindow.xaml.cs` | Window code-behind |
| `src/CcDirector.Setup/ViewModels/SetupViewModel.cs` | Wizard state, navigation, prerequisite logic |
| `src/CcDirector.Setup/Models/PrerequisiteInfo.cs` | Prerequisite data model |
| `src/CcDirector.Setup/Services/PrerequisiteChecker.cs` | Check if dependencies are installed |
| `src/CcDirector.Setup/Services/WingetInstaller.cs` | Run winget install commands |
| `src/CcDirector.Setup/Services/ToolDownloader.cs` | Download tools from GitHub releases |
| `src/CcDirector.Setup/Services/PathManager.cs` | User PATH management via registry |
| `src/CcDirector.Setup/Services/GitHubApi.cs` | GitHub API for release info |

## Files to Modify

| File | Change |
|------|--------|
| `cc-director.sln` | Add CcDirector.Setup project reference |
| `docs/public/getting-started/installation.md` | Reference the new installer wizard |

## Implementation Order

1. Create project structure and .csproj
2. Build App.xaml with dark theme resources
3. Build MainWindow.xaml wizard shell (navigation, content area)
4. Build SetupViewModel (state machine, step navigation)
5. Build PrerequisiteChecker service (all check commands)
6. Build WelcomePage
7. Build PrerequisitePage (reusable template for steps 1-6)
8. Build WingetInstaller service
9. Build ToolDownloader + GitHubApi services
10. Build ToolsDownloadPage
11. Build ApiKeyPage
12. Build SummaryPage
13. Build PathManager service
14. Add to solution, test end-to-end
15. Add build script for self-contained publish

## Verification

- Build and run: `dotnet run --project src/CcDirector.Setup/CcDirector.Setup.csproj`
- Walk through every screen
- Verify each prerequisite check works correctly
- Test install flow for a missing package (e.g., uninstall Graphviz, reinstall via wizard)
- Publish self-contained: verify single exe runs on clean machine
- Verify PATH changes take effect in new terminal
