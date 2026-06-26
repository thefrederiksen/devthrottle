# Agent Plugin Rollout Plan

## Objective

Codex proved the target shape: one CLI integration should own its settings metadata, driver, launch behavior, slash commands, capability declarations, history integration, and QA evidence through a plugin. The next step is to migrate every built-in CLI to that model, then remove the old central tables and switch statements.

## Principles

- Migrate incrementally. Do not remove legacy paths until every built-in agent is plugin-backed and tested.
- Treat Codex as the reference implementation.
- Keep one CLI per issue after the shared migration work lands.
- Every issue must include tests and a QA report update.
- Use an isolated worktree and a session-owned slot for implementation and live QA.
- Keep settings, launch, controls, slash commands, and history in one plugin-owned surface.

## Target End State

- Each built-in CLI has a plugin class that owns:
  - Stable plugin id and config key.
  - Display name and type metadata.
  - Command-line presets.
  - Model selection metadata.
  - Driver instance and declared capabilities.
  - Launch strategy creation.
  - History/transcript support declaration.
  - Detection and validation metadata.
- `AgentToolCatalog` is removed or reduced to migration-only compatibility.
- `AgentDrivers.For` is removed or reduced to plugin-registry compatibility.
- Settings UI and Control API read agent metadata only from plugins.
- Session creation, session controls, slash commands, and history lookup resolve through plugins.
- External DLL plugin loading is supported after built-ins prove the contract.

## Workstreams

### 1. Strengthen The Plugin Contract

Codex currently proves the first contract, but the interface is still too shallow for deleting old code.

Add plugin-owned surfaces for:
- Detection candidates and install guidance.
- Validation command/version probe.
- Config serialization key.
- History provider metadata and transcript path lookup.
- Settings capabilities and UI hints.
- Agent factory and launch spec builder.

Exit criteria:
- Codex still passes.
- Compatibility adapters compile for all non-Codex agents.
- Tests prove all plugin metadata needed by Settings and Control API comes from the plugin contract.

### 2. Move Settings Completely To Plugins

The Settings dialog and `/settings/agents/catalog` already read part of the plugin metadata. Finish the move.

Migrate:
- Type option list.
- Display labels.
- Preset source.
- Model metadata.
- Detection eligibility.
- Validation status text.
- Add/edit defaults.
- Detection wizard suggestions.

Exit criteria:
- No Settings UI or Control API settings path reads `AgentToolCatalog` directly.
- Every built-in agent appears in settings from plugin metadata.
- Each agent has a screenshot in the QA report showing settings metadata.

### 3. Move Launch Completely To Plugins

Session creation now uses plugin-created agents in the main desktop and Control API paths, but older switches remain.

Migrate:
- Control API handoff/new-session creation.
- Desktop launch entry selection.
- SessionManager overloads.
- AgentEntry path override creation.
- Raw CLI special handling.

Exit criteria:
- No built-in launch path switches over `AgentKind` to construct an agent directly.
- Each built-in plugin can create its launch strategy.
- Tests prove launch args for every built-in agent.

### 4. Convert Built-In CLI Plugins One At A Time

Recommended order:

1. Claude Code
   - Richest existing driver.
   - Transcript and model support already mature.
   - Migrating it validates the full plugin contract.

2. Cursor
   - Dedicated driver exists.
   - Useful test for limited capabilities and stream-json session id behavior.

3. Copilot
   - Dedicated driver exists.
   - Useful test for preassigned session id without transcript-read support.

4. Pi
   - Dedicated driver exists.
   - Important because its interrupt behavior differs sharply from generic terminal assumptions.

5. Gemini
   - Currently generic driver plus terminal-history fallback.
   - Useful test for a thin plugin with terminal-buffer history.

6. OpenCode
   - Currently generic driver plus SQLite history reader.
   - Useful test for external persistent history store.

7. Grok
   - Currently generic driver plus transcript reader/locator.
   - Useful test for file-based history with limited controls.

For each CLI:
- Create a concrete plugin class.
- Move catalog entry data into that plugin.
- Move slash commands, model metadata, and capability metadata into that plugin.
- Ensure launch creation goes through the plugin.
- Add driver/plugin/config/settings tests.
- Add a QA report section with settings screenshot and live session screenshot.

### 5. External DLL Plugin Loading

After built-ins are plugin-backed, add external plugin loading.

Implement:
- Plugin manifest format.
- Plugin folder discovery.
- Assembly load context.
- Contract version compatibility.
- Safe failure reporting.
- Duplicate id protection.
- Settings display for external plugins.

Exit criteria:
- A sample test plugin can be loaded from disk.
- Bad plugin manifests fail visibly without taking down Director.
- External plugin appears in settings and can be disabled.

### 6. Remove Legacy Central Code

Only after all built-ins and external loading are verified:

Remove or shrink:
- `AgentToolCatalog`.
- `AgentDrivers.For`.
- Hard-coded type option lists.
- Hard-coded tool display-name switches.
- Hard-coded detection key switches where plugin metadata can replace them.
- Direct `new ClaudeAgent(...)`, `new PiAgent(...)`, etc. switch blocks.

Exit criteria:
- Plugin registry is the single authority for built-in and external CLIs.
- Tests fail if a built-in CLI is missing plugin metadata.
- QA report confirms every built-in CLI can be configured, launched, and controlled through the plugin path.

## QA Requirements

Every migration issue must include:
- Unit tests for plugin metadata.
- Unit tests for launch args.
- Unit tests for driver capabilities.
- Settings API test coverage.
- Avalonia build.
- Slot build.
- Live QA screenshot of agent settings.
- Live QA screenshot of a running session for that agent.
- QA report under `docs/qa/`.

## Suggested Implementation Environment

Use a new worktree and isolated slot:

```powershell
git worktree add D:\ReposFred\devthrottle-agent-plugins main
cd D:\ReposFred\devthrottle-agent-plugins
powershell -NoProfile -File scripts\agent-session-isolation.ps1 allocate -Worktree D:\ReposFred\devthrottle-agent-plugins
```

Build and launch through the allocated slot. Do not use the user's long-lived slots 1-4 for automated QA.

## Issue Breakdown

1. [#748](https://github.com/thefrederiksen/devthrottle/issues/748) - Strengthen plugin contract to own settings, detection, launch, and history metadata.
2. [#749](https://github.com/thefrederiksen/devthrottle/issues/749) - Move Settings dialog and settings Control API fully to plugin metadata.
3. [#750](https://github.com/thefrederiksen/devthrottle/issues/750) - Move desktop and Control API launch creation fully to plugins.
4. [#751](https://github.com/thefrederiksen/devthrottle/issues/751) - Convert Claude Code to a concrete plugin.
5. [#752](https://github.com/thefrederiksen/devthrottle/issues/752) - Convert Cursor to a concrete plugin.
6. [#753](https://github.com/thefrederiksen/devthrottle/issues/753) - Convert Copilot to a concrete plugin.
7. [#754](https://github.com/thefrederiksen/devthrottle/issues/754) - Convert Pi to a concrete plugin.
8. [#755](https://github.com/thefrederiksen/devthrottle/issues/755) - Convert Gemini to a concrete plugin.
9. [#756](https://github.com/thefrederiksen/devthrottle/issues/756) - Convert OpenCode to a concrete plugin.
10. [#757](https://github.com/thefrederiksen/devthrottle/issues/757) - Convert Grok to a concrete plugin.
11. [#758](https://github.com/thefrederiksen/devthrottle/issues/758) - Add external DLL plugin loading.
12. [#759](https://github.com/thefrederiksen/devthrottle/issues/759) - Remove legacy central catalog/driver switch code.
13. [#760](https://github.com/thefrederiksen/devthrottle/issues/760) - Produce final all-agents plugin QA report with screenshots.
