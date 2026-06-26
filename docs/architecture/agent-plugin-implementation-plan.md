# Agent CLI Plugin Implementation Plan

## Goal

Move CLI-specific behavior out of central Director switches and into per-CLI plugins. A CLI plugin should own its driver, command-line presets, model settings, executable resolution, launch contract, slash commands, transcript support, and declared UI capabilities.

## High-Level Plan

1. Add a plugin contract in the core layer.
   - Define the stable surface every CLI plugin must expose.
   - Wrap the existing built-in CLIs with that contract first, without changing behavior.

2. Route discovery through the plugin registry.
   - Replace central catalog reads with plugin-provided settings metadata.
   - Keep the current built-in catalog as a compatibility source until each tool moves fully into its plugin.

3. Route settings through plugins.
   - The Agent Settings dialog should ask the selected plugin for presets, model support, defaults, and launch preview data.
   - Remove hard-coded settings assumptions from the dialog after the plugin path is proven.

4. Route launch through plugins.
   - Each plugin should build the launch spec for its CLI.
   - Existing `IAgent` implementations can be retired once plugin launch parity is verified.

5. Convert CLIs one at a time.
   - Start with Codex because the current gap is visible in history/context controls.
   - Then migrate Claude, Pi, Cursor, Copilot, Gemini, OpenCode, and Grok.
   - Keep Raw CLI as a special non-plugin terminal mode unless we later formalize it as a user-defined plugin.

6. Add external plugin loading.
   - Load CLI plugins from a plugin folder using a manifest plus a DLL.
   - Validate plugin identity, supported Director contract version, and declared capabilities before enabling the plugin.

7. Remove central CLI tables.
   - Delete or shrink `AgentToolCatalog`, `AgentDrivers.For`, tool detection switches, and config key switches after all built-ins are plugin-backed.
   - Leave only framework-level registry and compatibility migration code.

## Delivery Strategy

Use multiple issues or work items rather than one large branch. The first issue should establish the plugin contract and built-in registry. Subsequent issues should migrate settings, launch, and each CLI plugin independently so every step can be tested without breaking all CLI support at once.

## Adding a CLI After the Plugin Rollout

New built-in CLI tools must be added as concrete `IAgentPlugin` implementations. Do not add launch, settings, detection, history, presets, or driver selection through central switches as the primary path.

Minimum checklist:

- Add one concrete plugin class under `src/CcDirector.Core/AgentPlugins`.
- Put settings metadata, detection candidates, validation arguments, history declaration, presets, launch metadata, agent construction, and driver binding on that plugin.
- Add or update the tool-specific driver and slash-command catalog where needed.
- Register the plugin in `AgentPluginRegistry.BuildBuiltIns`.
- Add focused tests in `AgentPluginRegistryTests`.
- Keep UI ownership in Director views; plugins provide data/launch/driver behavior only.
- Add proof under `docs/cencon/proof/issue-<n>/` for live launch/catalog behavior when the CLI is available.

Architecture guard tests enforce the core rule:

- every `AgentToolCatalog` entry must resolve to a concrete built-in plugin class;
- production code may instantiate concrete CLI agents only inside `AgentPlugins`;
- the old generic `BuiltInAgentPlugin` adapter must stay deleted.
