# Installer + Gateway Clean-Slate Test Plan (and installer-session brief)

> Status: PLAN ONLY - nothing here has been executed. This doc is both the test
> procedure and the mission brief for the dedicated "installer" session.
> Authoritative install spec: [docs/install/INSTALLATION.md](../install/INSTALLATION.md).
> Existing procedures to fold in: [docs/testing/install-autoupdate-test-procedure.md](install-autoupdate-test-procedure.md),
> [docs/install/INSTALLER_HANDOVER.md](../install/INSTALLER_HANDOVER.md).

## Goal

On THIS machine (the dev box, which is currently the live production Gateway):
remove the Gateway + Cockpit completely, do a fresh Gateway-role install of
v0.6.13, and verify the whole thing comes up - including the new key vault.
The real end-to-end test the vault feature has been waiting for.

## The headline issue to resolve FIRST (blocker)

The installer requires `OPENAI_API_KEY` in the user environment and **fails the
Gateway install** if it's missing (`GatewayTrayInstaller` step 1; INSTALLATION.md
section 4). But v0.6.13's key vault sets the OpenAI key from the **Cockpit Keys
page** after install, and the Director's `OpenAiKeyResolver` has **no env
fallback**. These two models now conflict.

Decide one (then make code + master spec agree - INSTALLATION.md is authoritative):
- **A. Env var is a one-time bootstrap that seeds the vault.** Install still reads
  `OPENAI_API_KEY` from the env, but writes it into the vault (`keyvault.json`)
  so the Cockpit shows it as set and Directors pull it. Cockpit can rotate later.
- **B. Drop the env requirement.** Gateway installs with no key; the Cockpit Keys
  page is the only way to set it; dictation is "unavailable, set a key" until then.
- **C. Keep both, documented.** Env var required at install AND settable in Cockpit;
  define precedence (vault wins, per the resolver). Riskiest for user confusion.

Recommendation to evaluate in-session: **A** (bootstrap-into-vault) - keeps the
loud "no silent degrade" install gate while making the Cockpit the single live
source going forward.

## Safety / blast radius (READ BEFORE RUNNING)

- This takes down the **live Gateway + Cockpit on this machine**. The Directors
  registered to it (fleet view, Cockpit, turn briefs) go dark until reinstall.
  Do it only when you're ready to lose that for ~15-30 min.
- `uninstall --role gateway` removes ONLY install-owned files (gateway\ + cockpit\
  + the `CcDirectorGateway` Run key). It does **not** touch Director apps
  (main + slots 1-4) or your data (config\, vault\). Never kill the user's
  Director processes by hand.
- Back up first anyway (below).

## Procedure

### 0. Pre-flight backup + baseline
- Copy `%LOCALAPPDATA%\cc-director\config\` aside (holds `config.json` with the
  `gateway` block, and `keyvault.json` once the vault has keys).
- Record the autostart value: `HKCU\...\Run\CcDirectorGateway`.
- Baseline: `cc-director-setup-cli status --json` and
  `powershell -File scripts\verify-gateway.ps1` (expect PASS now).

### 1. Remove Gateway + Cockpit completely
- Dry run: `cc-director-setup-cli uninstall --role gateway --plan`
- Apply:   `cc-director-setup-cli uninstall --role gateway`
- For a TRULY clean key test, also clear the vault: delete
  `%LOCALAPPDATA%\cc-director\keyvault.json` (it is your data; uninstall preserves it).
- Confirm gone: `verify-gateway.ps1` should now FAIL; ports 7878 (gateway) and
  7470 (cockpit) dead; `gateway\` and `cockpit\` dirs removed; Run key absent.

### 2. Fresh Gateway install of v0.6.13
- Source: the published release (cc-director-setup.exe from GitHub v0.6.13) OR
  `cc-director-setup-cli install --role gateway --manifest latest`.
- This is where the headline issue bites: if `OPENAI_API_KEY` is not in the user
  env, the install fails. Use the test to validate whichever decision (A/B/C)
  we land on; for now set it (`setx OPENAI_API_KEY <key>`) to get past the gate,
  then ALSO set it via the Cockpit Keys page and observe which one dictation uses.
- Install does: places gateway exe, extracts Cockpit, starts `--managed`,
  registers Run key, waits for 7878 + 7470.

### 3. Set up the Gateway + point a Director at it
- Local Directors auto-register via filesystem discovery (same machine, same root).
- Confirm a Director shows up: Gateway tray Settings shows Directors >= 1;
  Cockpit /directors lists it.

### 4. Set the OpenAI key in the vault (the new flow)
- Open the Cockpit -> **API Keys** -> set `OPENAI_API_KEY`.
- Confirm `keyvault.json` now has the key (name only) and the page shows "is set".

### 5. Verify end to end
- `scripts\verify-gateway.ps1` -> PASS (exe, Run key, process, 7878, 7470).
- Cockpit loads via the Tailscale front door (one URL).
- **Dictation (the real test):** Cockpit Speak (streams to the owning Director's
  /dictate, which pulls the key from the vault) transcribes; desktop Speak works.
- Standalone check: a Director with no gateway.url uses Settings > Voice key.

### 6. Persistence + restore
- Log off / log on once; re-run `verify-gateway.ps1` -> tray app auto-restarted.
- If anything is wrong, restore the config\ backup; Directors reconnect on the
  next discovery/heartbeat.

## Deliverable from the session
- A short QA report (pass/fail per step, screenshots of the Cockpit Keys page and
  a working dictation).
- A decision + edit: reconcile the `OPENAI_API_KEY`-vs-vault seam in
  INSTALLATION.md (master spec first), then `GatewayTrayInstaller` / installer copy.
- File issues for any gaps found.

## Session brief (seed for the installer session)
- Name: "Installer + Gateway clean-slate test"
- Repo: D:\ReposFred\devthrottle
- Mission: execute this plan; resolve the env-var-vs-vault seam; keep
  INSTALLATION.md authoritative; never kill the user's Director processes; HOLD on
  the destructive steps (section 1+) until Soren explicitly says go.
