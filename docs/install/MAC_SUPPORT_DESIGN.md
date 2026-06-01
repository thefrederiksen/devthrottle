# CC Director - macOS Install & Gateway (design)

> **STATUS: DESIGN / PROPOSAL - not built.** This is the agreed direction for
> extending install/update and the Gateway to macOS. It does NOT change what
> ships today. The authoritative master spec for shipped (Windows) behavior and
> locations remains `docs/install/INSTALLATION.md`; this document extends that
> model to macOS and will be folded into it once implemented.

## 1. Context

Today install/update is Windows-only:
- The shared engine (`CcDirector.Setup.Engine`) is mostly platform-neutral but its
  `InstallLayout` hardcodes Windows roots and it has no concept of a non-Windows
  service.
- There are two installer UIs: `tools/cc-director-setup` (WPF, Windows, now wired
  onto the engine) and `tools/cc-director-setup-avalonia` (Avalonia, still on the
  old pre-engine code).
- The Gateway is a Windows Service (NSSM, LocalSystem). The master spec currently
  says macOS "cannot host the Gateway."

This design answers two questions: (a) should there be one installer that compiles
for both platforms, and (b) how does the always-on Gateway run on macOS, which has
no Windows Service.

## 2. Decision: one Avalonia installer over the shared engine

Adopt a single **Avalonia** installer for both Windows and macOS, over the shared
engine - mirroring the main app (WPF archived; `CcDirector.Avalonia` is canonical
on both platforms). The Windows-only WPF setup wizard is retired once the Avalonia
installer reaches parity on Windows.

Responsibilities:

- **`CcDirector.Setup.Engine`** stays the shared, headless brain (planner,
  manifest, download + SHA-256 verify + swap + `.old` backup, rollback, pins).
  Three things become platform-resolved instead of Windows-only:
  - `InstallLayout` - resolves per-OS roots (section 4).
  - A new `IServiceController` abstraction - Windows (NSSM) impl and macOS
    (launchd) impl (section 3).
  - `UpdateRunner` gains archive handling. It currently skips `.zip` assets; macOS
    ships the app as a zipped `.app`, so it needs extraction + `.app` placement.
    Reuse the logic the main app already has (`UpdateService.ExtractMacApp` +
    `StripQuarantine`).
- **The Avalonia installer UI** is a thin shell over the engine; only copy/labels
  and a few platform branches differ.
- **Platform helpers** behind small interfaces: PATH (Windows registry vs a shell
  profile entry), launcher (Start Menu `.lnk` vs `/Applications` + Dock), elevation
  (UAC vs `sudo` / Authorization Services), service (NSSM vs launchd).

The WPF rewire already done is not wasted: it proved the engine seam and keeps
Windows shipping until the Avalonia installer is at parity.

## 3. Decision: the Gateway "service" on macOS = launchd

macOS has no Windows Service / NSSM; the direct analog is **launchd**, driven by a
`.plist` job. Two flavors, and the choice is the real decision:

| | LaunchAgent (per-user) | LaunchDaemon (system) |
|---|---|---|
| Lives in | `~/Library/LaunchAgents/` | `/Library/LaunchDaemons/` |
| Runs as | the user | root |
| Starts | at login, in the user session | at boot, before login, all users |
| Admin to install | none | `sudo` once |
| Always-on / headless | only while logged in | true always-on |

The Windows Gateway is machine-wide, boot-start, survives-logout - the faithful
analog is a **LaunchDaemon** (`sudo` once, like the Windows "admin once"). But the
Gateway is "usually someone's main workstation, not a headless box," and no-admin
is a strong product goal, so a **LaunchAgent** is the better default on macOS: zero
admin ever, lives entirely under `~/Library`, starts at login. The only thing given
up is running while logged out.

**Recommendation:** LaunchAgent as the default Mac Gateway (no-admin parity);
LaunchDaemon as an opt-in for a genuinely headless/always-on box. (This is the one
open decision - see section 8.)

The Cockpit stays a supervised child process of the Gateway on both platforms
(no second launchd job).

## 4. macOS install locations

Analog of the Windows three roots (master spec section 2). Final paths are subject
to the section 8 decision (LaunchAgent vs LaunchDaemon changes the Gateway root).

| Role | Windows | macOS (proposed) |
|------|---------|------------------|
| Per-user app | `%LOCALAPPDATA%\cc-director\app\` | `~/Applications/CC Director.app` |
| Per-user tools | `%LOCALAPPDATA%\cc-director\bin\` (+ PATH) | `~/Library/Application Support/cc-director/bin` (+ shell profile) |
| Per-user data/config | `%LOCALAPPDATA%\cc-director\` | `~/Library/Application Support/cc-director/` |
| Gateway (LaunchAgent) | `%ProgramFiles%\CC Director\` | `~/Library/Application Support/cc-director/gateway\|cockpit/` + `~/Library/LaunchAgents/<label>.plist` |
| Gateway (LaunchDaemon opt-in) | (n/a) | `/Library/Application Support/cc-director/` + `/Library/LaunchDaemons/<label>.plist` |

Per-user macOS install needs no admin, matching the Windows Workstation story.

## 5. Self-update on macOS

The no-admin-on-update guarantee holds, and is in fact simpler on macOS:

- Unix allows replacing a running binary's file (unlink + write new; the running
  process keeps its open inode), so there is no Windows-style "file is locked"
  problem. The swap just succeeds; the new binary is used on the next launch.
- The Gateway updates its own files, then restarts itself with
  `launchctl kickstart -k <label>` (or unload/load). A LaunchAgent does this as the
  user (no admin); a LaunchDaemon does it as root (no repeated `sudo`) - the same
  self-updating-service pattern as the Windows service.
- App + tool updates are plain per-user file swaps under `~`, no admin.
- Gatekeeper: unsigned/notarized-absent builds are quarantined on download; the
  installer/updater must strip the quarantine attribute
  (`xattr -dr com.apple.quarantine <path>`), which the main app's updater already
  does.

## 6. Prerequisites and gating gaps

Two of these are not about the installer at all and gate real macOS support:

1. **Release pipeline builds only the macOS *Director* today.** `.github/workflows/
   release.yml` builds the cc-* tools and the Gateway/Cockpit on `windows-latest`
   only. macOS support needs the pipeline to also produce `osx-arm64` artifacts for
   the Gateway/Cockpit. Until then a Mac installer can only place the Director app.
2. **The cc-* tools are Windows-built** (PyInstaller / .NET / node). Shipping them
   on macOS is a separate build effort - likely OUT OF SCOPE for a first cut. A v1
   Mac installer installs the Director app (and optionally the Gateway), not the
   30+ tools.
3. **Signing / notarization.** Quarantine stripping (section 5) unblocks local use;
   notarization is the longer-term requirement for clean external distribution.

## 7. Phased plan

1. **Engine: platformize.** `InstallLayout` resolves per-OS; add `IServiceController`
   (NSSM today, launchd next); add `.zip`/`.app` extraction to `UpdateRunner`.
2. **Pipeline: macOS artifacts.** Build Gateway/Cockpit `osx-arm64` (Director already
   builds); defer tools.
3. **Avalonia installer onto the engine.** Workstation/Director-only first; proves
   the cross-platform UI. Windows keeps the WPF installer meanwhile.
4. **Mac Gateway via launchd.** Generate + load the LaunchAgent plist, self-update via
   `kickstart`; LaunchDaemon as opt-in.
5. **Converge.** Retire the WPF setup wizard once the Avalonia installer is at parity
   on Windows; fold macOS locations into the master spec.

## 8. Open decision

**Mac Gateway = LaunchAgent (per-user, no admin, login-scoped) or LaunchDaemon
(root, boot, headless, `sudo` once)?** This drives the Gateway root in section 4 and
the elevation story. Lean: LaunchAgent default (maximizes no-admin), LaunchDaemon
opt-in for a headless/serve-while-logged-out box.

## 9. References

- Master spec (shipped Windows behavior + locations): `docs/install/INSTALLATION.md`
- Engine: `tools/cc-director-setup-engine/` (`InstallLayout`, `UpdateRunner`, `GatewayServiceCommands`)
- Avalonia installer (to be rewired): `tools/cc-director-setup-avalonia/`
- Mac app packaging + quarantine strip today: `scripts/package-mac-app.sh`, `src/CcDirector.Core/Update/UpdateService.cs`
- Release pipeline: `.github/workflows/release.yml`
