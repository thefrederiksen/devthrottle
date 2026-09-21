# Runbook - one real reply, end to end, through the trigger

For the Delivery Lead's run on 22 September 2026. It was NOT run on 21 September: the Delivery Lead ruled that no
Site Checker verdict may be hand-written, and without one there is no business thread to put a reply on (see
"Before you start").

**What it proves:** one real customer reply, inserted into the owner's mailbox on a business thread of the
live-test record, is counted by the REAL check (`cc-website-factory mail-waiting`), the trigger starts ONE real
Front Desk session in the factory folder, and that session leaves a Gmail DRAFT answering it - and nothing ran
before the trigger fired. **Goal: insert to draft in under 10 minutes.**

**Never send anything.** Inserting is not sending (`messages.insert` writes straight into the mailbox; nothing
crosses the internet). Every draft stays in the drafts folder, is never sent, and is never deleted.

Everything below runs on SOREN_NORTH, from Git Bash unless it says PowerShell. `<wt>` is the devthrottle worktree
the stack is built from; `<cc>` is a cc-consult worktree cut from `origin/main`.

## Before you start

1. **A business thread to reply on.** The live-test record
   (`%LOCALAPPDATA%\website-factory\factory-livetest.db`) must have a thread whose counterparty is an allowlisted
   test address that is NOT suppressed. On 21 September there was none:
   - `wftest1@duksrevo.com` is suppressed (the phases 3-4 "remove me" check).
   - `wftest2@duksrevo.com` (prospect `wf-test2`, paid) and `wftest3@duksrevo.com` (prospect `wf-test3`,
     contactable) are not suppressed, but neither has a thread (`show <prospect> --json`: `threads: []`).
   - A thread is made by a Scout `draft-outreach`, which refuses unless the prospect's current site has a PASSED
     Site Checker verdict. `wf-test3`'s current site is 4 (`wftest-change-preview`), check `pending`: its automated
     check passed (row 36) but no Site Checker verdict was ever recorded. It is also the phases 3-4 change-request
     fixture.
   So the run needs, first, a REAL Site Checker run that passes a `wf-test3` site (the Codex seat, back from
   04:46), or another way the Delivery Lead chooses. Then:
   ```bash
   W=<the rig venv>/Scripts/cc-website-factory.exe
   DB="$LOCALAPPDATA/website-factory/factory-livetest.db"
   "$W" --db "$DB" --actor trigger show wf-test3 --json        # the site's check_result must read passed
   "$W" --db "$DB" --actor scout draft-outreach wf-test3 --subject "[WF TEST] trigger end-to-end" --file outreach-wftest3.html
   ```
   The outreach body used on 21 September is in `%TEMP%\wbf-live\e2e\outreach-wftest3.html` (four lines, "[WF TEST]
   ... It is never sent."). Record the Gmail draft id and the thread id it prints: that is test draft ONE.
2. **Nothing waiting yet:** `"$W" --db "$DB" --actor trigger mail-waiting --json` must print `"count": 0`. If it
   counts anything, find out what before going on - a reply already waiting makes the timing meaningless.
3. **No session in the stack:** `curl -s http://127.0.0.1:7898/sessions` prints `[]`.

## 1. Stand the isolated stack up

Skip what is still running from 21 September (`Get-ScheduledTask -TaskName wbf-live-*` in PowerShell; `curl -s
http://127.0.0.1:7898/healthz`). Everything lives under `%TEMP%\wbf-live` except the two worktrees. Nothing of the
owner's is touched: the Gateway and the Director each follow `CC_DIRECTOR_ROOT`, and neither uses Tailscale.

1. **devthrottle worktree** at the head of pull request 3281, or main once it has merged:
   `git -C D:/ReposFred/devthrottle worktree add ../devthrottle-wbf-live -b <branch> origin/main`.
2. **Gateway**, with its Cockpit, published from `<wt>`:
   ```bash
   dotnet publish src/CcDirector.GatewayApp/CcDirector.GatewayApp.csproj -c Release -r win-x64 -p:SelfContained=false \
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RunMobileBuild=false -p:RunCockpitBuild=true \
     -o "$TEMP/wbf-live/gw-stage"
   ```
   Its root `%TEMP%\wbf-live\gw-root\config\config.json` is `{ "factoryAgents": { "enabled": true } }` - the switch
   is on in THIS Gateway's config only. `live-rig/launch-gateway.cmd` (copy it to `%TEMP%\wbf-live\` and point it at
   the stage folder) sets `CC_DIRECTOR_ROOT` to that root, `CC_GATEWAY_NO_TAILSCALE=1`, `CC_GATEWAY_NO_AUTH=1`, and
   runs `devthrottle-gateway.exe --port 7898 --no-autostart`. Register and start it (PowerShell):
   ```powershell
   $cmd = "$env:TEMP\wbf-live\launch-gateway.cmd"
   $a = New-ScheduledTaskAction -Execute $cmd -WorkingDirectory "$env:TEMP\wbf-live\gw-stage"
   $t = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
   Register-ScheduledTask -TaskName wbf-live-gateway -Action $a -Trigger $t -Force
   Start-ScheduledTask -TaskName wbf-live-gateway
   ```
   `curl -s http://127.0.0.1:7898/healthz` must name the commit you built.
3. **Director**, slot 21, never slots 1-4 or the installed app (CLAUDE.md rule 0b):
   `.\scripts\local-build-avalonia.ps1 -Slot 21 -OutputDir "<wt>\scripts\local-build"`. Its root
   `%TEMP%\wbf-live\dir-root\instances\default\config\config.json` carries
   `{ "onboarding": { "completed": true }, "gateway": { "url": "http://127.0.0.1:7898", "token": "wbf-live-no-auth", "streamMode": true } }`.
   `live-rig/launch-director.cmd` sets `CC_DIRECTOR_ROOT=%TEMP%\wbf-live\dir-root` and runs `cc-director21.exe`.
   Launch it ONLY through its own scheduled task `wbf-live-director21` (same three lines as above, working folder
   `<wt>\scripts\local-build`), never from an agent's own process tree. `healthz` then shows `"directors":1`.
4. **Tools**, a scratch virtual environment: `py -m venv "$TEMP/wbf-live/venv"`, then
   `"$TEMP/wbf-live/venv/Scripts/python.exe" -m pip install --no-deps <cc>/ideas/website-factory/factory` and
   `cc-devthrottle` from `<wt>` (`tools/cc_storage`, `tools/cc_shared`, `tools/cc-devthrottle`). Always run
   `cc-devthrottle` against the rig: `CC_GATEWAY_URL=http://127.0.0.1:7898 CC_GATEWAY_SESSION_KEY=wbf-live-no-auth`.
5. **The factory folder must be trusted** by the coding agent, or the Front Desk session stops on the folder-trust
   question and exits (seen on 21 September): add `<cc>/ideas/website-factory/factory` with
   `hasTrustDialogAccepted: true` under `projects` in `~/.claude.json` (done on 21 September for
   `D:/ReposFred/cc-consult-wbf-e2e/ideas/website-factory/factory`).
6. **Use a cc-consult worktree from origin/main**, not the shared checkout: on 21 September the shared checkout was
   28 commits behind with 37 changed files in the factory and live-test folders, so a session there would have run a
   stale Front Desk skill and tool. `git -C D:/ReposFred/cc-consult worktree add --detach D:/ReposFred/cc-consult-wbf-e2e origin/main`
   (it exists from 21 September at `1ea24f4`; refresh or re-cut it from the current `origin/main`).

## 2. The trigger, with the real check

Only one trigger may read the mailbox at a time (the lock is per trigger, GAPS.md G12): keep the stub trigger
paused. Create this one PAUSED, from Git Bash (bash expands `$LOCALAPPDATA` in the prompt; cmd expands
`%LOCALAPPDATA%` in the check at every run):

```bash
export CC_GATEWAY_URL=http://127.0.0.1:7898 CC_GATEWAY_SESSION_KEY=wbf-live-no-auth
DT="$TEMP/wbf-live/venv/Scripts/cc-devthrottle.exe"
"$DT" trigger add \
  --name "Website mail - live-test end-to-end" \
  --factory website-business \
  --agent front-desk \
  --machine SOREN_NORTH \
  --repo "D:/ReposFred/cc-consult-wbf-e2e/ideas/website-factory/factory" \
  --check "C:\\Users\\soren\\AppData\\Local\\Temp\\wbf-live\\venv\\Scripts\\cc-website-factory.exe --db %LOCALAPPDATA%\\website-factory\\factory-livetest.db --actor trigger mail-waiting --json" \
  --every 1m \
  --prompt "You are Front Desk (agents/front-desk.yaml). Use your website-front-desk skill. LIVE TEST: every cc-website-factory command in this run takes --db \"$LOCALAPPDATA/website-factory/factory-livetest.db\". cc-website-factory is not on PATH: run it as py -m cc_website_factory from this folder. New mail: {count} business threads. Handle them: py -m cc_website_factory --db \"$LOCALAPPDATA/website-factory/factory-livetest.db\" --actor front-desk mail-threads --json." \
  --paused
"$DT" trigger show "Website mail - live-test end-to-end"
```

Read it back and confirm the check and the prompt name the SAME file, the live-test record - never
`factory.db`, the real one. `--factory website-business --agent front-desk` match the rig's other triggers, so it
shows on the Cockpit's Front Desk page (`http://127.0.0.1:7898/factory-agents/website-business/front-desk`).
Resume it and let it run at least three checks: each must be `nothing-to-do` and start nothing.

## 3. The run

1. **Before:** save `"$DT" factory activity --json`, `"$DT" trigger runs "Website mail - live-test end-to-end" --json`,
   `"$W" --db "$DB" --actor trigger activity` (the business record's own log), and `curl -s .../sessions` (`[]`).
2. **Insert ONE reply** onto the thread from "Before you start", with the cc-director Python (it has `cc_gmail`):
   ```bash
   printf 'Looks good. How much is it?\n' > reply-wftest3.txt
   "$LOCALAPPDATA/cc-director/pyenv/Scripts/python.exe" <cc>/ideas/website-factory/livetest/insert_reply.py reply \
     --db "$DB" --thread <thread id> --from wftest3@duksrevo.com --body-file reply-wftest3.txt \
     --purpose "trigger end-to-end: one reply for the real check"
   ```
   Write down **T0**, the insert time it prints (it is also the new row in `<cc>/ideas/website-factory/livetest/INSERTED.md`),
   and the inserted message id.
3. **Watch:** `python live-rig/watch_runs.py <trigger id> 600` prints each new run row, the trigger's status and the
   session list every ten seconds. Expect: the next check counts 1 and is answered 202 at once; a `started` row with
   ONE session named `front-desk - Website mail - live-test end-to-end - <time>`; the next check `skipped-running` on
   it while it works.
4. **The draft:** the Front Desk sorts the reply (`interested` - it asks the price) and runs `draft-reply`. Its
   Gmail draft id is in its output, in `"$W" --db "$DB" --actor trigger show wf-test3 --json` (`drafts`), and in the
   business record's activity (`draft.reply`). Write down **T1**, the draft's creation time from the record. That is
   test draft TWO.
5. **After:** save the same four things as in step 1, plus a Cockpit screenshot of the Front Desk page and Activity
   (`live-rig/shoot.py`). Pause the trigger. Let the session finish; stop it only if it is stuck.

## 4. What to report

| Measure | Where it comes from | Pass |
|---|---|---|
| No session before the trigger fired | `sessions` empty before T0; the trigger's runs before T0 all `nothing-to-do` | presence of those rows, not absence of sessions alone |
| One session | exactly one `started` row after T0, and every later check `skipped-running` on it | one |
| Insert to draft | T1 - T0 | under 10 minutes |
| Check to start | the first run after T0: `checkedUtc` to the `started` row's `recordedUtc` | seconds; the report is answered at once |
| The draft is right | the draft's body: only prices from MISSION.md section 3; no send | the Front Desk skill's rules |

Name every row: the trigger run ids, the factory activity rows, the business record's activity rows (sort,
`draft.reply`, `mark-handled`), the session id, the inserted message id, and **both Gmail drafts by id** (the
outreach from "Before you start" and the reply) so the owner can see which drafts in his folder are tests. Leave
both drafts in place. Nothing is ever sent.

## 5. Taking the stack down

Only when the Tech Lead says so. Pause every trigger; stop the Director by its named signal
(`Local\cc-director-shutdown-<director id>`, id from `dir-root\instances\default\config\director\instances\`) -
never a kill; stop the Gateway's task and, if its process outlives the task, that one process by id after checking
its path is the rig's own stage folder; unregister both `wbf-live-*` tasks. Remove worktrees only when nothing
uncommitted is left in them.
