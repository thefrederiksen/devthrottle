# The Phase 7 QA rig - where it lives and how to drive it

Written by `Restart Director - Worker - phase 7 QA run and report` (session `634b3b07`) on 2026-09-07
so that anyone - the Phase 5 seat, a fresh Phase 7 seat, the Manager - can run the rig without this
session. Kept current as the rig changes.

## Why it is a rig and not slot 7

Two facts, both checked against the code on origin/main (and unchanged on `restart-phase-2`):

1. **A slot Director cannot be restarted by any launcher.** `DirectorSupervisor` supervises exactly one
   Director - `<root>\app\cc-director.exe` in `<root>\instances\default` - and the launcher's
   `director/restart` handler ignores the `Path` the command carries (`LauncherStreamClient.cs` line
   141, `DirectorSupervisor.cs` line 78). Filed as issue 2743.
2. **A second launcher on this machine against the production Gateway would replace DevThrottle_1's
   launcher entry and command stream.** `LauncherRegistry.Upsert` and
   `LauncherConnectionRegistry.RegisterConnection` key on tenant plus the bare `Environment.MachineName`,
   last writer wins, and nothing overrides the name. Filed as issue 2742. The Architect ruled out an
   override: it would be a deliberate capability to register as another machine.

So the rig is a whole isolated world - its own root, Gateway, launcher and Director - and the real
machine is unreachable from it by construction. The Manager confirmed this shape on 2026-09-07 and
pointed Phase 5 at it.

## The script

`scripts\restart-qa-rig.ps1` on branch `restart-phase-7` (worktree `D:\ReposFred\devthrottle-restart-p7`).

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 build    # publish all three from this tree
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 up       # stand it up
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 status
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 down     # never force-kills
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 reset    # down, then delete the root
```

`build` must be re-run (with `-Force`) after every rebase, so the rig carries the phases it proves.
Every binary is stamped with the tree's commit; `status` and the Gateway's `/healthz` both show it.

## Where everything is (as stood up 2026-09-07 14:19Z)

| Thing | Where |
|---|---|
| Rig root | `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig` |
| Gateway | `http://127.0.0.1:7911` - `devthrottle-gateway.exe`, auth ON, tailscale OFF, scheduled task `restart-qa-rig-gateway` |
| Shared token (the rig's admission credential) | `<root>\config\director\gateway-token.txt` |
| Launcher | `<root>\launcher\cc-launcher.exe --no-autostart`, scheduled task `restart-qa-rig-launcher`, registration `<root>\config\launcher\launcher.json`, root key `f9f5c724a0b1` |
| Director | `<root>\app\cc-director.exe`, instance home `<root>\instances\default`, registration under `<root>\instances\default\config\director\instances\` |
| Director id | `9f46482f-8eb6-4d2f-b2ae-f7b54def0b39` - and it SURVIVED a full down/up of the rig (14:33Z, new pid 74264), because `DirectorIdStore` persists the id per executable path and instance. The director-restart skill says a restarted Director gets a new identifier; on this rig it did not. Always re-read the registration rather than assuming either way. |
| Logs | `<root>\logs\director\` (Gateway and launcher) and `<root>\instances\default\logs\director\` (Director) |
| Builds | `%TEMP%\restart-qa-rig-builds\{gateway,launcher,director}` |
| Rig worktrees for seeded seats | `D:\ReposFred\devthrottle-rig-alpha` (branch `restart-rig-alpha`), `D:\ReposFred\devthrottle-rig-beta` (branch `restart-rig-beta`) - both cut from origin/main, throwaway |

Machine name inside the rig is the real one, `SOREN_NORTH` - that is fine because nothing else
registers on the rig Gateway.

## Driving it from a session on DevThrottle_1

```
set CC_GATEWAY_URL=http://127.0.0.1:7911
set CC_GATEWAY_SESSION_KEY=<contents of the token file>
set CC_SESSION_ID=
cc-devthrottle session list
cc-devthrottle session spawn "D:\ReposFred\devthrottle-rig-alpha" --director <rig director id> --agent ClaudeCode --standalone --name "..." --prompt "..."
```

The shared token is a self-host credential and passes every route, including the admission surface.
A session key minted by the rig Gateway (any session spawned on the rig Director carries one in its
environment) is what the negative cases use - the `403 session_key_out_of_scope` on the direct restart
route is only meaningful with a session key, never with the shared token.

## What the rig proved on the way up

- The launcher started the Director through `POST /machines/SOREN_NORTH/director/start` relayed down
  its command stream (`{"ok":true,"via":"stream"}`). The first attempt, sent before the stream had
  bound, was refused 502 - the script retries the start rather than guessing a delay.
- The production Gateway's launcher list still shows the REAL launcher (pid 69640) for SOREN_NORTH and
  the production Director list is unchanged - the isolation holds.
- A RawCli session and a Claude Code session both run on the rig Director (see the report for the
  buffers).

## Run two is a change of target

The run tooling under `scripts\restart-qa\` takes a Gateway address, a credential, a machine name and
a Director id. Run two (production, DevThrottle_1, driven from SORENLAPTOP, the owner's own accept on
his phone) uses the same scripts pointed at `https://gateway.devthrottle.com`. Nothing in them knows
the rig.

## Known limits of the rig itself

- The Cockpit and mobile app it serves are BUILT FROM THIS TREE by default (`build` with
  `-WebShells tree`, the default since 14:31Z; `wwwroot\c\build.json` and `wwwroot\mobile\build.json`
  carry the tree commit, `4f742a4d3` at the time of writing). Phase 6's accept screen lives in the
  Cockpit, so a rig serving the installed copy would exercise the accept on a screen without the
  feature; `-WebShells installed` exists only for a rig proving something below the web shells, and
  `up` says loudly which one it used. The Gateway does not serve `/c/build.json` over HTTP (the
  mobile one it does); read the file on disk under the rig root.
- The owner's accept on the rig is performed with the shared token by the driver, standing in for the
  owner. That is a proof of the mechanism, not of his experience of it - run two covers the experience.

## The seeded fleet (2026-09-07 14:36Z onward)

Missions on the rig Gateway: Rig Alpha `bc7829b2-facb-4f2b-b2bc-de71a68f57ec` (a status page for the
restart rig, worktree `devthrottle-rig-alpha`), Rig Beta `190b8487-be35-4db8-b7b3-80afe3f45117` (notes
on the launcher, worktree `devthrottle-rig-beta`). Seed files in `rig-seeds\` beside this note; seat
ids in `rig-seat-ids.txt`.

| Seat | Id | Agent | Reports to | Real work (all uncommitted, by design) | Unique fact only its handover can carry |
|---|---|---|---|---|---|
| Rig Alpha - Architect | 2dc5c432 | ClaudeCode | nobody (mission head) | docs/rig-alpha/DESIGN.md | rulings D-4117, D-4118, D-4119 |
| Rig Alpha - Manager | b60818cc | ClaudeCode | Architect | docs/rig-alpha/MANAGER-LOG.md | checkpoint M-2261 |
| Rig Alpha - Worker A - status file | 9aa73695 | ClaudeCode | Manager | docs/rig-alpha/STATUS.md | note W-7703 |
| Rig Alpha - Worker B - launcher test run | 25ccf5f9 | ClaudeCode | Manager | a foreground `dotnet test` run, docs/rig-alpha/TEST-RESULT.md | note W-7704 and the counts |
| Rig Beta - Manager | 9e2cd820 | ClaudeCode | nobody (mission head) | docs/rig-beta/BETA-LOG.md | checkpoint B-5150 |
| Rig Beta - Worker C - launcher notes | d0bce298 | Codex | Beta Manager | docs/rig-beta/LAUNCHER-NOTES.md | note W-8812 |
| Rig - standalone - weekly count (snoozed) | 52902570 | ClaudeCode | nobody | docs/rig-beta/STANDALONE-COUNT.md, then snoozes itself 720 minutes | count S-3391 |
| Rig - wedged - RawCli on a command that never returns | e1d7291b | RawCli (`cmd /c ping -t 127.0.0.1`) | nobody | none - it cannot do anything | none - it cannot write one |

**The wedge is proven wedged, through the drain's own delivery path.** `POST /sessions/{id}/prompt`
(the route `SessionCommandExecutor.SendPromptAsync` sits behind, which Phase 4's drain uses) answered
`accepted: false` with `EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts
- the TUI is not accepting input`; the buffer kept scrolling ping replies; no document appeared; the
`ping -t` process (pid 72700) was still alive; the session row went to `promptDeliveryUnresolved:
true`. Artefacts in the report's `attachments/wedge-proof/`. So a seat that cannot answer is recorded
by the Director at the moment of the attempt, not only ninety minutes later.

**Seeding hiccup, recorded because it is a finding in its own right:** four of the seven agent seats
(Alpha Manager, Worker A, Worker B, the standalone) had their seed prompt typed into a Claude Code
composer that was still initialising (`/rc connecting...`), the echo check missed twice, and the
Director stamped `promptDeliveryUnresolved: true` with the reason. The text sat parked in the
composer, doubled and unsubmitted - exactly the failure the restore path is told to check for. The
flag fired on 4 of 8 spawns, so it is an instrument that has been seen to say no. The prompts were
sent again through the same route once the composers had settled; see the report for the outcome.
