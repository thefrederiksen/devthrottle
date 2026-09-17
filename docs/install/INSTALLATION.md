# DevThrottle - Installation & Auto-Update (Windows)

> **MASTER SPEC - AUTHORITATIVE.** This document defines where every CC Director
> file is installed and how install/update works. It is the single source of
> truth. If any code, script, README, comment, or other document says otherwise,
> **that other source is wrong** and must be reconciled to this document. Do not
> "work around" a disagreement - fix the offending source to match this spec.

How DevThrottle installs onto a Windows machine, where every file lands, and how
it keeps itself up to date. The install/update engine (`CcDirector.Setup.Engine`)
and both its front-ends (the WPF installer UI and `cc-director-setup-cli`)
implement exactly this layout.

Scope: Windows. macOS installs via the `curl` bootstrap and is Director-only - it
cannot host a self-hosted Gateway.

---

## 1. Install is two steps, and only the second one asks who you are

Installing is **not** a role you choose up front. It is a sequence:

| | Step | Account? |
|---|------|----------|
| **1** | **Install the Director** - the app, every `cc-*` tool, and the Launcher | **No.** The Director installs and runs with no account and no gateway. |
| **2** | **Connect a gateway** - done later, from the app, not from the installer | **Yes.** Both gateway options require a DevThrottle login. |

This is the install spine, and it is a product decision, not an implementation
detail (issue #862):

1. **Download-first.** The Director installs and runs with no account. Never put
   an account, an email, or a sign-up in front of the download.
2. **Connecting a gateway requires a DevThrottle login** - for the hosted gateway
   AND for a self-hosted one. The login appears at the gateway-connect step,
   never in front of the download.
3. **Login is Google, GitHub, or email.** Email is auto-confirm; there is no
   email-verification gate.
4. **No bring-your-own-key, anywhere.** Inference always routes through
   DevThrottle on an account-minted `dt_live_` key that the runtime mints itself.
   That is precisely *why* a gateway needs a login.

### Step 1 - the Director install

One linear path, for every install and every update: **Welcome -> Install ->
Complete** (`WizardStepFlow`). There is no role picker, no prerequisites screen,
no skills screen, and no sign-in gate. The installer lays down the Director app,
every `cc-*` tool, and the Launcher, and then it is finished.

### Step 2 - the gateway choice

The user is asked about a gateway by the Director's own setup wizard, on its
second screen (see section 1a). Three answers, and **"Not now" is a first-class
one** - the machine stays fully usable without a gateway.

| Choice | What it means |
|--------|---------------|
| **Hosted** (recommended) | We run it. Sign in, and the machine is enrolled. Part of the Pro plan. |
| **Self-hosted** | The user runs the Gateway tray app on their own machine and joins it. Windows only. Still a DevThrottle login. For advanced setups. |
| **Not now** | Local-only on this machine. Connectable any time from Settings. |

The self-hosted Gateway is still installable - `install --role gateway` adds the
Gateway tray app, which starts at logon and serves the React Cockpit in-process.
It is a **per-user tray app** in the user's session, NOT a Windows service
(decision history: docs/plans/gateway-tray-app.md): everything it serves is
logon-bound (Directors are desktop apps) and its hosted agents (claude.exe) must
authenticate as the user. There is exactly one self-hosted Gateway on a tailnet,
and it is usually someone's main workstation, not a headless box.

The graphical installer never offers that role. It is reached deliberately, via
the CLI or the app, and it is not the path this release leads with.

### The admin question, answered

Admin is required **never**. Everything is 100% per-user under
`%LOCALAPPDATA%\cc-director`: install, update, rollback, and uninstall all run
unelevated (including from a cloud / CI session). The Gateway tray app swaps its
own binary (and its bundled Cockpit + mobile static files) and relaunches itself
with no UAC because everything it touches is user-writable by design.

---

## 1a. The Director's setup wizard (first run)

Distinct from the installer. The installer puts files on disk; this wizard runs
once inside the Director, the first time it opens, and configures **this
machine**. Its canonical order is `FirstRunWizardModel.CanonicalOrder`:

| # | Step | Screen title |
|---|------|--------------|
| 1 | `Welcome` | Let's get you set up |
| 2 | `Gateway` | Connect your gateway |
| 3 | `Agents` | Your agents |
| 4 | `Tools` | Tools, maintained for you |
| 5 | `Code` | Where does your code live? |
| 6 | `Screenshots` | Where do your screenshots go? |
| 7 | `Browsers` | Let your agents use a browser |
| 8 | `Done` | You are ready |

**The gateway comes second, immediately after Welcome, by design.** Connecting is
who you are; everything after it configures one particular computer. It also puts
the step that carries the real payoff - agents on your phone, voice, the morning
report - where it is actually seen, rather than five screens in. This does not
make the gateway a gate: "Not now" stays exactly as prominent, because a first
screen that reads as sign-up-to-continue would change what the product is.

**Every step here must be about THIS machine.** The wizard runs once per Director
per machine, so a question whose answer belongs to the *account* would be asked
once per machine and the copies would never reconcile. That is why there is **no
daily/morning-report step** (issue #996) - the report is one person, one email,
delivered through the gateway. The gateway step stays because enrolling *this
machine* genuinely is about this machine.

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
| `gateway\wwwroot\c\` | The React Cockpit static files, served in-process by the Gateway at the site root `/` (Gateway role only; issue #979). Unpacked from the `devthrottle-gateway-cockpit-win-x64.zip` side-car on install/self-update |
| `gateway\wwwroot\m\` | The mobile app static files, served by the Gateway at `/m` (Gateway role only) |
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
    "devthrottle-gateway-win-x64.exe": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "devthrottle-gateway-cockpit-win-x64.zip": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
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

### .NET runtime: nothing to install

Every Windows binary ships **self-contained** - the runtime is inside the
executable. The Director, the Gateway tray app, the Launcher and the setup CLI are
all published `--self-contained true -p:PublishSingleFile=true`
(`.github/workflows/release.yml`). Nothing has to be on the machine first, and
there is no winget step.

> **This changed.** The Director and Gateway used to ship framework-dependent, and
> the missing .NET runtime was the one thing that could stop a fresh install dead -
> it is the entire reason the installer had a Prerequisites screen with a gate on
> it. Both are self-contained now and that screen is gone (`WizardStepFlow`). The
> exe is bigger (the Director goes from roughly 37 MB to roughly 118 MB); it buys
> an installer that cannot dead-end on a clean machine. Any document still telling
> a reader to `winget install Microsoft.DotNet.AspNetCore.10` is wrong.

The Cockpit is a static React bundle (no runtime of its own) served in-process by
the Gateway; it ships as the `devthrottle-gateway-cockpit-win-x64.zip` side-car,
unpacked into `wwwroot/c` beside the Gateway exe.

### No API key is required, for any install

A self-hosted Gateway install has exactly **one** extra requirement, and it is the
platform: the managed Gateway is a Windows-only tray app (`GatewayInstallPreflight`).

There is deliberately **no `OPENAI_API_KEY` requirement** - not at install time,
not afterwards, not for any role. There is no bring-your-own-key anywhere in the
product. Inference routes through DevThrottle on the account-minted `dt_live_`
key, which the runtime mints and stores itself after account sign-in; the vault
key name is `DEVTHROTTLE_API_KEY`, never `OPENAI_API_KEY`
(`TranscriptionEndpointResolver`). Demanding an OpenAI key here would block a
Gateway refresh on machines that never needed one.

> **This changed.** Earlier versions of this spec told the reader to run
> `setx OPENAI_API_KEY "sk-..."` before a Gateway install and described a
> one-time vault bootstrap from that variable. Both are gone. Anything that still
> says otherwise is wrong and must be reconciled to this document.

Separately, and unrelated to install: a handful of standalone `cc-*` tools
(cc-image, cc-voice, cc-whisper, cc-transcribe, cc-computer) call OpenAI directly
and read their own `OPENAI_API_KEY` from the user's environment (on a machine
with cc-secrets: `cc-secrets run openai-api-key -- <tool>`). That is a per-tool credential the user opts into. It is
not a DevThrottle requirement and it is not part of any install.

No elevation: the Gateway is a per-user tray app; the installer extracts the
Cockpit and mobile static files beside the exe, starts the tray app with
`--managed`, and the app registers its own HKCU Run-key autostart.

---

## 5. Using the CLI

The headless front-end (`cc-director-setup-cli`) and the WPF installer UI share
one engine, so a human and an agent install/update identically. Commands:

The binary's assembly name is `cc-director-setup-cli`; it ships on the release as
`devthrottle-setup-cli-win-x64.exe` / `devthrottle-setup-cli-mac-arm64`.

```
cc-director-setup-cli components               # list known components + roles + assets
cc-director-setup-cli status                   # installed components and their versions
cc-director-setup-cli prereqs                  # check for a coding agent
cc-director-setup-cli plan                     # show what an install/update would change
cc-director-setup-cli install                  # STEP 1: install/update the Director set
cc-director-setup-cli signin                   # sign in (or create a free account)
cc-director-setup-cli enroll --hosted          # STEP 2: join DevThrottle's hosted gateway
cc-director-setup-cli enroll [--gateway <url>] # STEP 2, self-hosted: sign in, then join
cc-director-setup-cli update                   # download, verify, apply updates
cc-director-setup-cli rollback <component>     # restore the previous build and pin away
cc-director-setup-cli uninstall                # remove install-owned files (data preserved)
cc-director-setup-cli autostart                # manage the logon autostart entry
```

`install` with no `--role` is the two-step model's Step 1: the Director set, no
account, no gateway. `signin` and `enroll` are Step 2 and are the only commands
that involve a person - they open a browser to sign in with Google, GitHub, or
email. Everything else runs unattended.

Common options:

| Option | Meaning |
|--------|---------|
| `--role workstation\|gateway` | Install type (default `workstation` = Director only). `gateway` additionally installs the self-hosted Gateway tray app; Windows only. The graphical installer never sets this. |
| `--gateway <url>` | Gateway to enroll against (`enroll`); omit to auto-discover from the account |
| `--hosted` | Enroll at DevThrottle's hosted gateway (`enroll`); not combinable with `--gateway` |
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

The Windows binaries are code-signed as Center Consulting Inc. in the release
pipeline: `cc-director.exe`, `devthrottle-gateway.exe`, `cc-launcher.exe`,
`cc-director-setup.exe` (the wizard) and `cc-director-setup-cli.exe`. SmartScreen
may still warn until the signing certificate builds reputation; choose
"More info" -> "Run anyway".

The macOS app is **ad-hoc signed, not Apple-notarized**, so a browser download of
it is Gatekeeper-blocked. That is why the macOS path is a `curl` bootstrap
(`scripts/install-mac.sh`) - a curl download is never quarantined.

---

## 8. References

- Plan / design decisions: `docs/plans/install-autoupdate.md`,
  `docs/plans/gateway-tray-app.md` (Gateway = tray app, service retired)
- Engine source: `tools/cc-director-setup-engine/`
- CLI source: `tools/cc-director-setup-cli/`
- Release pipeline: `.github/workflows/release.yml`
- Gateway scripts: `scripts/verify-gateway.ps1`, `scripts/redeploy-gateway.ps1`
  (the one deploy path - it ships the in-process React Cockpit too),
  `scripts/test-gateway-selfupdate.ps1`

> Reminder: this file is the master spec (see the banner at top). When you change
> install behavior, change THIS document first, then make the code match it.
</content>
