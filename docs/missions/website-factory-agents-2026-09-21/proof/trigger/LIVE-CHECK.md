# Live check - the trigger on a real Director, on an isolated stack

The mission's Trigger check, run live on 2026-09-21 against a Gateway and a real Director both built from
devthrottle main at `761a45383`, isolated from the owner's own Gateway and Directors.

## The verdict

| Case | Result |
|---|---|
| Empty mailbox: at least three consecutive empty runs, no session started | **PASS** - nine consecutive "nothing to do" runs, no session |
| Broken check: RED in the history and in the Cockpit | **PASS** - every run "failed" with the reason, RED "check failed" in the Cockpit |
| Pause: records "paused", starts nothing; resume works | **PASS** - four "paused" runs, no session; resume turned the next check into a start attempt |
| One start: exactly ONE visible session, then "skipped" on the next interval while it runs | **FAIL on main `761a45383`** (below). **PASS after the fixes** `19ed0ba84` (report answered at once, lock before the spawn) and `d5f81154f` (a start whose outcome is unknown keeps the lock and adopts its session): one session, then `skipped-running` - including a start that outlived the Gateway's 30-second wait (see "The fix, live") |

## The fix, live (second seat, 2026-09-21 evening)

### 1. What changed (commit `19ed0ba84`, the Tech Lead's decision)

- The check route decides and answers the Director at once. When the check counted work and nothing stands in
  the way, it answers **202** (`TriggerStartAccepted`: trigger, count, `starting: true`) and never waits on the
  session start.
- The one-at-a-time lock is taken **before** the start begins: `TriggerStore.BeginStart` sets the trigger's
  start time with no session yet. `TriggerService.IsLastSessionAlive` reads a start time with no session as a
  start in flight, alive inside the 5-minute start grace, so a check that arrives meanwhile records
  `skipped-running` ("its session is still being started"). If the Gateway stops mid-start, that lock lapses
  after the grace instead of holding for good.
- The start runs on the Gateway's own lifetime (`GatewayHost._triggerStartLifetime`, cancelled only in
  `StopAsync`), never on the request's. When it returns it writes the check's ONE run row and ONE activity row:
  `started` with the session id (the lock is now that session), or `failed`, which releases the pending lock
  (`TriggerStore.RecordStartResult`) and turns the trigger RED "start failed: ...". A start that throws, or is
  cut off by the Gateway stopping, ends the same way with the reason said.
- The Director is unchanged: it treats any 2xx as recorded.

Tests (`TriggerServiceTests`, `FactoryTriggerHostTests`): a slow start that outlives a cancelled report request
ends as one `started` row with the lock held and the next check `skipped-running`, and the start ran on the
lifetime token, not the request's; a check during a pending start is `skipped-running` and starts nothing; a
failed slow start releases the lock, is RED "start failed", and the next check starts again; a start that throws
and one cut off by the Gateway stopping are failed rows that release the lock; a pending lock left by a stopped
Gateway lapses after the grace; a broken check during a pending start does not release it; over the real host,
a report that begins a start is answered 202 and the start writes its row afterwards. The fake session starter
throws on a cancelled token, exactly as the real create command does, so the old code could not pass the first
of these. That is by construction, not a revert run: the new tests call the new members, so they do not
compile against the old code.

Gate (`gate-results.md` has the older runs): `.\scripts\test-local.ps1` - every suite green except the two
Launcher restart-signal tests known red (issue 3242: `Describing_the_launcher_asks_the_signal_and_never_raises_it`,
`An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown`). `dotnet test
src\CcDirector.Gateway.UnitTests`: 7157 passed, 0 failed, 8 skipped. `CcDirector.Gateway.Tests` filtered to
`Trigger|Factory`: 14 passed, 0 failed. No migration was touched, so the PostgreSQL proofs were not run.

### 2. Live again on the rebuilt stack

The rig Gateway was republished from the worktree at `19ed0ba84` to `%TEMP%\wbf-live\gw-stage2` and restarted
through its own scheduled task (`/healthz` version `2.9.0+19ed0ba84...`). Its old process outlived its stopped
task, so that one process (the rig's own `gw-stage\devthrottle-gateway.exe`, checked by path) was stopped by its
id. The Director was not rebuilt (unchanged) and reconnected by itself. Two rig changes, both to make the
one-start case observable: the scratch repository folder was marked trusted in Claude Code's own settings (the
first seat's sessions exited on the folder-trust question), and the stub trigger's prompt asks the session to
run a 100-second `ping`, so it is still running at the next interval.

Stub trigger `b132120e` resumed at 19:34:55 UTC. Rows: `live-rows/05-after-fixed-start-runs-b132120e.json` and
`05-after-fixed-start-activity.json` (before: `04-before-fixed-start-*`); the ten-second watch of the runs and
the session list: `live-rows/05-fixed-start-watch.txt`; both logs: `live-rows/06-fixed-start-log-excerpt.txt`.

| Check (UTC) | Director's report answered | Start | Row | Session actually created |
|---|---|---|---|---|
| 19:35:33.66 | 19:35:33.85, no timeout | lock taken 19:35:33.74; the Gateway's spawn wait gave up at 19:36:04.04 (30 s): "The Director did not answer within 30 seconds. It is not known whether the command was carried out." | `failed`, count 1, no session; lock released | `7748ec0f` - create received 19:35:35.1, then 27 s re-installing skills, Claude Code launched 19:36:02.3 |
| 19:36:38.34 | 19:36:38.72, no timeout | lock taken 19:36:38.72; returned in 10.2 s | `started`, count 1, session `de8b16e7`, recorded 19:36:48.88 | `de8b16e7` |
| 19:38:00.39 | 19:38:00.44 | none | **`skipped-running`, session `de8b16e7`** | none |

**What this proves.** The defect the first seat found is gone: the report no longer times out (every report
answered in under a quarter of a second, against 10 seconds and a timeout before), a start that returns is
recorded `started` with its session, and the lock then holds - the next interval was `skipped-running` on that
session and started nothing.

**What it also found - a second way to start twice.** The spawn itself has a 30-second wait on the Director's
answer (`DirectorCommandRouter`). The first start after the Gateway restart took 29 seconds on the Director, 27 of
them re-installing skills, so the Gateway's wait ran out; the spawner answered "it is not known whether the
command was carried out"; by the rule "a failed spawn releases the lock", the row went `failed` and the lock was
released; and the Director did create the session. The next interval then started a second one. The spawner
gives no structured "outcome unknown" signal - it is only in the sentence - so the fix cannot tell a definite
failure from an unknown one. Put to the Tech Lead at about 19:40 UTC with a recommendation: an outcome-unknown
start keeps the pending lock, adopts the session when the Director reports one with the start's unique name, and
lets the lock lapse after the start grace only if none appears; a definite failure still releases at once. The
Tech Lead said yes at 19:41 - built and proven in section 3 below.

Screenshots: `screens/03-fixed-start-1.png` (Front Desk: the stub trigger, the failed start row with the
"not known" reason), `screens/03-fixed-start-2.png` (Activity for Website Business after the two starts).
Both stub sessions were then stopped (`cc-devthrottle session stop ... --reason`), after the stub trigger was
paused again.

Session `7748ec0f` stayed "waiting for input" with no turn - its opening prompt did not arrive - while
`de8b16e7` ran its turn. Not chased here; it is the lost create answer's other side.

### 3. A start whose outcome is unknown keeps the lock (commit `d5f81154f`, the Tech Lead's ruling)

**What changed.**

- The create's outcome travels as a flag, never read from the sentence. `SessionVerbClient.CreateSessionWithOutcomeAsync`
  sets it from the command router's own status (`Timeout` or `TunnelDropped`), and
  `MachineSessionSpawner.SpawnOnMachineWithOutcomeAsync` carries it (`MachineSpawnResult.OutcomeUnknown`) to the
  trigger's starter (`TriggerStartAttempt`). A failure before the create was sent - machine off, a refused
  Director - and a Director that answered with an error are definite.
- An unknown outcome writes a `failed` row whose reason says so and says the lock is held:
  "the session start outcome is not known: <the router's sentence> The lock is held until a session named '<name>'
  shows up, or for 5 minutes." The pending lock stays. The trigger reads RED "start outcome unknown - waiting for
  the session" for as long as that lock holds, whatever the checks since have come to.
- At each later check, while no start of the trigger runs on this Gateway, the session is looked up by the start's
  unique name (computed from the lock's own start time, so nothing new is stored), among the sessions the
  Directors report with origin surface `trigger`. Found: a NEW `started` row with its id, the lock is that session,
  and the status clears. Not found once the 5-minute grace is over: the lock lapses with a `failed` row saying "no
  session named '<name>' showed up within 5 minutes of a start whose outcome was not known; the lock is released".
- A definite failure still releases the lock at once. A start cut off by the Gateway stopping is now an unknown
  outcome too (the create may already be on its way), so the next Gateway adopts or lapses it rather than
  starting again.

Tests: `TriggerServiceTests` - an unknown outcome keeps the lock, is RED, writes the failed row with that reason,
and the next check inside the grace is skipped and starts nothing; an unknown outcome whose session shows up is
adopted as a new `started` row, the lock is on it, the status is OK, and once it ends the next check starts; with
no session inside the grace the lock lapses with its row and the next check starts; a start still running is not
taken for an unknown one; a start cut off by the Gateway stopping keeps the lock. `MachineSessionSpawnerTests`,
through the real verb client: a tunnel dropped mid-create is an unknown outcome; a Director's error answer and a
machine that is off are not. `TriggerStatusFoldTests`: the unknown lock is RED whatever the last check was, and
silence still outranks it. Gate: `dotnet test src\CcDirector.Gateway.UnitTests` 7166 passed, 0 failed, 8 skipped;
`CcDirector.Gateway.Tests` filtered to Trigger, Factory and the three host tests that build the spawner
(`DirectorSpawnMissionAndSeat`, `MachineSpawnOriginStamp`, `HostedTenantMachineControl`): 67 passed, 0 failed;
`.\scripts\test-local.ps1`: every suite green except the same two Launcher tests (issue 3242).

**Live.** The rig Gateway was republished at `d5f81154f` to `gw-stage3` and restarted the same way (`/healthz`
`2.9.0+d5f81154f...`). The first start after the restart was fast this time (3.6 seconds): one `started` row for
`c060b632` and `skipped-running` on it at the next two checks (`live-rows/08-second-build-fast-start-watch.txt`).
The slow case did not recur by itself, so it was made to happen: `live-rig/slow-director.ps1` watches the rig
Director's log for the next create and suspends that ONE process - the rig's own slot-21 Director, which the
script checks by path - for 40 seconds, then resumes it. Suspended, never killed. The Gateway's 30-second wait
runs out while the Director, resumed, still carries the create out. That is the live defect, on purpose.

| Time (UTC) | What happened | Row |
|---|---|---|
| 20:10:00.16 | check counted 1; lock taken; create sent; the Director received it and was suspended at 20:10:00.29 | none yet |
| 20:10:30.17 | the router: "TIMED OUT after 30 seconds"; the spawner: `outcomeUnknown=True` | `failed`, count 1: "the session start outcome is not known: The Director did not answer within 30 seconds. ... The lock is held until a session named 'front-desk - Website mail - stub count 1 - 2026-09-21 16:10' shows up, or for 5 minutes." Status **RED "start outcome unknown - waiting for the session"** |
| 20:10:40.33 | the Director resumed, reconnected, and finished the create: session `c8aeafb3`, its agent launched 20:10:44 | none |
| 20:11:30.06 | next check: the session was found by its name and adopted | a NEW **`started`** row, session `c8aeafb3`; then this check's own **`skipped-running`** on `c8aeafb3`. Status **OK** |
| 20:13:00.14 | next check | `skipped-running` on `c8aeafb3` |

Exactly one session for that start. Rows: `live-rows/09-after-unknown-adopted-runs-b132120e.json` and
`09-after-unknown-adopted-activity.json` (before: `07-before-unknown-fix-*`); the ten-second watch with the status
at each tick: `live-rows/09-unknown-adopted-watch.txt`; both logs: `live-rows/10-unknown-adopted-log-excerpt.txt`.
Screenshots: `screens/04-unknown-adopted-1.png` (Front Desk, the stub trigger OK "work waiting, its last session
still running") and `screens/04-unknown-adopted-2.png` (Activity). The RED status while the lock waited shows in
the watch (20:10:44 to 20:11:24), not in a screenshot. The stub trigger was then paused and `c8aeafb3` stopped.

Not proven live: the lapse after 5 minutes with no session (it needs a create that is lost outright; the unit
test covers it), and adoption after a Gateway restart mid-start.

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

**Fixed in this pull request by the second seat** (the Tech Lead's decision: answer at once, lock before the
spawn, spawn on the Gateway's lifetime) - see "The fix, live" above. The first seat stopped at the finding, as
its mandate said.

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

- The end-to-end reply (the real check starting a real Front Desk session that leaves a Gmail draft): no
  allowlisted test address has a business thread to insert a reply on (wftest1 is suppressed; wftest2 and wftest3
  have no thread), so it waits on the Tech Lead's word on how to make one.
- The real `mail-waiting` against the live-test record with work waiting: it counted 1 when run by hand at 14:55
  but was not wired to a running trigger, so no Front Desk session was started from it.
