# Spec: Unified Install and Auto-Update (Director + Gateway + Cockpit)

**Status:** DRAFT / SPEC (not yet implemented)
**Date:** 2026-05-31
**Author:** Soren
**Related:** [docs/PHILOSOPHY.md](../PHILOSOPHY.md), GitHub tracking issue (see top of repo issues), [docs/install/install-prompt.md](../install/install-prompt.md)

## Goal

Make installing CC Director one simple step on **Windows and macOS**, and make it keep itself current automatically: **the next time you run CC Director, it has updated itself.** Instructions must be super straightforward. No scavenger hunt across releases, no repeated admin prompts, no re-pasting.

## Guiding principle

**Deterministic work is code; only judgment is a prompt.**

- **Code (deterministic):** detect OS, download the right release artifacts, verify checksums, install, register the Gateway service, set PATH, and update later.
- **Claude Code (judgment):** install Claude Code itself (Anthropic's installer), Tailscale account sign-in, the single admin elevation, and machine-specific troubleshooting.

The installer is intentionally boring. Boring is reliable.

## Current state (the problems this closes)

- **Auto-update does not reliably work** (Director builds set `UpdaterEnabled=true` but updates are not landing reliably; root cause TBD as part of this work).
- **Gateway and Cockpit are not shipped** as release artifacts; today the Gateway only runs from source.
- **Install is Windows-leaning and fragmented:** `cc-director-setup` (Windows wizard) + the retired Python setup command (now folded into `cc-devthrottle setup`) + a Claude Code prompt that installs only the Director.
- **No single owner of versions** across Director / Gateway / Cockpit / tools.

## Target design

### 1. One cross-platform installer/updater tool

Evolve the existing setup tooling (`cc-director-setup` plus `cc-devthrottle setup`) into a single cross-platform installer/updater that:

- Detects OS + arch.
- Reads one **release manifest** (versions + per-asset SHA-256 for Director, Gateway, Cockpit, tools).
- Downloads the artifacts always needed together (Director + Gateway; Cockpit where applicable), verifies SHA-256, installs them.
- Registers the Gateway service (Windows: Windows Service, LocalSystem; macOS later: launchd daemon -- tracked separately).
- Is interactive enough to feel like a normal install ("Install everything? [Y/n]") but also runnable unattended.

### 2. Two front doors, one installer underneath

- **Normal installer:** `irm .../install.ps1 | iex` (Windows), `curl -fsSL .../install.sh | bash` (macOS). These fetch and run the installer tool.
- **Claude Code prompt:** triggers the same installer tool (does not reinvent the steps).

Both share one code path -> one source of truth.

### 3. Auto-update model (install once, update forever)

After the first install, no one pastes anything again:

- **Director:** self-updates per-user (user-writable install location, no admin). Fix whatever is currently preventing this from working.
- **Gateway:** runs as a service (LocalSystem on Windows). It updates **itself and the Cockpit** on a timer using its existing privilege -- no admin re-prompt.
- **Versioning:** one release manifest drives all components so the updater reasons about the whole set, not piece by piece.
- **Trigger:** "next launch is current" -- check on startup, apply in the background, take effect on the following run (same pattern Claude Code uses).

### 4. Release pipeline

CI must publish, for every release, a coherent set + manifest:

- `cc-director-win-x64.exe` (Director, exists)
- `cc-director-mac-arm64.zip` (Director, exists)
- `cc-director-gateway-win-x64.exe` (Gateway, **new** -- service-capable via `UseWindowsService()`)
- Cockpit artifact (**new**, as needed)
- `release-manifest.json` with version + SHA-256 per asset (exists for Director assets; extend to cover Gateway/Cockpit)

## Acceptance criteria

- [ ] A first-time user installs CC Director on **Windows** with one command/click; Director + Gateway end up installed and the Gateway service is running.
- [ ] A first-time user installs CC Director on **macOS (Apple Silicon)** with one command; the Director is installed and launchable. (Mac Gateway tracked separately.)
- [ ] After a new release, **launching CC Director updates it with no user action** (Director), and the **Gateway service updates itself + Cockpit** with no admin prompt.
- [ ] One release manifest is the single source of truth for all component versions + checksums.
- [ ] The Claude Code prompt and the one-line installer both call the same installer tool.
- [ ] Instructions in the README / docs are reduced to one obvious command per OS.

## Out of scope (tracked elsewhere)

- macOS Gateway (launchd daemon) -- separate issue.
- The Director "gateway not connected" banner -- separate issue (depends on Gateway shipping).
- Cockpit feature work beyond shipping + auto-update.

## Notes / open questions

- Root-cause the current Director auto-update failure before layering Gateway/Cockpit update on top.
- Gateway-as-service self-update on Windows: confirm the locked-exe replacement approach (stop/replace/restart via the service's own LocalSystem rights, or a SYSTEM scheduled task).
- Keep the installer free of "fallback" behavior: on failure, stop with a clear error + exact fix, never silently degrade.
