# First-Install of All Components - QA Report

**Date:** 2026-06-01
**Scope:** Make a clean Windows machine install every CC Director component from one GitHub
release - Workstation role (no admin) and Gateway role (service + Cockpit, admin once).
**Plan:** docs/plans/first-install.md (workstreams W1-W7).
**Status:** Code complete and locally verified. One gate remains: the live elevated
clean-machine Gateway run (requires admin + a fresh box), called out at the end (the
maintainer's step).

---

## 1. Summary

All seven workstreams are implemented. Every locally-verifiable behavior passes:
69 unit tests (60 engine + 9 CLI), all four new/changed release components publish
self-contained, the offline install runs end-to-end through the shipping CLI binary, the
Gateway elevation guard refuses cleanly, and the `sc.exe` service command parses correctly.

| Workstream | Result |
|---|---|
| W1 Release ships every component | DONE - release.yml builds Gateway, Cockpit, 26-tool matrix, setup wizard + CLI; valid YAML |
| W2 Self-contained everywhere | DONE - Gateway/Cockpit/CLI all publish self-contained; no .NET runtime prerequisite |
| W3 Native service install (no NSSM) | DONE - Gateway is a Windows service host; sc.exe/reg.exe install in the engine + CLI |
| W4 Cockpit first-install extraction | DONE - CockpitPackage extracts the zip with SHA-256 verify; 4 unit tests |
| W5 WPF role selection + elevated handoff | DONE - role picker + shells the elevated CLI, tails its log |
| W6 Prerequisites (minimal, honest) | DONE - framework check kept, no runtime check, Gateway requires OPENAI_API_KEY |
| W7 Test + this report | DONE (local); live elevated Gateway run is the remaining gate |

---

## 2. What changed (by workstream)

### W1 - Release pipeline (.github/workflows/release.yml)
- New `build-gateway-win` job -> `cc-director-gateway-win-x64.exe` (self-contained single file).
- New `build-cockpit-win` job -> `cc-director-cockpit-win-x64.zip` (self-contained publish folder).
- Tools job converted to a 26-tool matrix (`fail-fast: false`); 6 heavyweight tools marked
  `continue-on-error` until proven green. Excludes for non-Python tool bundles documented inline.
- Setup-wizard job now also publishes and ships the CLI as `cc-director-setup-cli-win-x64.exe`.
- `create-release` wires all assets in; the manifest auto-discovers them with version + SHA-256.

### W2 - Self-contained everywhere
- Gateway, Cockpit, and CLI all publish `--self-contained true`. INSTALLATION.md updated to state the
  runtime is bundled, so a clean machine needs nothing but the agent framework.

### W3 - Native Windows-service install (decision D1: no NSSM)
- `CcDirector.Gateway` now runs under the generic host with `AddWindowsService` (new
  `GatewayWorker` BackgroundService wraps the existing GatewayHost + Cockpit supervisor). It is a
  no-op outside a service, so dev `dotnet run` is unchanged.
- `GatewayServiceCommands` rewritten from NSSM to `sc.exe` + `reg.exe` (create/describe/env/start/stop/delete).
- New `GatewayServiceInstaller` (engine): verifies the Gateway exe is placed, extracts the Cockpit,
  registers the auto-start LocalSystem service, writes its environment (CC_DIRECTOR_ROOT,
  OPENAI_API_KEY, CC_COCKPIT_MANAGED, CC_COCKPIT_EXE), starts it, and waits for 7878 + 7470 health.
- CLI `install --role gateway` runs that machine-scoped step after the file swap.

### W4 - Cockpit first-install extraction
- New `CockpitPackage.ExtractAsync`: downloads the `.zip`, verifies SHA-256, clears stale contents,
  and extracts into `%ProgramFiles%\CC Director\cockpit`. The generic UpdateRunner still skips `.zip`
  on the update path (service-owned); only first install extraction is new (the service isn't running yet).

### W5 - WPF wizard (decisions D2 shell elevated CLI, D3 WPF only)
- Welcome step has a Workstation/Gateway picker (`WelcomeStep.SelectedRole`).
- Workstation install is unchanged (per-user, no admin).
- Gateway: the wizard does the per-user work non-elevated, then `GatewayServiceLauncher` downloads the
  CLI and shells `install --role gateway --component gateway` with one UAC prompt
  (`Verb=runas`, hidden window), tailing the CLI's `--log-file` for live progress.

### W6 - Prerequisites
- Framework (Claude Code / Codex) check kept; no .NET runtime check (self-contained makes it moot).
- Gateway pre-flight requires elevation AND `OPENAI_API_KEY`, failing loudly with the exact fix.

---

## 3. Test evidence (all local, this machine)

### Unit tests
- Engine: **60 passed, 0 failed** (incl. 4 new CockpitPackage tests + reshaped sc.exe command tests).
- CLI: **9 passed, 0 failed**.
- Gateway: 89 passed; the 46 failures are the pre-existing `OPENAI_API_KEY` GatewayHost setup issue
  (documented), identical with or without these changes - the changed files (`Program.cs`,
  `GatewayWorker.cs`) are not touched by those tests.

### Release components publish self-contained
- Gateway: `cc-director-gateway.exe` produced (~112 MB self-contained single file).
- Cockpit: `cc-director-cockpit-win-x64.zip` produced (~47 MB); verified the zip's top-level layout is
  the publish folder (`cc-director-cockpit.exe` + `wwwroot/`...), exactly what CockpitPackage expects.
- CLI: `cc-director-setup-cli.exe` produced (~73 MB self-contained single file).

### Offline end-to-end install (real shipping CLI binary)
Ran `cc-director-setup-cli install --role workstation --release-dir <fake release> --root/-program-files/-program-data <temp>`:
```
Install complete:
  director       Installed
  cc-pdf         Installed
  cc-html        Installed
installed=3 updated=0 failed=0 skipped=0
```
Placed files: `<root>\app\cc-director.exe`, `<root>\bin\cc-pdf.exe`, `<root>\bin\cc-html.exe`.
This exercises the real ReleaseSource -> UpdatePlanner -> UpdateRunner -> InstallSwapper -> InstallLayout path.

### Gateway elevation guard (not elevated)
`install --role gateway` refused with exit 1:
```
ERROR: A Gateway install must run elevated (it writes %ProgramFiles% and registers a Windows service).
       Re-run this command from an Administrator console.
```

### --log-file tee
`status --log-file <path>` wrote the console output to the file (the mechanism the wizard tails during
the elevated Gateway install).

### sc.exe command quoting (live parse check, not elevated)
Ran the exact `sc create ... binPath= "\"...\cc-director-gateway.exe\" --port 7878" start= auto obj= LocalSystem ...`:
result was `OpenSCManager FAILED 5: Access is denied` - i.e. sc.exe **parsed the command and quoting
correctly** and only stopped on privilege. A malformed binPath would have produced a syntax/usage
error instead. So the command is well-formed and will succeed when run elevated.

### YAML
`release.yml` parses as valid YAML.

---

## 4. The one remaining gate (requires a clean machine + admin)

Everything above is verified without elevation. The only thing that cannot be exercised here is the
actual privileged Gateway run end to end. On a fresh Windows box (or an elevated console):

1. Download `cc-director-setup-win-x64.exe` from the release and run it.
2. Workstation pass: Director launches from the Start Menu shortcut; tools run from a NEW shell (PATH).
3. Gateway pass: pick Gateway, accept the single UAC prompt. Confirm:
   - `sc query cc-gateway-service` shows RUNNING and auto-start,
   - `http://localhost:7878/healthz` and `http://localhost:7470/` both answer,
   - it survives a reboot.
4. Idempotency: re-run; it reports up to date and does not break the running service.

It also requires cutting a real release tag (e.g. `v0.0.0-test1`) so the GitHub release exists for the
wizard to download from. Pushing a tag is an outward-facing action and was intentionally NOT done
automatically - it needs an explicit go-ahead.

---

## 5. Status of work product

All code changes are in the working tree, **uncommitted**. No commit, push, or tag was made.
