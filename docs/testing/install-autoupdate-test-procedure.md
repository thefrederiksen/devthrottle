# CC Director - Install & Auto-Update Test Procedure (agent handover)

> OUTDATED IN PART (2026-06-05): the Gateway is no longer a Windows service.
> Every `cc-gateway-service` / `sc.exe` / elevation step below is obsolete: the
> Gateway is a per-user TRAY app (HKCU Run key, POST /shutdown for stop, no
> admin anywhere). Use `scripts/verify-gateway.ps1` for the new checks and see
> docs/plans/gateway-tray-app.md (v2). The Workstation sections remain valid.
> Rewrite this procedure on the next full install QA pass.

**Audience:** an AI coding agent (like Claude Code) running on a **disposable Windows test machine**.
**You may delete anything cc-director on this machine** - it is not anyone's daily driver.

This procedure verifies, end to end and from real GitHub releases:
1. A clean machine can be wiped of any prior cc-director install.
2. A **Workstation** install works (Director + CLI tools, no admin).
3. A **Gateway machine** install works (the always-on `cc-gateway-service` + Cockpit, admin once).
4. **Auto-update** works: publish a newer release and watch the running install move to it on its own,
   including the Gateway's **self-update with auto-rollback**.
5. A **single module** can be changed and shipped independently (per-component versioning).
6. **Uninstall** removes only install-owned files.

Produce an HTML report at the end (Section 9).

---

## 0. Ground rules for you, the test agent

**Your job is to TEST and REPORT - not to fix.** You do not know this codebase and you must not try to
diagnose or patch its code. When something does not work, you **file a GitHub issue** (Section 0a) and
move on to the next test. The maintainer monitors those issues and fixes the code. Do not edit source,
do not "work around" a failure, do not mark a broken thing as passing.

### 0a. Filing issues - the ONLY way you report failures

For **every** failure, error, blank/broken page, unexpected output, or "this should work and doesn't,"
open a GitHub issue on `thefrederiksen/devthrottle`, labeled `installation`, immediately:

```powershell
gh issue create --repo thefrederiksen/devthrottle --label installation `
  --title "[install-test] <short symptom> (<Test A/B/C/D or Clean/Uninstall>)" `
  --body @"
## What I did
<the exact command(s) or wizard step>

## Expected
<what should have happened>

## Actual
<what happened - paste the exact output/error, verbatim>

## Environment
- Machine: test box (disposable)
- OS: $([System.Environment]::OSVersion.VersionString)
- Release under test: <vBASE / vNEXT tag>
- Tailnet host: <this machine's tailscale DNSName, if relevant>

## Logs
<relevant lines from %ProgramData%\cc-director\logs and %LOCALAPPDATA%\cc-director\logs\setup-cli.log>
"@
```

Rules for issues:
- **Label every one `installation`** (the maintainer monitors that label). The label already exists.
- **One issue per distinct problem.** Don't bundle unrelated failures.
- Title prefix **`[install-test]`** and name the test it came from, so they're easy to triage.
- **Redact secrets** - never paste an `OPENAI_API_KEY` or token into an issue. If a log line contains
  one, replace it with `***` and ALSO file an issue that a secret leaked.
- Keep going after filing: a failure in one test should not stop you from running the others. If a test
  is fully blocked, file the issue, note "blocked - skipping," and continue.
- When ALL tests are done, also post one summary issue titled `[install-test] Run summary <date>` that
  lists each test and PASS/FAIL with links to the per-failure issues (and attach/reference the HTML
  report from Section 9).

### Other ground rules

- **Never hardcode another machine's tailnet identity.** Derive THIS machine's:
  `& "C:\Program Files\Tailscale\tailscale.exe" status --json | ConvertFrom-Json | %{ $_.Self.DNSName }`
  and `tailscale ip -4`. Use that host in all tailnet URLs you build/report.
- **Show URLs as the tailnet host, never localhost**, in anything a human reads (localhost is fine for
  your own probes from this machine).
- **Report faithfully.** If a step fails, say so with the output. Never claim PASS while an error is on screen.
- **ASCII only** in any output/scripts you write.
- The Gateway install/uninstall needs **Administrator** rights (UAC). If you cannot elevate, ask the
  human to approve the UAC prompt, or run from an Administrator shell.

---

## 1. Prerequisites (verify before starting; STOP if missing)

```powershell
# 1. GitHub CLI authenticated (to download - and, for the auto-update test, cut - releases)
gh auth status

# 2. The repo, if you will cut releases or build locally (clone if absent)
#    git clone https://github.com/thefrederiksen/devthrottle  (path of your choice)

# 3. .NET 10 SDK (only needed if building locally; the published installer is self-contained)
dotnet --version    # expect 10.0.x

# 4. OPENAI_API_KEY in the USER environment - the Gateway service needs it to start.
[Environment]::GetEnvironmentVariable('OPENAI_API_KEY','User')   # must be non-empty for the Gateway test
#    If empty, ask the human for the key, then: setx OPENAI_API_KEY "sk-..."  (opens a NEW shell to take effect)

# 5. An agent framework on PATH (the installer requires one): Claude Code or Codex
where claude   # or: where codex

# 6. Tailscale installed + logged in (for the Gateway tailnet checks)
& "C:\Program Files\Tailscale\tailscale.exe" status
```

Also copy `scripts/verify-gateway.ps1` from the repo to this machine (you will run it repeatedly).

---

## 2. Which release to install

Auto-update only exists from the commit series that added it (phases 1-5), which landed **after**
`v0.3.7`. So:

- **Baseline release `vBASE`** (the build you install first) **must contain the auto-update work.**
  If the latest published release predates it, a coordinator must cut a fresh release from `main` first
  (see Section 8 for how). Confirm `vBASE` is auto-update-capable before relying on Section 5.
- **Update release `vNEXT`** is what you publish during the auto-update test (Section 5) so the running
  `vBASE` install can move to it.

Check the current latest:
```powershell
gh api repos/thefrederiksen/devthrottle/releases/latest --jq '.tag_name'
```

---

## 3. Clean the machine (remove any prior cc-director)

This machine is disposable, so wipe thoroughly. Run an **Administrator** PowerShell.

```powershell
# Stop + delete ALL cc-director / gateway services (current and any stale older names)
foreach ($s in @('cc-gateway-service','cc-director-gateway','cc_director')) {
  & sc.exe stop $s 2>$null | Out-Null
  & sc.exe delete $s 2>$null | Out-Null
}

# Kill any leftover processes
Get-Process cc-director,cc-director-gateway,cc-director-cockpit,cc-director-gateway-tray -ErrorAction SilentlyContinue | Stop-Process -Force

# Remove install locations (disposable machine: OK to wipe the per-user root entirely)
Remove-Item "$env:ProgramFiles\CC Director" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\cc-director"  -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\cc-director" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "C:\cc-tools" -Recurse -Force -ErrorAction SilentlyContinue   # retired tools root, if present

# Remove the bin PATH entry + Start Menu shortcut
$p = [Environment]::GetEnvironmentVariable('Path','User')
if ($p) { [Environment]::SetEnvironmentVariable('Path', (($p -split ';' | Where-Object { $_ -notmatch 'cc-director\\bin' }) -join ';'), 'User') }
Remove-Item (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\CC Director.lnk') -ErrorAction SilentlyContinue

# Reset Tailscale Serve (clears stale per-Director mappings that accumulate)
& "C:\Program Files\Tailscale\tailscale.exe" serve reset 2>$null
```

**Verify clean:**
```powershell
Get-Service cc-gateway-service -ErrorAction SilentlyContinue   # expect: nothing
Test-Path "$env:ProgramFiles\CC Director"                       # expect: False
Get-NetTCPConnection -State Listen -LocalPort 7470,7878 -ErrorAction SilentlyContinue  # expect: nothing
```

---

## 4. Test A - Workstation install (no admin)

```powershell
# Download the wizard from the baseline release (set $TAG to vBASE)
$TAG = 'vBASE'
gh release download $TAG --repo thefrederiksen/devthrottle --pattern cc-director-setup-win-x64.exe --dir $env:USERPROFILE\Downloads --clobber
& "$env:USERPROFILE\Downloads\cc-director-setup-win-x64.exe"
```
In the wizard: **Welcome -> select Workstation -> Next through to Install -> Complete.**

**Verify (open a NEW terminal so PATH is fresh):**
```powershell
Test-Path "$env:LOCALAPPDATA\cc-director\app\cc-director.exe"   # Director present
(Get-ChildItem "$env:LOCALAPPDATA\cc-director\bin" -Filter *.exe).Count   # ~26 tools
cc-pdf --help                                                   # a tool resolves on PATH
Get-ChildItem (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\CC Director.lnk')  # shortcut
```
- PASS = Director launches from the Start Menu shortcut, tools resolve in a new shell.
- Record the installed versions: `Get-Content "$env:LOCALAPPDATA\cc-director\config\setup\installed.json"`

---

## 5. Test B - Gateway machine install (admin)

Confirm `OPENAI_API_KEY` is set (Section 1). Re-run the **same wizard**, pick **Gateway**, approve the
**UAC** prompt. (Or, headless: from an Administrator shell, download `cc-director-setup-cli-win-x64.exe`
and run `cc-director-setup-cli install --role gateway`.)

**Verify:**
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File <path>\verify-gateway.ps1
```
Expect **RESULT: PASS** with: Service installed, Auto-start, **Native service (not NSSM)**, Service
running, `/healthz` (7878) OK, Cockpit (7470) OK, exes at the canonical `C:\Program Files\CC Director\...`.
The script prints the **tailnet** Cockpit + Gateway URLs - open the Cockpit URL it prints from another
tailnet device and confirm the page renders (not blank/400).

**Survives reboot:** reboot, then re-run `verify-gateway.ps1` -> still PASS (service auto-started).

**Tailnet note / known issue:** raw `http://<tailnet-ip>:7470` may return 400; use the HTTPS Serve URL
the verify script prints (`https://<host>:7470` or the mapped port). If the Cockpit page is blank over
the tailnet, capture the browser console + the Gateway logs at `%ProgramData%\cc-director\logs` and
report - do not silently pass.

---

## 6. Test C - Auto-update (the headline test)

Goal: a published `vNEXT` reaches the running `vBASE` install with no manual install.

1. **Confirm the baseline is auto-update-capable** and note the cadence:
   - Default cadence is every 6h; shorten it for the test by writing the config:
     ```powershell
     $cfg = "$env:LOCALAPPDATA\cc-director\config\config.json"
     $j = if (Test-Path $cfg) { Get-Content $cfg -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
     $j | Add-Member -NotePropertyName autoUpdate -NotePropertyValue (@{ enabled=$true; intervalHours=0.25 }) -Force
     $j | ConvertTo-Json -Depth 10 | Set-Content $cfg -Encoding UTF8
     ```
   - (Kill switch for reference: `CC_AUTOUPDATE=0` disables all auto-update.)

2. **Publish `vNEXT`** (a coordinator with push rights, or you if this machine has the repo + gh auth) -
   see Section 8. Make the change **visible** (e.g., a version-string bump that shows in the Cockpit/UI)
   so propagation is observable.

3. **Trigger / wait:**
   - **Tools + Director:** relaunch the Director (or wait one interval). It checks on launch and on the
     cadence; tools swap in place, the Director self-update applies on its next restart.
   - **Gateway + Cockpit:** the Gateway worker checks on the cadence; when it finds `vNEXT` it launches
     the detached self-update helper -> the service stops, swaps, restarts, health-checks, and the
     Cockpit updates on relaunch. To force it promptly, restart the service:
     `sc.exe stop cc-gateway-service; sc.exe start cc-gateway-service` (the supervisor applies the
     Cockpit update before launch on start).

4. **Verify propagation to `vNEXT`:**
   ```powershell
   Get-Content "$env:LOCALAPPDATA\cc-director\config\setup\installed.json"   # tool/director/cockpit/gateway versions
   (Get-CimInstance Win32_Service -Filter "Name='cc-gateway-service'").PathName
   powershell -NoProfile -ExecutionPolicy Bypass -File <path>\verify-gateway.ps1   # PASS on the new build
   ```
   PASS = installed.json shows `vNEXT` for the components, Gateway/Cockpit still healthy, the visible
   change is present.

5. **Auto-rollback (DA-1):** prove a bad build does NOT brick the service. Easiest is the bundled test
   against a **throwaway** service (never touches the live one):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\scripts\test-gateway-selfupdate.ps1
   ```
   Expect: happy path -> Updated + healthy; rollback path -> RolledBack + the service healthy again on
   the previous build (bad version pinned). (This needs the repo + .NET SDK to build the test Gateway.)

---

## 7. Test D - A single module changes independently

Demonstrate per-component updates (you do NOT have to bump everything):
- Change **one** component (e.g., the Cockpit, or one tool), and in `scripts/release-asset-versions.json`
  bump **only that asset's** version, then publish. Auto-update should refresh **only** that component;
  everything else stays put. Verify via `installed.json` before/after.

---

## 8. How to publish a release (for `vBASE` / `vNEXT`)

From a clone of the repo with `gh` authenticated and push rights:
```powershell
# Bump the 5 version files to the new version (e.g., 0.3.8):
#   src\CcDirector.Avalonia\CcDirector.Avalonia.csproj            (<Version>)
#   tools\cc-director-setup\CcDirectorSetup.csproj                (<Version>)
#   tools\cc-director-setup\MainWindow.xaml                       (Text="vX.Y.Z")
#   tools\cc-director-setup-avalonia\CcDirectorSetup.csproj       (<Version>)
#   tools\cc-director-setup-avalonia\MainWindow.axaml             (Text="vX.Y.Z")
git commit -am "release: vX.Y.Z"
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin main; git push origin vX.Y.Z
# CI (.github/workflows/release.yml) builds + publishes; ~5-10 min. Watch:
gh run watch (gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
```
A non-prerelease tag (no `-` in it) becomes **Latest**, which is what auto-update pulls. Re-using a tag
needs the prior release + tag deleted first (delete release objects by id via the API, then the tag).

**Gotcha:** if a tool drops from a release, the build's "Verify release completeness" step warns; the
release still ships. Required assets (apps/installer/CLI/mac) are hard-gated - a missing one fails CI.

---

## 9. Report

Two outputs, both required:

1. **GitHub issues** (the channel the maintainer acts on): a `installation`-labeled issue for **each**
   failure as it happens (Section 0a), plus one `[install-test] Run summary <date>` issue at the end
   listing every test with PASS/FAIL and links to the per-failure issues.

2. **HTML summary** (for the human reading along): `cc-html` (boardroom theme) report capturing, per test
   (A-D + Clean + Uninstall): PASS/FAIL, the key command outputs, `installed.json` before/after for the
   auto-update test, and screenshots of the running Director and the Cockpit (tailnet URL). Note the live
   elevated steps a human performed. Reference the issue numbers you filed.

Remember: you do not fix anything. A clean run files no issues; a run with problems files one issue per
problem so the maintainer can pick them up by the `installation` label.

## 10. Uninstall test (do last, or on a fresh pass)

```powershell
# Elevated:
cc-director-setup-cli install --role gateway   # (only if testing reinstall) -- otherwise:
cc-director-setup-cli uninstall --role gateway
```
Verify only install-owned files are gone (service, %ProgramFiles%\CC Director, app/bin, PATH, shortcut)
and the machine is back to the Section 3 "clean" state.

---

## Appendix - troubleshooting cheatsheet

- **Gateway won't start / unhealthy:** check `%ProgramData%\cc-director\logs` (stderr/stdout) and the
  engine log `%LOCALAPPDATA%\cc-director\logs\setup-cli.log`. Common cause: `OPENAI_API_KEY` not set.
- **Cockpit blank/400 over tailnet:** use the HTTPS Serve URL, not raw `http://<ip>:7470`. If Serve has
  stale mappings, `tailscale serve reset` and re-register (the Gateway re-provisions on restart).
- **"must run elevated":** the Gateway install/uninstall needs Administrator.
- **PATH tools not found:** open a NEW shell after install (PATH only refreshes for new processes).
- **A secret appeared in a log:** the installer redacts OPENAI_API_KEY; if you ever see one, it predates
  the redaction fix - report it and rotate the key.
- **Don't commit** the uncommitted parallel Cockpit/Tailnet work that may exist in a dev clone; only the
  coordinator handles that.
