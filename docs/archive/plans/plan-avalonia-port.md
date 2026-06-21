# Plan: Port UI to Avalonia (Cross-Platform)

## Context

WPF is Windows-only. To run CC Director on Mac, we replace WPF with Avalonia -- a cross-platform .NET UI framework that uses nearly identical XAML syntax. The Core library already has `UnixPtyBackend` implemented and `SessionManager` already switches backends by platform. The UI layer is the only gap.

## Scope

**49 XAML files total:**
- 41 in CcDirector.Wpf (main app)
- 8 in CcDirectorSetup (setup wizard)

**3 Windows-only NuGet packages to replace:**
- `AvalonEdit` -> `AvaloniaEdit` (drop-in Avalonia port exists)
- `WebView2` -> `Avalonia.HtmlRenderer` or CEF-based solution
- `NAudio` -> cross-platform audio lib or feature-flag it out on Mac

**Projects that need `net10.0-windows` -> `net10.0` (multi-target):**
- CcDirector.Core
- CcDirector.Engine
- CcDirector.Core.Tests / Engine.Tests
- CcDirector.CliExplorer

**Projects that stay Windows-only (no Mac equivalent):**
- cc-click, cc-trisight, cc-computer (FlaUI / Windows UI Automation)

## Step-by-Step

### Step 1: Multi-target the Core libraries

- Change CcDirector.Core.csproj: `<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>`
- Wrap ConPty code in `#if WINDOWS` conditional compilation
- UnixPty code is already guarded by runtime checks
- Verify Whisper.net works cross-platform (if not, conditionally exclude)
- Same for CcDirector.Engine (no Windows-specific code, just remove `-windows`)
- Update test projects to `net10.0`

### Step 2: Create Avalonia project structure

- Add new project: `src/CcDirector.Avalonia/CcDirector.Avalonia.csproj`
  - Target: `net10.0`
  - References: CcDirector.Core, CcDirector.Engine
  - NuGet: Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, AvaloniaEdit
- Add new project: `tools/cc-director-setup-avalonia/`
  - Target: `net10.0`
  - Self-contained for both `osx-arm64` and `win-x64`

### Step 3: Port App.xaml (global styles)

- Convert WPF namespace `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` to Avalonia namespace `xmlns="https://github.com/avaloniaui"`
- Port dark theme brushes, button styles, scrollbar styles (mostly 1:1)
- Avalonia uses `Styles` instead of WPF `Style` in some cases

### Step 4: Port Setup Wizard (8 XAML files -- do this first as proof of concept)

- MainWindow.xaml + 6 step views + App.xaml
- Minimal dependencies (no AvalonEdit, no WebView2)
- Port Services/ -- most are pure C# (HttpClient, file I/O)
- Replace `ShortcutCreator.cs` with no-op on Mac
- Replace `PathManager.cs` to write `~/.zshrc` on Mac instead of Windows Registry
- Replace `PrerequisiteChecker.cs` to use `which` instead of `where`

### Step 5: Port Main App (41 XAML files)

- Start with MainWindow.xaml (layout shell)
- Port dialogs one at a time (they're independent)
- Replace AvalonEdit usages with AvaloniaEdit
- Replace WebView2 with Avalonia HTML renderer for markdown preview
- Replace `Dispatcher.BeginInvoke` with `Dispatcher.UIThread.Post`
- Port ObservableCollection bindings (mostly identical syntax)

### Step 6: Platform-specific abstractions

- Create `IPlatformService` interface for:
  - PATH management (Registry vs .zshrc)
  - App data directory (`%LOCALAPPDATA%` vs `~/.local/share`)
  - Process launching differences
  - Shortcut creation (Windows only)
- Implement `WindowsPlatformService` and `MacPlatformService`

### Step 7: Conditional features

- Voice/Vosk STT -- verify Mac support or disable on Mac
- NAudio -- replace or disable audio features on Mac
- cc-click/cc-trisight/cc-computer -- exclude from Mac builds entirely
- WebView2 markdown preview -- use Avalonia HTML renderer on Mac

## Key Files to Modify

- `src/CcDirector.Core/CcDirector.Core.csproj` -- multi-target
- `src/CcDirector.Engine/CcDirector.Engine.csproj` -- remove -windows
- `cc-director.sln` -- add Avalonia projects
- All 49 XAML files -- namespace + minor syntax changes
- `tools/cc-director-setup/Services/PathManager.cs` -- cross-platform
- `tools/cc-director-setup/Services/PrerequisiteChecker.cs` -- cross-platform
- `tools/cc-director-setup/Services/ShortcutCreator.cs` -- conditional

## Decision Point: Shared Source vs Separate Projects

**Option A (Recommended):** Keep WPF project as-is for Windows, create separate Avalonia project for cross-platform. Share code-behind logic via shared project or linked files. This avoids breaking the working Windows build.

**Option B:** Replace WPF with Avalonia entirely (Avalonia runs on Windows too). Simpler long-term but riskier -- Avalonia on Windows won't be pixel-identical to current WPF look.

## Verification

- Build Avalonia setup wizard for `osx-arm64` and `win-x64`
- Run setup wizard on Mac (or macOS VM) -- verify all 5 steps work
- Build Avalonia main app for both platforms
- Verify terminal session works on Mac (UnixPtyBackend)
- Verify all dialogs render correctly
- Run existing test suite (should pass unchanged)
