# Live check - the trigger on a real Director, on an isolated stack

The mission's Trigger check, run live on 2026-09-21 against a Gateway and a real Director both built from
devthrottle main at `761a45383`, isolated from the owner's own Gateway and Directors.

## The verdict

| Case | Result |
|---|---|
| Empty mailbox: at least three consecutive empty runs, no session started | **PASS** - nine consecutive "nothing to do" runs, no session |
| Broken check: RED in the history and in the Cockpit | **PASS** - every run "failed" with the reason, RED "check failed" in the Cockpit |
| Pause: records "paused", starts nothing; resume works | **PASS** - four "paused" runs, no session; resume turned the next check into a start attempt |
| One start: exactly ONE visible session, then "skipped" on the next interval while it runs | **FAIL - defect found** (below) |

## The defect: every start on a real Director is recorded as failed, and the lock is never set

**What happened.** With the stub check counting 1, the trigger was resumed at 15:08:01 UTC. Over the next
three intervals the Gateway recorded three runs as `failed`, reason "the session could not be started: The
connection to the Director dropped while the command was being sent. It is not known whether the command was
carried out", with no session id. But the Director DID start a session each time - three sessions in three
intervals, numbered 100 each time, all named `front-desk - Website mail - stub count 1 - <time>`:

| Check | Spawn sent | Row written | Session actually created |
|---|---|---|---|
| 15:08:06 | 15:08:06.327 | 15:08:16.342 failed, session none | `9a734428`, created 15:08:15, exited 15:08:51 |
| 15:09:36 | 15:09:36.598 | 15:09:46.606 failed, session none | `1b4585fd`, created 15:09:49, exited 15:10:24 |
| 15:11:09 | 15:11:09.335 | 15:11:19.388 failed, session none | `df6c14a1`, created 15:11:25, exited 15:12:00 |

Every row lands exactly 10 seconds after the spawn was sent. Log excerpt: `live-rows/03-start-attempts-log-excerpt.txt`.

**Cause, read from both logs and the code.**

1. The Director reports each check with `POST /directors/{id}/triggers/{trigger}/checks` through its
   `GatewayClient`, whose `HttpClient.Timeout` is 10 seconds (`src/CcDirector.ControlApi/GatewayClient.cs:109`).
2. The Gateway decides inside that same request, and for a count above zero it starts the session inside it
   too, passing `ctx.RequestAborted` (`src/CcDirector.Gateway/Api/TriggerEndpoints.cs`, the checks route) down
   to `MachineSessionSpawner.SpawnOnMachineAsync`.
3. A real session start takes longer than 10 seconds: 9.8, 13.4 and 16.0 seconds here (the first spends about
   six of them installing skills before Claude Code launches).
4. At 10 seconds the Director's client gives up (Director log: `ReportTriggerCheckAsync FAILED ... HttpClient.Timeout
   of 10 seconds elapsing`, then `report NOT recorded`). That aborts the Gateway request, the abort cancels the
   create command in flight (`TUNNEL DROPPED mid-command: Invocation canceled by the server`), and the Gateway
   writes `failed` with no session - while the Director finishes the create anyway.

**What it costs.** The one-at-a-time lock is keyed on the session the trigger recorded, and none is recorded,
so the lock never holds: every interval with work starts another session. The unit tests could not see this
because their fake session starter answers at once, and the end-to-end host test had no Director attached.

**What it is not.** The sessions exiting after about 35 seconds is this rig, not the product: the repository
folder was a fresh scratch folder Claude Code had never trusted, so its first screen was the folder-trust
question, and the Director's retype after a missed prompt echo answered it with an exit (Director log:
`EchoVerifiedSubmit: composer echo not seen ... clearing the composer and retyping`, then `ProcessExited ...
exitCode=0`).

**Not fixed here.** The mandate says to stop and tell the Tech Lead before fixing a defect the live check
finds. The Tech Lead was told at 15:14 UTC (message `d8b59d4b`, with two ways to fix it: start the session off
the request's cancellation and answer the report without waiting on the start, or give the report a longer
timeout) and did not answer by the one-hour deadline, so this pull request carries the finding and no fix.

**Also seen.** After the stub trigger was paused again, its status went back to plain PAUSED and the start
failures now show only in the history (`screens/02-start-failed-2.png`), because the status follows the last
outcome.

## The rows and screenshots

Rows are the record's own (`cc-devthrottle factory activity --json` and `cc-devthrottle trigger runs <id> --json`
against the isolated Gateway), saved in `live-rows/`:

- `01-before-start-*`: before the stub trigger was resumed.
- `02-after-start-attempts-*`: after the three failed starts and the pause.
- `03-start-attempts-log-excerpt.txt`: every start attempt in the Gateway's and the Director's logs.

The three triggers, all for factory `website-business`, factory agent `front-desk`, machine `SOREN_NORTH`, every
minute:

| Trigger | Id | Check command |
|---|---|---|
| Website mail - empty record | `a3c1fcdf` | `cc-website-factory --db <scratch>\factory-empty.db --actor trigger mail-waiting --json` |
| Website mail - broken check | `b9a1456f` | a `cc-website-factory.exe` path that does not exist |
| Website mail - stub count 1 | `b132120e` | `stub-count-1.cmd`, which prints `{"count": 1}`; created paused |

**Empty mailbox** (`02-after-start-attempts-runs-a3c1fcdf.json`): nine consecutive runs, 15:02:07 to 15:14:07,
each `nothing-to-do`, count 0, no session. The record shows each as "Checked Website mail - empty record -
nothing to do", actor `trigger:a3c1fcdf...`.

Why a scratch record and not the live-test record: at 14:55 the live-test record
(`%LOCALAPPDATA%\website-factory\factory-livetest.db`) counted **1** waiting reply (thread `1a0c4411677f8956`),
so it cannot give an empty mailbox, and pointing a running trigger at it would have started a real Front Desk
session. The empty runs use a record made with `cc-website-factory init` in the rig's scratch folder: it has no
threads, so `mail-waiting` answers 0 without querying the mailbox. The live-test record was only read.

**Broken check** (`02-after-start-attempts-runs-b9a1456f.json`): nine consecutive runs, each `failed`, reason
"exit code 1: The system cannot find the path specified." The trigger's status is RED "check failed: exit code 1:
The system cannot find the path specified."

**Pause** (`02-after-start-attempts-runs-b132120e.json`): created paused; four runs 15:02:06 to 15:06:36, each
`paused` with count 1 and no session ("counted 1, but the trigger is paused, so nothing started"). Resumed at
15:08:01; the next check went to a start (which then hit the defect). Paused again at about 15:12; the runs at
15:12:37 and 15:14:06 are `paused` again.

| Screenshot | What it shows |
|---|---|
| `screens/01-before-start-1.png` | Factories: Website Business FAULT, "Trigger "Website mail - broken check": check failed", 4 failed, 4 empty checks, 4 checks while paused. |
| `screens/01-before-start-2.png` | All factory agents: Front Desk with its three triggers. |
| `screens/01-before-start-3.png` | Front Desk: broken check FAULT with the reason, empty record OK "nothing to do", stub PAUSED "work waiting, paused". |
| `screens/01-before-start-4.png` | Activity: the trigger rows, empty checks collapsed. |
| `screens/02-start-failed-1.png` | Factories after the start attempts. |
| `screens/02-start-failed-2.png` | Front Desk: 12 failed, including the three "the session could not be started" rows. |
| `screens/02-start-failed-3.png` | Activity after the start attempts. |

## How the stack was built, so the next live check can repeat it

Everything lives under `%TEMP%\wbf-live` except the worktree. Nothing of the owner's is touched: the
Director's whole data tree follows `CC_DIRECTOR_ROOT`, the Gateway's too, and neither uses Tailscale.

1. **Worktree**: `git worktree add ../devthrottle-wbf-live -b wbf-live-check origin/main`.
2. **Gateway, with its Cockpit**:
   `dotnet publish src/CcDirector.GatewayApp/CcDirector.GatewayApp.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RunMobileBuild=false -p:RunCockpitBuild=true -o %TEMP%\wbf-live\gw-stage`.
   Its root `%TEMP%\wbf-live\gw-root\config\config.json` is `{ "factoryAgents": { "enabled": true } }`.
   `live-rig/launch-gateway.cmd` sets `CC_DIRECTOR_ROOT` to that root, `CC_GATEWAY_NO_TAILSCALE=1` and
   `CC_GATEWAY_NO_AUTH=1`, and runs `devthrottle-gateway.exe --port 7898 --no-autostart`, launched through the
   scheduled task `wbf-live-gateway`. `/healthz` reported version `2.9.0+761a4538...`, the worktree head.
3. **Director**: `scripts\local-build-avalonia.ps1 -Slot 21 -OutputDir "<worktree>\scripts\local-build"`.
   How a Director is pointed at a Gateway, from the code: the Director reads `CC_DIRECTOR_ROOT` as its machine
   root, then isolates itself under `<root>\instances\default` (`Program.ResolveInstance`), and reads the Gateway
   address from that instance's `config\config.json`, block `gateway` (`GatewayConfig.Load`). So the rig writes
   `%TEMP%\wbf-live\dir-root\instances\default\config\config.json` as
   `{ "onboarding": { "completed": true }, "gateway": { "url": "http://127.0.0.1:7898", "token": "wbf-live-no-auth", "streamMode": true } }`
   (the token is any string, because the Gateway runs with no authentication; `onboarding.completed` skips the
   first-run wizard). `live-rig/launch-director.cmd` sets `CC_DIRECTOR_ROOT=%TEMP%\wbf-live\dir-root` and runs
   `cc-director21.exe`, launched through the scheduled task `wbf-live-director21`, never from an agent's own
   process tree. Its single-instance guard is keyed on the slot, so it cannot collide with slots 1-4 or the
   installed app. Director id `5453e038-f4c3-491d-a3a2-34f02fc0e34e`, registration in
   `dir-root\instances\default\config\director\instances\`.
   What a slot Director still does machine-wide, as every slot build does: it places the fleet skills into
   `~/.claude/skills` (from the isolated Gateway's store, which is built from the same main) and runs the
   start-up tool path repair and tool copy sweep.
4. **The tools**: a scratch virtual environment with `cc-website-factory` from cc-consult `origin/main`
   (`git archive`, then `pip install`) and `cc-devthrottle` from the worktree (`tools/cc_storage`,
   `tools/cc_shared`, `tools/cc-devthrottle`). `cc-devthrottle` is always run with
   `CC_GATEWAY_URL=http://127.0.0.1:7898`, never the fleet's own address.
5. **Screenshots**: `live-rig/shoot.py <out> <prefix> <route>...` - Playwright as a library against the
   no-authentication rig, a deliberate scripted use rather than a signed-in browser.
6. **Stopping the Director**: the named signal `Local\cc-director-shutdown-5453e038-f4c3-491d-a3a2-34f02fc0e34e`,
   never a kill; then unregister both scheduled tasks and stop the Gateway.

## Not covered

- The "one start then skipped" case, because of the defect above.
- The real `mail-waiting` against the live-test record with work waiting: it counted 1 when run by hand at 14:55
  but was not wired to a running trigger, so no Front Desk session was started from it.
