---
name: ui-test
description: Interact with and test the CC Director WPF app using the cc-click CLI tool. Use this to click buttons, read text, take screenshots, list elements, right-click session tabs, and navigate the UI.
disable-model-invocation: true
argument-hint: [action or test description]
---

# CC Director UI Automation

Interact with the CC Director WPF application using the `cc-click` CLI tool (located at `tools/cc-click/src/CcClick`). Use this skill to test UI interactions, take screenshots, and verify app behavior.

## Input: $ARGUMENTS

## The cc-click CLI Tool

Run all commands from the repo root via:
```
dotnet run --project tools/cc-click/src/CcClick -- <command> [options]
```

### Available Commands

| Command | Description |
|---------|-------------|
| `list-windows` | List visible top-level windows |
| `list-elements` | List UI elements in a window |
| `click` | Click (or right-click) a UI element |
| `type` | Type text into a UI element |
| `screenshot` | Capture a screenshot |
| `read-text` | Read text content of a UI element |

### Common Options

- `--window "CC Director"` / `-w` — Target the CC Director window
- `--name "Button Text"` — Find element by display text
- `--id "AutomationId"` — Find element by AutomationId
- `--type Button` / `-t` — Filter by ControlType (Button, TextBox, ListItem, TabItem, MenuItem, Text, Edit, etc.)
- `--depth N` / `-d` — Max tree traversal depth (default 25)
- `--right` / `-r` — Right-click instead of left-click
- `--xy "x,y"` — Click at absolute screen coordinates
- `--output path.png` / `-o` — Screenshot output path

### Output

All commands output **JSON to stdout**. Errors go to **stderr as JSON** with exit code 1.

## CC Director UI Map

### Main Window Buttons (by AutomationId)

| Button | AutomationId | Name |
|--------|-------------|------|
| New Session | `BtnNewSession` | `+ New Session` |
| Kill Session | `BtnKillSession` | `Kill Session` |
| Reconnect | `BtnReconnect` | `Reconnect` |
| Open Logs | `BtnOpenLogs` | `Open Logs` |
| Send (prompt) | — | `Send` |
| Refresh | — | `↻` |
| Pipe Toggle | `PipeToggleButton` | `«` |

### Tabs (by Name)

| Tab | ControlType |
|-----|-------------|
| Terminal | TabItem |
| Source Control | TabItem |

### Other Key Elements

| Element | AutomationId | ControlType | Notes |
|---------|-------------|-------------|-------|
| Session list | `SessionList` | List | Contains session ListItems |
| Session tab | — | ListItem | Name is `CcDirector.Wpf.SessionViewModel`; children have session name + status as Text elements |
| Prompt input | `PromptInput` | Edit | Text input at bottom of window |
| Tab container | `SessionTabs` | Tab | Contains Terminal and Source Control tabs |

### Session Tab Context Menu (right-click a ListItem)

Right-clicking a session tab opens a context menu with these MenuItems:
- **Rename** — Rename the session
- **Open in Explorer** — Open working directory in Explorer
- **Open in VS Code** — Open working directory in VS Code
- **Close Session** — Close/remove the session

### New Session Dialog

Clicking `+ New Session` opens a modal dialog. It shows:
- Recent sessions list
- Repository list to select from
- Browse button for custom path
- Create / Cancel buttons

The dialog is a **modal child** of CC Director (not a separate top-level window).

## Instructions

1. **Parse the user's request** from `$ARGUMENTS` to determine what UI action(s) to perform.

2. **Ensure CC Director is running:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- list-windows --filter "CC Director"
   ```
   If not found, tell the user to launch it first.

3. **Perform the requested action(s).** Common patterns:

   **Click a button:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- click --window "CC Director" --id "BtnNewSession"
   ```

   **Right-click a session tab** (use coordinates from list-elements):
   ```
   dotnet run --project tools/cc-click/src/CcClick -- list-elements --window "CC Director" --type ListItem
   # Then right-click using the center of the bounding rect:
   dotnet run --project tools/cc-click/src/CcClick -- click --xy "130,80" --right
   ```

   **Click a context menu item** (after right-clicking):
   ```
   dotnet run --project tools/cc-click/src/CcClick -- click --window "CC Director" --name "Rename"
   ```

   **Type into the prompt input:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- type --window "CC Director" --id "PromptInput" --text "hello world"
   ```

   **Switch tabs:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- click --window "CC Director" --name "Source Control"
   ```

   **Read element text:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- read-text --window "CC Director" --name "SESSIONS"
   ```

   **Take a screenshot:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- screenshot --window "CC Director" --output tmp_build_output/shot.png
   ```
   For context menus or popups, use a **fullscreen** screenshot (omit `--window`):
   ```
   dotnet run --project tools/cc-click/src/CcClick -- screenshot --output tmp_build_output/shot.png
   ```

   **Discover elements dynamically:**
   ```
   dotnet run --project tools/cc-click/src/CcClick -- list-elements --window "CC Director" --type Button
   dotnet run --project tools/cc-click/src/CcClick -- list-elements --window "CC Director" --depth 4
   ```

   **Close CC Director gracefully** (click the window Close button):
   ```
   dotnet run --project tools/cc-click/src/CcClick -- click --window "CC Director" --id "Close"
   ```
   Verify it's gone:
   ```
   dotnet run --project tools/cc-click/src/CcClick -- list-windows --filter "CC Director"
   ```

   **Build and start CC Director:**
   ```
   dotnet build src/CcDirector.Wpf -c Debug
   cmd.exe //c start "" "src\CcDirector.Wpf\bin\Debug\net10.0-windows\win-x64\cc-director.exe"
   ```
   Wait for startup then verify:
   ```
   sleep 2
   dotnet run --project tools/cc-click/src/CcClick -- list-windows --filter "CC Director"
   ```

   **Full restart cycle** (close, build, launch, verify):
   ```
   dotnet run --project tools/cc-click/src/CcClick -- click --window "CC Director" --id "Close"
   sleep 1
   dotnet build src/CcDirector.Wpf -c Debug
   cmd.exe //c start "" "src\CcDirector.Wpf\bin\Debug\net10.0-windows\win-x64\cc-director.exe"
   sleep 2
   dotnet run --project tools/cc-click/src/CcClick -- list-windows --filter "CC Director"
   ```

4. **Take a screenshot** after the action to show the result. Use the Read tool on the PNG to display it to the user.

5. **Clean up** screenshots from `tmp_build_output/` when done if they're no longer needed.

## Tips

- Session ListItems all share the same name (`CcDirector.Wpf.SessionViewModel`). To target a specific session, use `list-elements --type ListItem` to get bounding rects, then `click --xy "x,y"` with the center of the desired item.
- Context menus disappear when the window loses focus. Use fullscreen screenshots (no `--window`) to capture them, and take the screenshot immediately after the right-click.
- The New Session dialog is modal. After opening it, elements can be found within the CC Director window tree.
- Add `sleep 0.5` between click and screenshot if the UI needs time to update.
- All screenshot paths should use `tmp_build_output/` to keep the repo clean (it's gitignored).
- **NEVER use `taskkill` to stop CC Director** — it kills the process ungracefully and orphans the embedded console windows. Always use the Close button: `click --window "CC Director" --id "Close"`. This lets the app shut down cleanly.
- After restarting CC Director, use "Reconnect" to re-adopt any running Claude sessions.
