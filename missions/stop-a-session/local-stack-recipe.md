# Standing the whole stack up locally, from the mission branch

**What this is for.** The mission's goal is a report showing the feature working on a real fleet. That
needs a Gateway carrying `mission/stop-a-session`, and the hosted Gateway deploys only from `main`,
which this branch cannot reach until the report exists. So the stack is stood up **locally**: a
Gateway built from the branch, and a test Director pointed at it.

**This recipe was executed end to end on 9 September 2026 and a real session was stopped by it.**
Every command below is one that actually ran, not one that ought to work. Where something went wrong,
it is written down here rather than smoothed over - see "What went wrong" at the end.

**It was run against commit `ed32e709` of `mission/stop-a-session`, on machine SORENLAPTOP.** The
Gateway reports the commit it was built from on `GET /healthz`, so whoever repeats this can say in
their own report exactly which code answered.

---

## What it does NOT touch, and how you can tell

- **Not the hosted Gateway.** Everything here is `http://127.0.0.1:7997`. The hosted Gateway
  (`https://gateway.devthrottle.com`) is never called and never deployed to.
- **Not the installed Gateway.** The owner runs `devthrottle-gateway.exe` from
  `%LOCALAPPDATA%\cc-director\gateway` on the default port **7878**. This one is a separate
  executable, on port **7997**, with its own storage root. Nothing is swapped, replaced or restarted.
  In particular, **do not use `scripts\redeploy-gateway.ps1`** - that publishes over the installed
  tray Gateway, which is the owner's.
- **Not the owner's Directors.** The owner's daily Director runs from
  `%LOCALAPPDATA%\cc-director\app\cc-director.exe` on the `default` instance. This one is
  `cc-director6.exe`, slot 6, on its own named instance `stop-a-session-qa`. Slots 1 to 5 and the
  installed application are never touched, and nothing is ever force-killed.
- **Not the owner's session data.** The test Director's whole data tree is
  `%LOCALAPPDATA%\cc-director\instances\stop-a-session-qa`, which nothing else reads.

**The one shared file this writes to** is
`%LOCALAPPDATA%\cc-director\config\director\named-instances.json`, which lists the named instances
every Director and the launcher can enumerate. One entry is added; step 9 takes it out again. Back it
up first (step 4 does) so the restore is exact rather than remembered.

---

## What you need before you start

- A worktree on `mission/stop-a-session`, or a detached worktree cut from its tip. **Use a separate
  worktree from the one anyone is editing in** - two builds in one worktree is how the Phase A
  Manager ended up with test failures that vanished when the machine went quiet.
- The .NET 10 software development kit (`dotnet --version`), already on this machine.
- No elevation. Nothing here needs Administrator.

Everything below assumes these two paths. Substitute your own throughout:

    the build worktree   C:\ReposFred\devthrottle-stack
    the scratch area     C:\ReposFred\devthrottle-stack\_stack

---

## 1. Cut an isolated worktree from the branch

    cd C:\ReposFred\devthrottle-stop-a-session
    git worktree add --detach ..\devthrottle-stack HEAD

Detached, so it does not fight the checked-out mission branch. To repeat this against a LATER tip,
cut it again from the new commit - do not build from a tree that is behind.

## 2. Publish the Gateway from that worktree

The `CcDirector.Gateway` project is the local development console host. It runs byte-identical
startup logic to the hosted container host (`GatewayEntryPoint`), and it takes `--port`.

    cd C:\ReposFred\devthrottle-stack
    dotnet publish src\CcDirector.Gateway\CcDirector.Gateway.csproj -c Debug -o C:\ReposFred\devthrottle-stack\_stack\gateway

Roughly four minutes on a warm machine.

## 3. Run the Gateway on its own port and its own storage root

Write `C:\ReposFred\devthrottle-stack\_stack\run-gateway.cmd`:

    @echo off
    set CC_DIRECTOR_ROOT=C:\ReposFred\devthrottle-stack\_stack\gateway-root
    set CC_GATEWAY_NO_TAILSCALE=1
    "C:\ReposFred\devthrottle-stack\_stack\gateway\CcDirector.Gateway.exe" --port 7997

`CC_DIRECTOR_ROOT` is what keeps this Gateway's whole data tree - its database, its token, its
registrations - inside the scratch area. `CcStorage` honours it, and every Gateway store resolves
through `CcStorage`. Start it and leave it running in its own window or background task:

    cmd /c C:\ReposFred\devthrottle-stack\_stack\run-gateway.cmd

Wait for it to answer, and **read the commit back**:

    curl -s http://127.0.0.1:7997/healthz

    {"status":"ok","directors":0,"sessions":0,
     "version":"2.0.7+ed32e709e9cd4ae62e1f96606c82ed465e67d69e", ...}

That `+<commit>` suffix is the proof that this Gateway is the branch and not something else already
listening. **Check it. If the version does not carry the commit you built, you are talking to
another Gateway** and everything after this point would be measuring the wrong thing.

**Authentication is left ON, exactly as in production.** There is an environment switch that turns it
off (`CC_GATEWAY_NO_AUTH=1`) and this recipe deliberately does not use it: a proof run with the
authentication gate disabled would not exercise the gate the real thing runs behind. The Gateway
generates its own shared machine token on first start:

    C:\ReposFred\devthrottle-stack\_stack\gateway-root\config\director\gateway-token.txt

Confirm the gate is really up - an unauthenticated call must be refused:

    curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:7997/directors     ->  401

## 4. Register a named instance for the test Director

A Director's data home and its Gateway both come from its **named instance**. Back the registry up
first, then add one entry and scaffold that instance's configuration:

    copy "%LOCALAPPDATA%\cc-director\config\director\named-instances.json" ^
         C:\ReposFred\devthrottle-stack\_stack\named-instances.backup.json

Add to the `instances` array in
`%LOCALAPPDATA%\cc-director\config\director\named-instances.json`:

    {
      "id": "<any new identifier>",
      "name": "stop-a-session-qa",
      "displayName": "Stop a session QA",
      "gatewayUrl": "http://127.0.0.1:7997",
      "createdAt": "<now, in round-trip format>"
    }

And write `%LOCALAPPDATA%\cc-director\instances\stop-a-session-qa\config\config.json`, pasting in the
token from step 3:

    {
      "gateway": {
        "url": "http://127.0.0.1:7997",
        "token": "<the contents of gateway-token.txt>"
      }
    }

**Do not skip the registry entry.** `Program.ResolveInstance` looks the slug up, and an **unknown
slug silently falls back to the default instance** - which is the owner's. A typo here does not fail
loudly; it lands your test Director in the owner's data tree and on the owner's Gateway. That is the
single most dangerous step in this recipe, and it is why the verification in step 7 exists.

## 5. Reserve a Director slot

Slots 1 to 5 and the installed application belong to the owner. The arbitration script reserves the
lowest free slot at 6 or above, and the reservation IS the scheduled-task registration, so two agents
cannot take the same one:

    powershell -NoProfile -File C:\ReposFred\devthrottle-stop-a-session\scripts\agent-session-isolation.ps1 allocate -Worktree C:\ReposFred\devthrottle-stack

    [allocate] SLOT=6
    [allocate] TASK=cc-director6-launch
    [allocate] MANIFEST=C:\ReposFred\devthrottle-stack\local_builds\agent-session-slot6.json

## 6. Build the Director into that slot

    powershell -NoProfile -File C:\ReposFred\devthrottle-stack\scripts\local-build-avalonia.ps1 -Slot 6 -OutputDir C:\ReposFred\devthrottle-stack\local_builds

Roughly two minutes. It produces `C:\ReposFred\devthrottle-stack\local_builds\cc-director6.exe`.

## 7. Launch it through Windows Task Scheduler, with the instance argument

**Never start it from inside an agent's own session.** A Director launched inside a coding agent's
pseudo-console gives every agent process it spawns a nested pseudo-console, and those exit within
about three seconds saying input must be provided through standard input. Task Scheduler runs it
under the scheduler service instead, outside that console. This is rule 0b in `CLAUDE.md`.

`agent-session-isolation.ps1 launch` cannot be used as-is here, because it registers the task with no
arguments and **the `--instance` argument is the whole point** - without it the Director runs as the
default instance, which is the owner's. So register the task yourself:

    $exe = "C:\ReposFred\devthrottle-stack\local_builds\cc-director6.exe"
    $wd  = "C:\ReposFred\devthrottle-stack\local_builds"
    $action  = New-ScheduledTaskAction -Execute $exe -Argument "--instance stop-a-session-qa" -WorkingDirectory $wd
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    Register-ScheduledTask -TaskName "cc-director6-launch" -Action $action -Trigger $trigger -Force
    Start-ScheduledTask -TaskName "cc-director6-launch"

`-WorkingDirectory` must be set or the interface framework's first-run resource resolution can fail
with exit code -1.

**Readiness is the instance registration the running process writes**, not a port - the Director
binds nothing at all. Wait for the file under the instance home whose `Pid` is your process, and read
its `DirectorId` out; that identifier is what stops it cleanly later:

    $regDir = "$env:LOCALAPPDATA\cc-director\instances\stop-a-session-qa\config\director\instances"
    # ... poll $regDir for the .json whose Pid matches, take its DirectorId

**Now verify it joined the RIGHT Gateway** - this is the check that catches a mistyped slug:

    $tok = (Get-Content C:\ReposFred\devthrottle-stack\_stack\gateway-root\config\director\gateway-token.txt -Raw).Trim()
    curl -s -H "Authorization: Bearer $tok" http://127.0.0.1:7997/directors

It must list exactly your Director identifier, and `GET /healthz` must now say `"directors":1`. If it
says zero, your Director went somewhere else - stop and fix step 4 before going on. **An empty
directors list is a broken instrument, never a clean run.**

## 8. Stop a real session

### 8a. Spawn one

Session spawning is an old verb, so the **installed** command line can do it. Point it at the local
Gateway and hand it the local Gateway's own token as the credential, and name the Director so the
session cannot land anywhere else:

    set CC_GATEWAY_URL=http://127.0.0.1:7997
    set CC_GATEWAY_SESSION_KEY=<the contents of gateway-token.txt>
    cc-devthrottle session spawn C:\ReposFred\devthrottle-stack\_stack\scratch-repo --director <your director id prefix> --standalone --name "Stop a session - throwaway"

Use a scratch repository, not a real one. Note the session identifier it prints.

**Find the agent's process id before you stop it**, so you can prove afterwards that it is gone. The
session row does not carry one; the operating system does:

    Get-CimInstance Win32_Process -Filter "ParentProcessId = <the Director's process id>" |
      Select-Object ProcessId, Name

### 8b. Run the stop from the BRANCH, not from the installed tools

**This is the step that will stop you if you do not know about it.** `session stop` is new in this
mission, and the command line installed on this machine is from July - it answers
`No such command 'stop'`. Do not install over the owner's tools to fix that. Stage the branch's
command line on the module path instead:

    # copy tools\cc-devthrottle\src  ->  <scratch>\pypkg\cc_devthrottle
    # copy tools\cc_shared           ->  <scratch>\pypkg\cc_shared

    set PYTHONPATH=<scratch>\pypkg
    set CC_GATEWAY_URL=http://127.0.0.1:7997
    set CC_GATEWAY_SESSION_KEY=<the contents of gateway-token.txt>
    "%LOCALAPPDATA%\cc-director\pyenv\Scripts\python.exe" -c "from cc_devthrottle.cli import app; app()" session stop <session> --reason "why"

The interpreter under `pyenv` already has the packages this needs. What actually came back:

    stopped 75f2e3f2 - process 16316 ended, row removed
    the worktree C:\ReposFred\devthrottle-stack\_stack\scratch-repo was left untouched - it had no uncommitted changes
    reason: the local stack smoke test for the Stop a session mission

### 8c. Confirm the process is actually gone

This is the check that separates a row being hidden from a session being ended:

    Get-Process -Id 16316 -ErrorAction SilentlyContinue     ->  nothing

## 9. Take it back down

In this order. **Nothing here is force-killed.**

1. **The test Director - signal it, do not kill it.** A clean shutdown makes it end its own sessions
   and delete its crash journal, so it leaves no phantom "interrupted" entry. The signal is named for
   that Director's own identifier, which is the only string that names one process on a machine
   running several:

       $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\cc-director-shutdown-<directorId>")
       $evt.Set(); $evt.Dispose()

   `OpenExisting` throwing means nothing is listening and that Director cannot be asked to stop -
   only then is a force-kill the answer, and only against a process whose image path you have
   confirmed is your own slot executable.
2. **The scheduled task:** `Unregister-ScheduledTask -TaskName "cc-director6-launch" -Confirm:$false`
3. **The Gateway:** stop its console process. It is yours and it is the only thing on 7997.
4. **The named-instance registry:** restore the backup from step 4. The instance's data home under
   `instances\stop-a-session-qa` can be deleted too; nothing else reads it.
5. **The worktree:** `git worktree remove --force ..\devthrottle-stack`

---

## What went wrong on the run that produced this recipe

Written down because a recipe that only lists the happy path is a recipe that only works when the
person who wrote it is driving.

| What happened | What it actually was | What fixed it |
|---|---|---|
| `cc-devthrottle session stop` answered `No such command 'stop'` | The **installed** command line is from 23 July 2026 and predates this mission. Nothing was wrong with the branch. | Step 8b - run the branch's command line off `PYTHONPATH`. Do NOT install over the owner's tools. |
| `cc-devthrottle: command not found`, twice, then it worked again with no change | Transient. The tool is reached through the session's own path and a few calls did not see it. | Retried. Worth knowing so it is not chased as a real fault. |
| Running the tool's `main.py` directly still failed, complaining a variable was not set | It imported the **installed** package, not the repository's, because the repository copy was not on the module path. The variable it wanted was one the remove-the-network-port mission deleted - a symptom of the stale install, not of the branch. | Staging the source as `cc_devthrottle` on `PYTHONPATH`, which is what step 8b does. |
| `cmd /c run-gateway.cmd` was not found although the file was there | The working directory was not what the shell thought it was. | Give the batch file its absolute path. |

---

## What this run proved, and what it did not

**Proved, live, against a Gateway and a Director both built from this branch:**

- A real agent process was ended. `stopped 75f2e3f2 - process 16316 ended, row removed`, and process
  16316 no longer existed afterwards. That closes the "nothing has stopped a real session" gap the
  Phase A report named as the mission's largest risk.
- **Ruling 2, on a dirty worktree.** A second session was stopped while its repository held an
  uncommitted file. It went through without argument, the answer named the worktree and said the
  changes were left untouched, and the file was still on disk afterwards, byte for byte.
- **Ruling 3's second stop.** Running the same stop again answered
  `not on this fleet - nothing in this account carries the id 75f2e3f2, so no machine was asked and
  no machine's processes were searched`, and **exited zero**. Note it is `notOnFleet` rather than
  `alreadyStopped`, and that is correct: the first stop removed the row, so there is no longer a
  machine to ask. `alreadyStopped` is for a row that is still present with no process behind it.
- **Ruling 4's refusal.** A stop with no reason was refused, said a reason was what was missing, named
  the flag that supplies it, and **exited non-zero**.
- **Ruling 4's audit trail, which is the ground the owner accepted the ruling on.** Both stops wrote
  rows, and they read back out of `GET /gateway/governance/audit-events?eventType=stopped` carrying
  the exact reason that was typed, `category: intervention`, `eventType: stopped`, and an actor.
- The machine-readable answer (`--json`) parses and carries the whole folded response.
- The authentication gate is genuinely on: an unauthenticated call is refused with 401.

**NOT proved by this run, and named so nobody reads it as covered:**

- **No interface control was used.** Every stop here went through the command line. The Cockpit
  button, the phone and the Director window are Phase B's own work and are not exercised by this
  recipe. Photographing those is the report's job, not this one's.
- **Single tenant only.** A locally hosted Gateway resolves every request to the Local tenant. Nothing
  here says anything about the hosted multi-tenant path or the cross-tenant isolation the parked suite
  covers.
- **The actor was a machine token**, not a session key. The route a session's own key takes through
  the allow list was not exercised here.
- **`stoppedNotDescribed` was not produced**, because both halves of this stack are the same commit.
  It needs a Gateway from this branch talking to an older Director, which this recipe does not build.
- **One machine.** Everything ran on SORENLAPTOP. Nothing was stopped across a machine boundary.
- **This is a builder's smoke test, and it is not the proof.** It exists so that the report is
  possible, not to stand in for it. A builder photographing his own work reaches for the path he
  already knows works.
