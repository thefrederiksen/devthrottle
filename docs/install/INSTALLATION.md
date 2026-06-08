# CC Director - Installation & Auto-Update (Windows)

> **MASTER SPEC - AUTHORITATIVE.** This document defines where every CC Director
> file is installed and how install/update works. It is the single source of
> truth. If any code, script, README, comment, or other document says otherwise,
> **that other source is wrong** and must be reconciled to this document. Do not
> "work around" a disagreement - fix the offending source to match this spec.

How CC Director installs onto a Windows machine, where every file lands, and how
it keeps itself up to date. The install/update engine (`CcDirector.Setup.Engine`)
and both its front-ends (the WPF installer UI and `cc-director-setup-cli`)
implement exactly this layout.

Scope: Windows only. macOS is manual-install and cannot host the Gateway.

---

## 1. The two install types

You choose one of two roles up front. The Gateway role is a strict superset of
the Workstation role.

| Role | What it installs | Admin needed? |
|------|------------------|---------------|
| **Workstation** | Director app + CLI tools, entirely per-user | No - never |
| **Gateway** | Everything a Workstation installs, PLUS the Gateway tray app (starts at logon) and the Cockpit it supervises | No - never |

There is exactly one Gateway on a tailnet; it is usually someone's main
workstation, not a headless box. The Gateway is a **per-user tray app** that
starts at logon and runs in the user's session - NOT a Windows service
(decision history: docs/plans/gateway-tray-app.md). It runs in the user's
session because everything it serves is logon-bound (Directors are desktop
apps) and because its hosted agents (claude.exe) must authenticate as the user.

### The admin question, answered

Admin is required **never**. Both roles are 100% per-user under
`%LOCALAPPDATA%\cc-director`: install, update, rollback, and uninstall all run
unelevated (including from a cloud / CI session). The Gateway tray app swaps its
own binary (and the Cockpit's) and relaunches itself with no UAC because
everything it touches is user-writable by design.

---

## 2. Where everything is placed (canonical)

One per-user root. Nothing lives anywhere else. (`C:\cc-tools`,
`%ProgramFiles%\CC Director`, and `%ProgramData%\cc-director` are retired and
must not be used.)

### Per-user - `%LOCALAPPDATA%\cc-director\` (no admin, ever)

`%LOCALAPPDATA%` = `C:\Users\<you>\AppData\Local`. Everything here installs and
auto-updates with zero UAC (the same reason Chrome, VS Code, and Teams install
per-user).

| Path | Contents |
|------|----------|
| `app\cc-director.exe` | The Director desktop app (in-place self-update by the user) |
| `bin\<tool>.exe` | CLI tools (cc-pdf, cc-html, cc-word, ...), added to the USER PATH |
| `gateway\cc-director-gateway.exe` | The Gateway tray app (Gateway role only; starts at logon via the HKCU Run key `CcDirectorGateway`, runs with `--managed`) |
| `cockpit\cc-director-cockpit.exe` | The Cockpit, supervised as a child of the Gateway tray app (Gateway role only) |
| `config\` | Per-user app configuration (`config\config.json`) |
| `config\setup\update-pins.json` | Rollback pins (versions to skip) |
| `state\` | Setup/update scratch state (e.g. the staged Gateway exe during self-update) |
| `vault\` | The user's personal data store |
| `logs\` | Director + Gateway + `setup-cli.log` |

Generated output documents land in `%USERPROFILE%\Documents\cc-director\`.

The Gateway serves the same user's personal `vault\` directly - same user, same
root, no environment-variable indirection needed.

---

## 3. How updates work

### Independent per-component versioning

Every component carries its **own version**. A release can move one tool forward
without touching the Director, and vice versa. This is driven by a per-asset
`version` field in `release-manifest.json`:

```jsonc
{
  "version": "0.4.0",                 // release tag (informational)
  "assets": {
    "cc-director-win-x64.exe":         { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-director-gateway-win-x64.exe": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-director-cockpit-win-x64.zip": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-pdf-win-x64.exe":              { "version": "1.2.0", "sha256": "...", "platform": "windows" },
    "cc-html-win-x64.exe":             { "version": "1.1.3", "sha256": "...", "platform": "windows" }
  }
}
```

All assets use the release-pipeline naming `<id>-win-x64.exe` (apps and tools
alike). The planner reads each installed component's version, compares it to that
asset's `version` in the latest manifest, and updates **only the components that
are behind**. Cutting a release that changed only `cc-pdf` re-stamps `cc-pdf`;
nothing else is behind, so nothing else moves.

### Cadence: silent and non-disruptive

- Updates are silent and automatic. No banner, no prompt, no UAC - ever.
- Resident apps orchestrate: the Director (while open) and the Gateway tray app
  (in managed mode) periodically run the engine's "update all present components"
  routine.
- Applied so live work is never killed: the Director stages the new build and
  swaps it on next startup; a tool binary not currently running is replaced in
  place and the next invocation picks it up; the Gateway tray app stages the new
  build, exits gracefully (POST /shutdown from the detached helper), swaps, and
  relaunches itself - with /healthz verification and auto-rollback + pin if the
  new build does not come up.

### Each swap keeps a backup

Every component swap (Director, Gateway, Cockpit, tool) keeps the previous build
next to the new one as `<file>.exe.old`. Updates never destroy the build they
replace.

### Rollback

If a new build misbehaves, roll back manually:

```
cc-director-setup-cli rollback <component>
```

This restores the `.old` backup over the live build and **pins away** from the
bad version (written to `config\setup\update-pins.json`) so the update loop does
not immediately re-stage it. There is no automatic health-check or auto-rollback -
rollback is a deliberate, explicit action.

---

## 4. Prerequisites

CC Director needs an agent framework present (it does not install one for you):

- **Claude Code** - https://docs.anthropic.com/en/docs/claude-code/overview
- **Codex** (alternative)

The installer detects whichever is present and, if none is found, prints the
install link and exits with a distinct "prerequisite missing" code (3). It never
runs the framework's own installer.

.NET runtime: the Director and Gateway ship framework-dependent (they need the
.NET 10 runtime; the installer detects it and guides via winget when missing).
The Cockpit ships self-contained inside its zip.

A Gateway-role install has one extra requirement, checked up front and failed
loudly (never half-installed):

- **`OPENAI_API_KEY`** must be set in the user environment at install time
  (`setx OPENAI_API_KEY "sk-..."`). It is a **one-time bootstrap**: on first
  start the Gateway seeds its central key vault (`keyvault.json`) from this
  environment variable when the vault does not already carry the key, so the
  Cockpit shows it as set and Directors pull it immediately. The **vault is the
  live source of truth** thereafter - rotate the key from the Cockpit's *API
  Keys* page (which overwrites the seed); the bootstrap never clobbers an
  existing vault value. The tray app runs in the user's session and inherits the
  user environment directly.

No elevation: the Gateway is a per-user tray app; the installer extracts the
Cockpit, starts the tray app with `--managed`, and the app registers its own
HKCU Run-key autostart.

---

## 5. Using the CLI

The headless front-end (`cc-director-setup-cli`) and the WPF installer UI share
one engine, so a human and an agent install/update identically. Commands:

```
cc-director-setup-cli components               # list known components + roles + assets
cc-director-setup-cli status                   # installed components and their versions
cc-director-setup-cli prereqs                  # check for the agent framework
cc-director-setup-cli plan                     # show what an install/update would change
cc-director-setup-cli install --role <role>    # install/update all components for a role
cc-director-setup-cli update                   # download, verify, apply updates
cc-director-setup-cli rollback <component>     # restore the previous build and pin away
```

Common options:

| Option | Meaning |
|--------|---------|
| `--role workstation\|gateway` | Install type (default `workstation`) |
| `--manifest <path\|latest>` | Release source (default `latest` = GitHub latest release) |
| `--release-dir <dir>` | Use a local directory as the release (offline; see below) |
| `--component <id\|all>` | Limit an update to one component |
| `--tools <id,id,...>` | Override the tool set |
| `--root <dir>` | Override the per-user root (`%LOCALAPPDATA%\cc-director`) - testing |
| `--dry-run` | Plan only; do not download or apply |
| `--json` | Machine-readable output (for agents) |

Exit codes: `0` ok, `1` runtime error, `2` usage error, `3` prerequisite missing.

Every asset is verified against the manifest's SHA-256 before it is placed; a
mismatch is rejected, not installed.

---

## 6. Offline / no-admin testing (`--release-dir`)

`--release-dir <dir>` treats a local directory as a full release. The directory
must contain `release-manifest.json` plus each asset file named exactly as in the
manifest. Because the Workstation flow needs neither network nor admin, this lets
you exercise the entire install -> update -> rollback loop hermetically.

A verified end-to-end run (no admin, no network), installing into a sandbox root
and then updating only the Director:

```
# 1. Fresh install of release v1 into a sandbox
cc-director-setup-cli install --role workstation --root <sandbox> --release-dir <relV1>
#    -> director, cc-pdf, cc-html, cc-word all Installed at 0.1.0

# 2. Release v2 bumps ONLY the Director (0.1.0 -> 0.2.0); tools stay 0.1.0
cc-director-setup-cli plan --role workstation --root <sandbox> --release-dir <relV2>
#    director  Update    0.1.0 -> 0.2.0
#    cc-pdf    UpToDate  (0.1.0)        <- independent versioning: tools untouched
#    cc-html   UpToDate  (0.1.0)
#    cc-word   UpToDate  (0.1.0)

cc-director-setup-cli update --role workstation --root <sandbox> --release-dir <relV2>
#    -> only director Updated; <sandbox>\app\cc-director.exe.old backup created

# 3. Roll the Director back and pin away from 0.2.0
cc-director-setup-cli rollback director --root <sandbox>
#    -> director restored to 0.1.0; config\setup\update-pins.json = {"director":"0.2.0"}

cc-director-setup-cli plan --role workstation --root <sandbox> --release-dir <relV2>
#    director  Pinned    (skipping 0.2.0)   <- the bad version is not re-staged
```

Workstation install lands the Director at `<sandbox>\app\cc-director.exe` and each
tool at `<sandbox>\bin\<tool>.exe`, exactly mirroring the production per-user
layout.

---

## 7. Code signing

CC Director binaries are currently unsigned. On first run Windows SmartScreen may
show a warning; choose "More info" -> "Run anyway". Signing will be revisited if
the product ships to external users.

---

## 8. References

- Plan / design decisions: `docs/plans/install-autoupdate.md`,
  `docs/plans/gateway-tray-app.md` (Gateway = tray app, service retired)
- Engine source: `tools/cc-director-setup-engine/`
- CLI source: `tools/cc-director-setup-cli/`
- Release pipeline: `.github/workflows/release.yml`
- Gateway scripts: `scripts/verify-gateway.ps1`, `scripts/deploy-cockpit.ps1`,
  `scripts/redeploy-gateway.ps1`, `scripts/test-gateway-selfupdate.ps1`

> Reminder: this file is the master spec (see the banner at top). When you change
> install behavior, change THIS document first, then make the code match it.
</content>
