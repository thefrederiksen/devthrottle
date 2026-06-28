# Plan: cc-devthrottle Installation and Tool Healing

**Status:** Draft implementation plan  
**Date:** 2026-06-27  
**Related:** [Unified Install and Auto-Update](unified-install-and-update.md), [tool inventory report](../reviews/tool-inventory.html), [cc-devthrottle review](../reviews/cc-devthrottle-tool-review.html)

## Goal

Make a clean install, update, or repair automatically produce the correct `cc-devthrottle` command surface on another machine, without manually fixing this local shell.

The installation system should:

- Install the shipped tool set from one source of truth.
- Put `cc-devthrottle` on PATH reliably.
- Recreate missing or stale shims during update and repair.
- Avoid installing extra/research tools unless they are explicitly included in a profile.
- Keep legacy command names only as temporary compatibility wrappers, not as the primary surface.
- Prove the result with repeatable installer and CLI tests.

## Non-goals

- Do not fix the current machine by hand-editing `%LOCALAPPDATA%`, PATH, or local shims.
- Do not keep parallel standalone setup tools once `cc-devthrottle setup` owns that surface.
- Do not broaden into every Gateway or Cockpit API before the install/update/repair path is reliable.
- Do not install unregistered or experimental tools by accident.

## Current highest-risk issues

1. `cc-devthrottle` can be present in source and manifests but still missing from the live PATH after install or update.
2. Legacy aliases can mask the failure because `cc-send`, `cc-spawn`, `cc-sessions`, and similar wrappers may exist even when the unified command does not.
3. Installer/build code still has broad hard-coded tool lists, which can reinstall extra tools and drift away from the shipped product contract.
4. `cc-devthrottle actions --json` is static and incomplete, so agents cannot reliably discover the real command surface.
5. `cc-devthrottle setup` currently overlaps with installer responsibilities but is not yet the authoritative repair path.

## Fixed-state contract

A fresh machine or stale machine should pass this smoke sequence after install, update, or repair:

```powershell
Get-Command cc-devthrottle
cc-devthrottle --version
cc-devthrottle actions --json
cc-devthrottle setup status --json
cc-devthrottle setup doctor --json
```

The smoke should run in a fresh process or shell so PATH changes are tested honestly.

## Phase 0: Lock the source of truth

**Objective:** Make it impossible for installer code, manifests, docs, and shipped profiles to disagree silently.

Implementation:

- Define the shipped Python tool set from product metadata, not from ad hoc lists.
- Treat `tools/registry.json`, `src/CcDirector.Core/Tools/tools-manifest.json`, and the Python bundle manifest as a synchronized contract.
- Add a test that fails if a required shipped tool is absent from any release/install manifest.
- Add a test that fails if an unregistered or extra tool is included in the core install profile.
- Mark legacy aliases as compatibility wrappers with explicit ownership and removal criteria.

Files likely touched:

- `tools/registry.json`
- `src/CcDirector.Core/Tools/tools-manifest.json`
- `scripts/build-python-bundle.ps1`
- `scripts/build-python-bundle.sh`
- `tools/cc-director-setup-avalonia/Models/InstallProfile.cs`
- `tools/cc-director-setup-engine.Tests`

Acceptance:

- One test can answer "what ships by default?"
- Core install profile includes `cc-devthrottle`.
- Core install profile excludes extra/research tools.
- No installer path depends on a broad hard-coded list as the product definition.

## Phase 1: Fix bundle and shim generation

**Objective:** Make release artifacts actually contain and expose `cc-devthrottle`.

Implementation:

- Update bundle builders so the Python tools bundle contains the `cc-devthrottle` package and entry point.
- Generate Windows `.cmd` shims and extensionless shims from the bundle manifest.
- Include shim regeneration in install and update, not only first install.
- Verify the unpacked bundle before publishing.
- Make `cc-devthrottle` the primary installed command.
- Keep old names only as thin wrappers to `cc-devthrottle` where needed for one transition release.

Files likely touched:

- `scripts/build-python-bundle.ps1`
- `scripts/build-python-bundle.sh`
- `tools/cc-director-setup-engine/PythonToolsInstaller.cs`
- `tools/cc-director-setup-engine.Tests`
- `src/CcDirector.Core/Tools/tools-manifest.json`

Acceptance:

- Built bundle contains `cc-devthrottle`.
- Install creates `bin\cc-devthrottle.cmd`.
- Install creates any required shell-compatible shim variants.
- A fresh shell resolves `cc-devthrottle` without relying on legacy aliases.
- Installer tests validate the shim content and entry point.

## Phase 2: Make install, update, and repair self-healing

**Objective:** If a machine has stale or missing tools, the supported installer path repairs it automatically.

Implementation:

- Add a shared tool-healing routine used by first install, update, and repair.
- Detect missing commands, stale shims, wrong target paths, old bundle versions, missing venv packages, and PATH gaps.
- Make `cc-devthrottle setup doctor --json` report:
  - install root
  - bin directory
  - PATH status
  - current bundle version
  - expected shipped tools
  - missing tools
  - stale shims
  - legacy aliases
  - exact repair action
- Make `cc-devthrottle setup repair` call the same install engine used by the installer, rather than maintaining a separate broad tool installer.
- Ensure repair does not install extra tools.

Files likely touched:

- `tools/cc-devthrottle/src/setup_ops.py`
- `tools/cc-devthrottle/src/cli.py`
- `tools/cc-director-setup-engine/PythonToolsInstaller.cs`
- `tools/cc-director-setup-cli/Commands.cs`
- `tools/cc-director-setup-avalonia/Services/EngineInstallRunner.cs`

Acceptance:

- Removing `cc-devthrottle.cmd` and running repair recreates it.
- A stale alias target is detected and fixed or reported.
- Repair uses the same shipped-tool list as install.
- `setup doctor --json` is stable enough for automated checks.

## Phase 3: Replace static action metadata

**Objective:** Agents should discover the real unified tool surface from `cc-devthrottle actions --json`.

Implementation:

- Replace the static `_ACTIONS` list with declarative command metadata or a generated catalog.
- Include every implemented command.
- Include intentional omissions as documented non-actions, not accidental gaps.
- Add contract tests comparing Typer commands to the action catalog.
- Update fleet preambles and Claude Code skills from the same command catalog.

Files likely touched:

- `tools/cc-devthrottle/src/cli.py`
- `tools/cc-devthrottle/tests`
- `src/CcDirector.Core/Sessions/FleetPreamble.cs`
- `src/CcDirector.Core/Sessions/SessionManager.cs`
- `.claude/skills/fleet-comms/SKILL.md`
- `.claude/skills/cc-director/SKILL.md`
- `docs/FleetMessaging.md`

Acceptance:

- `cc-devthrottle actions --json` lists all implemented command groups and subcommands.
- Tests fail when a new command is added without action metadata.
- Session preambles mention the unified command accurately.
- Documentation no longer teaches retired standalone tools as the main path.

## Phase 4: Remove standalone setup entry points

**Objective:** Finish the consolidation the user asked for: one setup surface through `cc-devthrottle setup`.

Implementation:

- Remove old standalone setup commands from shipped profiles and documentation.
- Keep implementation libraries if they are used by the installer, but do not expose duplicate user-facing commands.
- Make `cc-devthrottle setup install/update/repair/doctor/status` the documented setup interface.
- Add compatibility behavior only where needed to avoid breaking an existing release during one upgrade hop.

Files likely touched:

- `tools/registry.json`
- `src/CcDirector.Core/Tools/tools-manifest.json`
- `tools/cc-director-setup-*`
- `docs/install`
- `.claude/skills`

Acceptance:

- No shipped manifest exposes old standalone setup commands.
- New installs only teach `cc-devthrottle setup`.
- Existing users can update once and land on the unified tool.

## Phase 5: Fix the highest-value command parity gaps

**Objective:** After the unified tool is reliably installed, make it cover the UI/Gateway/Director operations users expect.

Implementation order:

1. Settings live API parity
   - Use Control API `/settings` by default when running inside a live Director session.
   - Keep direct file editing as explicit offline mode.
   - Add agents/settings subcommands for existing `/settings/agents` endpoints.
   - Verify `gateway.url` changes trigger live reapply.

2. Schedule parity
   - Add `cc-devthrottle schedule update`.
   - Match Gateway schedule create/list/get/runs/run/enable/disable/delete/update behavior.

3. Session controls
   - Add high-value session commands for prompt, interrupt, escape, hold, wingman toggle, recap, queue, history, usage, and delete.
   - Keep command names consistent with the UI concepts.

4. Gateway/Cockpit ownership decision
   - Decide whether `cc-devthrottle` owns all Gateway/Cockpit API surfaces.
   - If yes, add grouped commands for worklists, account status, telemetry consent, dictionary, transcripts, recordings, exes, and wingman instructions.
   - If no, document the boundary and create separate tool plans.

Acceptance:

- The common UI actions have CLI equivalents.
- CLI tests use mocked Control API/Gateway responses.
- Missing surfaces are tracked intentionally, not discovered by surprise during usage.

## Phase 6: Release and migration proof

**Objective:** Prove this works on machines other than the development machine.

Test matrix:

- Clean Windows install.
- Stale Windows install with legacy aliases only.
- Windows install with missing `cc-devthrottle.cmd`.
- Windows install with stale bundle version.
- Fresh shell after PATH update.
- CI unpack-and-smoke of the release bundle.
- macOS/Linux bundle smoke where supported by the current installer scope.

Required proof:

- Install log.
- `Get-Command cc-devthrottle` or platform equivalent.
- `cc-devthrottle --version`.
- `cc-devthrottle actions --json`.
- `cc-devthrottle setup doctor --json`.
- Confirmation that extra tools were not installed by the core profile.

Acceptance:

- The tool fixes itself through install/update/repair.
- No manual local PATH or shim edits are part of the runbook.
- Release notes clearly say old aliases are compatibility wrappers and `cc-devthrottle` is the supported entry point.

## Suggested issue breakdown

1. Install manifest and shipped-tool contract.
2. Bundle builder includes `cc-devthrottle` and generates correct shims.
3. Installer/update/repair self-heals missing or stale tool shims.
4. `cc-devthrottle setup doctor/repair` uses the install engine.
5. `actions --json` generated or contract-tested against actual commands.
6. Skills, preambles, and docs generated or synchronized from the unified command catalog.
7. Settings live API parity.
8. Schedule update parity.
9. Session control parity.
10. Gateway/Cockpit surface ownership matrix.

## First implementation slice

The first slice should be deliberately narrow:

1. Add shipped-tool contract tests.
2. Fix bundle generation so `cc-devthrottle` is present.
3. Fix install/update shim regeneration.
4. Add installer smoke tests for `cc-devthrottle`.
5. Update `setup doctor` to report missing `cc-devthrottle` clearly.

Do not start broad CLI parity until this slice is green. The primary failure is that the one unified command is not reliably installed and healed; that must be fixed before expanding the command surface.
