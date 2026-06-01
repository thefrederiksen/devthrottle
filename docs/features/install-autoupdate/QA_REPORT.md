# CC Director Install & Auto-Update Engine - QA Report

Date: 2026-06-01
Scope: Windows install/update engine + CLI front-end (the testable core of all
plan phases in `docs/plans/install-autoupdate.md`).
Build: .NET 10.0.300, all projects `TreatWarningsAsErrors=true`.

---

## 1. Summary

A headless install/update engine (`CcDirector.Setup.Engine`) and a CLI front-end
(`cc-director-setup-cli`) were built and tested. They implement the decisions
agreed in the plan: one shared engine behind a CLI front-end, a Workstation vs
Gateway role model, independent-per-component versioning, silent-auto update
planning, swap-with-backup plus rollback, and the Gateway self-update command
sequence.

- 61 automated tests pass (52 engine, 9 CLI). 0 warnings, 0 failures.
- The CLI was exercised live: `help`, `components`, `status`, `prereqs`, `plan`,
  `update --dry-run`, the no-download error path, and `rollback` all behave
  correctly, including against the REAL published GitHub release and a REAL
  installed Gateway on this machine.
- No existing C# was modified (the shipping WPF installer is untouched). The only
  change to an existing file is `.github/workflows/release.yml` (manifest now
  carries a per-asset version), validated as still-parsing YAML.

The full end-to-end wizard run on a clean machine remains a separate exercise (as
agreed); this report covers what could be verified individually and in isolation.

---

## 2. What was built

### Engine library - `tools/cc-director-setup-engine/`
A plain `net10.0` library (no WPF, no network) so it is reusable by the CLI, the
WPF wizard, the Director, and the Gateway, and is fully unit-testable.

| File | Responsibility |
|------|----------------|
| `InstallRole.cs`, `ComponentKind.cs`, `Component.cs` | The component model |
| `ComponentRegistry.cs` | Canonical component list; role -> component mapping (Gateway = Workstation superset) |
| `InstallLayout.cs` | Where each component lives on disk (roots injectable) |
| `VersionUtil.cs` | Version parse/normalize/compare (handles `v`, `-rc`, `+githash`, 4-part) |
| `ReleaseManifest.cs` | Parse manifest with PER-ASSET versions (the independence mechanism) |
| `InstalledStateReader.cs` | Present? which version? (file + version readers injectable) |
| `UpdatePlanner.cs` + `UpdatePlan.cs` | Pure decision: Install / Update / UpToDate / MissingAsset / Pinned |
| `UpdatePin.cs` | Rollback version pins so silent-auto does not re-stage a bad build |
| `Hashing.cs` | SHA-256 verify against the manifest |
| `InstallSwapper.cs` | Atomic swap keeping `.old` backup; rollback restore |
| `UpdateRunner.cs` | Execute a plan: download (injected) -> verify SHA -> swap |
| `GatewayServiceCommands.cs` | NSSM stop/start/status sequence for Gateway self-update |
| `Orchestrator.cs` | The single "update everything in scope" entry resident apps call |

### CLI front-end - `tools/cc-director-setup-cli/`
A thin `net10.0` console exe over the engine (`AssemblyName=cc-director-setup-cli`).

Commands: `components`, `status`, `prereqs`, `plan`, `update`, `install`,
`rollback`, `help`. Global options: `--role workstation|gateway`,
`--manifest <path|latest>`, `--component <id|all>`, `--tools`, `--root`,
`--service-root`, `--dry-run`, `--json`. Exit codes: 0 ok, 1 error, 2 usage,
3 prerequisite missing.

### Release pipeline - `.github/workflows/release.yml` (modified)
The manifest generator now emits a `version` per asset. Default = the release
version (backward compatible; identical effective behavior to before). An
optional `scripts/release-asset-versions.json` map overrides individual assets so
a single tool can be bumped without a full release. Fails loudly if that map is
present but `jq` is unavailable (no silent no-op).

---

## 3. Test results

Command: `dotnet test <project>`

```
CcDirector.Setup.Engine.Tests   Passed: 52, Failed: 0, Skipped: 0
CcDirector.Setup.Cli.Tests      Passed:  9, Failed: 0, Skipped: 0
```

Coverage by area:

| Area | Tests | Key scenarios |
|------|-------|---------------|
| VersionUtil | 13 | normalize `v`/`-rc`/`+hash`/4-part; IsNewer; null safety |
| ReleaseManifest | 8 | per-asset versions; legacy inherit; invalid-manifest throws |
| ComponentRegistry | 5 | apps+tools; role scoping; Gateway is a superset of Workstation; duplicate-id reject |
| InstallLayout | 4 | app/bin/service paths; empty-root reject |
| UpdatePlanner | 5 | install/update/up-to-date/missing-asset; INDEPENDENT versioning; pinned-skip |
| InstallSwapper | 5 | fresh install; swap keeps `.old`; rollback restores + consumes backup; missing-source throws |
| UpdateRunner | 5 | install; update + backup; SHA-mismatch fails leaving target untouched; zip skipped |
| Gateway/Pins | 6 | restart sequence stop->start->status; pin blocks matching version; JSON round-trip |
| Orchestrator | 2 | no-work returns null run; installs a missing component |
| CLI args | 5 | command/options/flags; positionals; default-to-help; flag does not swallow next option |
| Framework detect | 4 | finds launcher by PATHEXT and bare name; null when absent |

The single key invariant - "only the component that is actually behind gets
updated" - is asserted directly in
`UpdatePlannerTests.IndependentVersioning_OnlyBehindComponentUpdates`
(Director current, cc-html current, only cc-pdf flagged).

---

## 4. Live verification (CLI exercised by hand)

All run against the built `cc-director-setup-cli.dll`.

### 4.1 Role scoping (correct superset behavior)
`components --role workstation` lists Director + tools only.
`components --role gateway` adds `gateway` + `cockpit`. Confirmed.

### 4.2 Framework prerequisite (decision D9: detect + guide)
```
prereqs ->
  claude   found (C:\Users\soren\.local\bin\claude.EXE)
  codex    not found
  exit=0
```

### 4.3 Plan against a local manifest (independent versions flow through)
```
plan --manifest <local> ->
  director  Install  install 0.4.0
  cc-pdf    Install  install 1.2.0
  cc-html   Install  install 1.1.0
  cc-word   Install  install 1.0.5
```
Each component carries its own target version - independence end to end.

### 4.4 REAL installed component read (no test stub)
With only `--root` overridden, `gateway`/`cockpit` resolved to the real
`C:\cc-tools`. The planner read the actually-installed Gateway's product version
(`1.0.0+<githash>`) and correctly judged it NOT behind the 0.4.0 test manifest
(`UpToDate`). Live `FileVersionInfo` reading + version comparison verified on a
real binary.

### 4.5 REAL GitHub release fetch (network)
```
plan --manifest latest ->   (fetched the real v0.3.5 release)
  director  Install  install 0.3.5
  cc-pdf    Install  install 0.3.5
  cc-html   Install  install 0.3.5
  cc-word   Install  install 0.3.5
```
`ReleaseSource.FetchLatestAsync` + parsing the real (legacy, no per-asset version)
manifest both work; missing per-asset versions correctly inherit `0.3.5`.

### 4.6 No-fallback failure path (honors the project rule)
A non-dry-run `update` against a LOCAL manifest (which has no download URLs) fails
loudly and clearly rather than silently degrading:
```
cc-pdf  Failed - No download URL for asset 'cc-pdf-win-x64.exe'. Use --manifest latest.
exit=1
```

### 4.7 Rollback (decision D8)
Seeded `bin/cc-pdf.exe` (bad) + `.old` (good), then `rollback cc-pdf`:
```
{ "component": "cc-pdf", "rolledBack": true, "pinnedAwayFrom": null }
cc-pdf.exe content after rollback: v1-good   (restored)
.old after rollback: absent                  (backup consumed)
```
`rollback` with no backup present reports clearly and exits 1.

### 4.8 Release manifest CI format
The exact `release.yml` manifest-generation bash was simulated locally; output is
valid JSON carrying the new per-asset `version` field, and the engine parsed it
via `plan`. YAML validated with `yaml.safe_load`.

---

## 5. Per-phase status (honest)

| Phase | Item | Status |
|-------|------|--------|
| 1 | Headless engine extracted into a testable core | DONE (implemented + 52 tests) |
| 1 | Rewire the WPF wizard to call the engine | NOT DONE - deliberately deferred to avoid destabilizing the shipping installer; the CLI proves the shared engine |
| 2 | CLI front-end (install/update/rollback/status/...) | DONE (implemented + 9 tests + live) |
| 3 | Workstation vs Gateway role | DONE in engine + CLI (`--role`); the UI toggle waits on the WPF rewire |
| 4 | Independent-per-component versioning | DONE (manifest per-asset version; planner judges each component independently; release.yml emits it) |
| 5 | Orchestration entry point (`Orchestrator.RunAsync`) | DONE (implemented + 2 tests) |
| 5 | Wire the loop into the running Director/Gateway processes | NOT DONE - integration into the live apps deferred |
| 6 | Rollback + `.old` backups (all components) | DONE (implemented + tested + live) |
| 6 | Gateway self-update restart sequence | DONE as a unit-tested command sequence; LIVE NSSM service swap deferred to the clean-machine run |
| 6 | Cockpit `.zip` extraction in the runner | NOT DONE - reported as Skipped by the generic runner; belongs to the Gateway-side path |
| 7 | Docs: SmartScreen "Run anyway" walkthrough; signing copy | NOT DONE |

---

## 6. Findings / notes for follow-up

1. ASSET-NAME MISMATCH IN THE EXISTING WPF TOOL. The shipping
   `tools/cc-director-setup/Services/ToolInstaller.cs` expects assets named
   `cc-pdf.exe` / `<tool>.zip`, but `release.yml` actually publishes
   `cc-pdf-win-x64.exe`. The new engine standardizes on the release-pipeline
   naming (`<id>-win-x64.exe`). When the WPF tool is rewired onto the engine this
   latent mismatch is resolved; until then the two disagree. Worth confirming the
   current wizard actually installs tools today.

2. DIRECTOR INSTALL PATH. The engine places the Director at
   `%LOCALAPPDATA%\cc-director\app\cc-director.exe` (matching the plan and
   `docs/install/install-prompt.md`), whereas the legacy `InstallDetector`/
   `ToolInstaller` use `...\bin\cc-director.exe`. Standardize on `app\` during the
   WPF rewire.

3. `jq` DEPENDENCY. The per-asset override map needs `jq` (preinstalled on
   `ubuntu-latest`; absent on this dev box, so that one branch was not exercised
   locally). The workflow now fails loudly if the map exists without `jq`.

4. LIVE GATEWAY SELF-UPDATE is the one piece that genuinely needs the
   empty/clean machine: stopping the LocalSystem service, swapping the locked exe
   in `C:\cc-tools`, and confirming NSSM relaunches it. The command sequence is
   built and unit-tested; the execution + detached-helper handoff is the clean-
   machine item.

---

## 7. How to use it (answers "install pieces / list components")

```
# list the components for a role
cc-director-setup-cli components --role gateway

# what is installed, and at what version
cc-director-setup-cli status --json

# what would an update change (no download)
cc-director-setup-cli plan --manifest latest

# update one component, or everything
cc-director-setup-cli update --component cc-pdf
cc-director-setup-cli update                 # all in scope
cc-director-setup-cli install --role gateway # role-scoped install

# step back a bad build
cc-director-setup-cli rollback director
```

---

## 8. Files

New (engine): `tools/cc-director-setup-engine/` - csproj + 18 source files.
New (engine tests): `tools/cc-director-setup-engine.Tests/` - csproj + 9 test files.
New (CLI): `tools/cc-director-setup-cli/` - csproj + 6 source files.
New (CLI tests): `tools/cc-director-setup-cli.Tests/` - csproj + 2 test files.
Changed: `.github/workflows/release.yml` (per-asset version in the manifest).
Plan reference: `docs/plans/install-autoupdate.md`.

Nothing was committed. The shipping WPF installer and all other existing C# are
untouched.
