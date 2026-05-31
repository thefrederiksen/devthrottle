# CC Director on macOS — build & launch

This directory (`local_builds/mac/`) is the **build output + launcher tooling** for
running CC Director on a Mac. It's the macOS counterpart of the Windows
`local_builds/_local_build_avalonia*.bat` flow. Only the cross-platform **Avalonia**
UI is built here — the WPF project is Windows-only.

The model: **one stable "main" app you keep in the Dock, plus four numbered test
slots** you build on demand while developing (run several at once to compare versions).

---

## TL;DR

```bash
# One-time: install the apps + pin "CC Director" to the Dock
scripts/mac-setup.sh

# Day to day, from the repo root:
scripts/mac-rebuild.sh main     # rebuild your Dock copy
scripts/mac-rebuild.sh 2        # build test slot 2
scripts/mac-rebuild.sh all      # rebuild main + all 4 slots
```

After setup, launch from the **Dock** (the bar at the bottom), **Spotlight**
(`Cmd+Space`, type "CC Director 2"), or **Launchpad** — never the terminal.

---

## One-time toolchain setup

CC Director targets `net10.0`, so you need the **.NET 10 SDK**:

```bash
# User-local install, no sudo (recommended):
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
#   …or, via Homebrew (asks for your password):  brew install --cask dotnet-sdk
```

The scripts auto-detect an SDK under `~/.dotnet`, so no PATH changes are needed.

Then run the one-time setup:

```bash
scripts/mac-setup.sh
```

This creates five apps in `/Applications`, builds the **main** binary, and pins
**"CC Director"** to your Dock. Re-running it is safe.

---

## The apps

| App (in `/Applications`) | Binary | Purpose |
|--------------------------|--------|---------|
| **CC Director**          | `cc-director-mac-main` | Your stable everyday copy. Pinned to the Dock. |
| **CC Director 1–4**      | `cc-director-mac1`…`4` | Independent dev test slots. Build on demand; run several at once. |

Each app is a distinct macOS bundle (own Dock tile, own Spotlight/Launchpad entry,
own bundle id), so they don't interfere with each other.

A slot you haven't built yet still has an icon in `/Applications`; clicking it just
pops a dialog telling you the build command.

---

## Scripts

| Script | What it does |
|--------|--------------|
| `scripts/mac-setup.sh` | One-time bootstrap: create all 5 app icons, build main, pin main to the Dock. |
| `scripts/mac-rebuild.sh <main\|1\|2\|3\|4\|all\|apps>` | The everyday command. Builds a target and refreshes its `.app`. `main` also re-pins the Dock; `apps` (re)creates the bundles without building. |
| `scripts/local-build-mac.sh` | The underlying build (clean → build Core → single-file publish → copy here). Called by the above; use directly for flags like `--self-contained`, `--rid osx-x64`, `--configuration Debug`. |
| `local_builds/mac/make-app-bundle.sh --target <main\|1\|2\|3\|4>` | Wraps a built binary in a `.app` bundle installed to `/Applications`. Called by `mac-rebuild.sh`. |
| `local_builds/mac/run.sh [--slot N]` | Launch a built binary straight from the terminal (sets `DOTNET_ROOT`). For quick checks; prefer the Dock/`.app` for real use (see below). |

`APPS_DIR` env var overrides the install location (default `/Applications`), e.g.
`APPS_DIR=~/Applications scripts/mac-rebuild.sh main`.

---

## Files in this directory

| File | Tracked? | Notes |
|------|----------|-------|
| `make-app-bundle.sh`, `run.sh`, `_local_build_mac1.sh` | yes | Build/launch helpers. |
| `AppIcon.svg` | yes | Editable vector **source** for the app icon. Edit this to change the icon. |
| `README.md` | yes | This file. |
| `cc-director-mac-main`, `cc-director-mac1`…`4` | no | Built binaries (gitignored). |
| `CC Director*.app` | no | Built bundles live in `/Applications`, not here. |
| `AppIcon.icns` | no | Generated from `AppIcon.svg` (gitignored). |

---

## Why launch via the `.app` and not the terminal?

The `.app` launches CC Director under **launchd** — macOS's service manager —
completely outside any Claude Code session's process tree. That matters: the
`claude` processes CC Director spawns then get clean, interactive stdio. If you
launch it from inside a terminal that's itself under a Claude Code session, those
child `claude` processes inherit a non-interactive pseudo-terminal and die with the
`--print` error. (This is the macOS analog of the Windows Task Scheduler / svchost
trick in `CLAUDE.md` §0b.)

So: **click the Dock icon (or use Spotlight/Launchpad).** That's the supported path.

---

## The app icon

- Source: `local_builds/mac/AppIcon.svg` — a dark macOS "squircle" with a **CC**
  monogram and a blue terminal underline/cursor (brand accent `#007ACC`).
- The bundle script renders it to `AppIcon.icns` using macOS's built-in `qlmanage`
  (no extra tooling). If the SVG is missing it falls back to the Avalonia `app.ico`.
- **To change the icon:** edit `AppIcon.svg`, delete `local_builds/mac/AppIcon.icns`,
  then run `scripts/mac-rebuild.sh apps`. Because macOS aggressively caches icons,
  `mac-rebuild.sh main` (and `mac-setup.sh`) bust the Dock/icon-services cache so the
  new icon actually shows.

---

## Troubleshooting

- **"To open CC Director you need to install Rosetta."** The bundle is built native
  (arm64 on Apple Silicon) and its `Info.plist` sets `LSRequiresNativeExecution` +
  `LSArchitecturePriority` to prevent this. If you ever see it, rebuild the bundle:
  `scripts/mac-rebuild.sh main` (or `apps`).
- **Dock tile shows a blank/white icon.** Stale icon cache. Run
  `scripts/mac-rebuild.sh main` (it busts the cache and re-pins), or just launch the
  app once and it settles.
- **"can't be opened because Apple cannot check it for malware."** First launch of an
  unsigned app — right-click the app → **Open** once. Locally-built apps usually skip
  this; the bundle script also strips the quarantine flag.
- **App won't start / runtime error.** The default build is *framework-dependent* and
  needs the .NET 10 runtime. The bundle bakes `DOTNET_ROOT=~/.dotnet` into its
  launcher; if your SDK lives elsewhere, reinstall per the setup section, or build
  `--self-contained` (no runtime needed): `scripts/local-build-mac.sh --slot -main --self-contained`.

---

## Notes

- The Windows-only NuGet packages (`Microsoft.Web.WebView2`, `NAudio`) restore
  cleanly on macOS and are simply not exercised at runtime (those features are no-ops).
- Default build is framework-dependent single-file (~40 MB). `--self-contained`
  bundles the runtime (~150+ MB, no .NET install required on the target).
- RID is auto-detected: `osx-arm64` on Apple Silicon, `osx-x64` on Intel.
