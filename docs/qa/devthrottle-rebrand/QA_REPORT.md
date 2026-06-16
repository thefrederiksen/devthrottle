# DevThrottle Rebrand - QA Report (v0.8.0)

**Date:** 2026-06-16
**Scope:** Rebrand the installer/setup and the application icon from "CC Director" to
"DevThrottle", create a DevThrottle logo, release a new version, and verify the README
install flow end-to-end as a real user.

**Verdict: PASS.** Everything below was executed and verified. The release
`DevThrottle v0.8.0` is published, every README link resolves, downloaded assets
match their published SHA-256, and an isolated install succeeds and ships the new icon.

---

## Brand assets

![DevThrottle logo](logo.png)

![DevThrottle icon](icon.png)

Simple "DT" monogram on a dark slate tile with an amber throttle accent, plus a
two-tone wordmark. Generated reproducibly by `scripts/branding/generate_devthrottle_icons.py`
(Pillow) and applied to the Director app, setup wizard, Gateway, and Launcher.

---

## What changed (and what was deliberately kept)

### Rebranded (user-facing)
- **App + setup icon** -> new DT monogram (`.ico` for Director, setup, Gateway, Launcher).
- **Setup wizard** (WPF + Avalonia): every window title, welcome/uninstall/complete
  text, sidebar, and buttons now say "DevThrottle" (54 strings).
- **Windows Add/Remove Programs**: DisplayName + Publisher -> "DevThrottle".
- **Start Menu shortcut**: `DevThrottle.lnk`.
- **Component display names**: DevThrottle / DevThrottle Gateway / DevThrottle Cockpit / DevThrottle Launcher.
- **Setup CLI** help text -> DevThrottle.
- **Release downloads** renamed: `devthrottle-setup-win-x64.exe`,
  `devthrottle-setup-cli-win-x64.exe`, `devthrottle-setup-mac-arm64.zip`.
- **GitHub release title** -> "DevThrottle v0.8.0" (live release edited + workflow template fixed).
- **README** rebranded (logo + title + prose); getting-started docs rebranded; stale/broken
  repo URLs fixed to `thefrederiksen/devthrottle`.

### Kept stable on purpose (would break upgrade / auto-update otherwise)
- Main app executable name `cc-director.exe` (the user asked to keep the "individual name").
- Main-app release assets `cc-director-win-x64.exe` / `cc-director-mac-arm64.zip`.
- Install directory `%LOCALAPPDATA%\cc-director`.
- Add/Remove Programs registry **key** `CcDirector` (only the DisplayName changed).
- Installed macOS bundle `CC Director.app` (its own identity, like the exe name).

### Couplings updated so nothing breaks
- `GatewayTrayLauncher.CliAsset` -> `devthrottle-setup-cli-win-x64.exe` (the wizard fetches
  the CLI by exact name).
- `ComponentRegistry.DiscoverToolIds` now excludes `devthrottle-setup` / `devthrottle-setup-cli`
  so the installer is never mis-detected as an installable tool (regression test updated).
- `release.yml` verify-completeness required-asset list + mac setup bundle `--app-name`.

---

## Test results

| # | Check | Result |
|---|-------|--------|
| 1 | `dotnet build cc-director.sln` | PASS - 0 warnings, 0 errors |
| 2 | Build WPF setup wizard + setup CLI | PASS - 0/0 |
| 3 | Setup-engine tests (ComponentRegistry + AddRemovePrograms) | PASS - 9/9 |
| 4 | Release `DevThrottle v0.8.0` published | PASS - not draft, not prerelease |
| 5 | Renamed setup assets present in release | PASS - all 3 `devthrottle-setup-*` |
| 6 | Main-app assets kept | PASS - `cc-director-win-x64.exe`, `cc-director-mac-arm64.zip` |
| 7 | Live README on GitHub rebranded | PASS - title "DevThrottle", logo, devthrottle links |
| 8 | Every README link resolves (HTTP) | PASS - 15/15 return 200 (table below) |
| 9 | Asset download integrity (SHA-256 vs manifest) | PASS - exact match |
| 10 | Setup CLI runs and reports version | PASS - `0.8.0` |
| 11 | Isolated install (`--root` temp) of Director + tools | PASS - director + 9 tools placed |
| 12 | Install creates `DevThrottle.lnk` shortcut | PASS - rebrand confirmed |
| 13 | New DT icon embedded in shipped `cc-director.exe` | PASS - extracted from installed exe |
| 14 | User's real install untouched / side effects cleaned | PASS - see below |

### Link sweep (every URL in README.md)
All 15 return HTTP 200:
```
200  https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-win-x64.exe
200  https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-mac-arm64.zip
200  https://github.com/thefrederiksen/devthrottle/releases/latest/download/cc-director-win-x64.exe
200  https://github.com/thefrederiksen/devthrottle/releases/latest/download/cc-director-mac-arm64.zip
200  https://github.com/thefrederiksen/devthrottle/releases/latest
200  https://github.com/thefrederiksen/devthrottle/discussions
200  https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest
200  https://img.shields.io/badge/Download-Setup%20for%20Windows-2EA44F  (badge)
200  https://img.shields.io/badge/Download-Setup%20for%20macOS-2EA44F    (badge)
200  https://claude.ai/install.ps1
200  https://claude.ai/install.sh
200  https://code.claude.com/docs/en/setup
200  https://docs.anthropic.com/en/docs/claude-code
200  https://dotnet.microsoft.com/download/dotnet/10.0
200  https://sorenfrederiksen.com
```
Relative links (`docs/PHILOSOPHY.md`, `docs/install/install-prompt.md`, all `images/*.png`)
were verified to exist in the repo.

### Isolated install transcript (abridged)
```
Plan:
  director       Install      install 0.8.0
Install complete:
  director       Installed
Python tools: Installed 9 Python tools (bundle 0.8.0).
Start Menu shortcut: created   (DevThrottle.lnk)
```
The install used the CLI's `--root` flag to target a throwaway temp directory, so the
user's real `%LOCALAPPDATA%\cc-director` install was never touched.

---

## Safety: user environment protected

The isolated install produces three shared-state side effects that `--root` does not
redirect (PATH entry, Start Menu shortcut, downloaded files). All were verified and then
**removed**:

- `DevThrottle.lnk` (pointed at the temp install) - removed.
- Temp `\bin` entry appended to the User PATH - removed.
- Temp install root + downloaded assets - deleted.
- Verified afterward: the user's real `CC Director.lnk` still points at
  `%LOCALAPPDATA%\cc-director\app\cc-director.exe`, the real install dir is intact, and the
  ARP registry key was never written by the component install.

No running `cc-director.exe` process was touched at any point.

---

## Known follow-ups (out of scope / not blocking)

1. **macOS runtime not verified** - all mac strings/bundle-name/asset renames are done in
   code and the mac release artifact builds, but no Mac was available to run the wizard.
2. **README setup screenshots recaptured (DONE)** - `images/setup-1-welcome.png`,
   `setup-2-prerequisites.png`, `setup-3-update.png` now show the rebranded DevThrottle wizard
   (DT icon, "Welcome to DevThrottle", prerequisites, installing tools/skills) at v0.8.0. The
   main-app hero (`cc-director-main.png`) was left as-is - it shows the Director app, whose
   window identity intentionally stays "cc-director" (only the icon changed).
3. **Internal dev docs** (`docs/install/INSTALLER_HANDOVER.md`, testing/plan docs) still use
   the old setup asset names; they are not linked from the README, so they are outside the
   user-facing install flow.
4. **Legacy Start Menu shortcut on upgrade**: an existing install's `CC Director.lnk` is not
   auto-removed when the rebranded installer creates `DevThrottle.lnk` (minor cosmetic; both
   launch the same app).

---

## Commits / release

- `dc2c89a` feat(brand): rebrand the installer + app icon to DevThrottle
- `cc852ec` release: v0.8.0
- `5ece933` fix(release): title GitHub releases "DevThrottle vX.Y.Z"
- Release: https://github.com/thefrederiksen/devthrottle/releases/tag/v0.8.0
