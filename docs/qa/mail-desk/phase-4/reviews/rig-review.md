# Review: the Mail Desk rig (pull request 4281, branch md4-rig)

Reviewer seat, Mail Desk phase 4 (issue #4213). Opened by the phase 4 Tech Lead.

## Scope

Read: `git diff origin/mail-desk-phase-4...origin/md4-rig` at `9fa0bf785` - `docs/qa/mail-desk/rig/run_qa.py`,
`checks/screen.py` (the wrapper), `tests/test_wrapper.py`, `compose-morning/`, `README.md` and the committed
`transcript.txt` (a run of `a5554b1d4`; the only change since is the transcript itself). Measured against
`docs/qa/mail-desk/phase-4/CONTRACT.md` section 5 and the six questions in the brief.

Run: the wrapper's tests (`uv run --no-project --with pytest --with "msal>=1.28" python -m pytest
docs/qa/mail-desk/rig/tests`): **10 passed**. I did not run the rig. While the Tech Lead's run was live I read
`/proc` command lines and the rig's Director log on this machine, and looked at the owner's `~/.claude` folder. I
changed nothing outside this file.

Not reopened: the loopback model setting (already reviewed in `model-setting-review.md`).

## Verdict

**The safety core holds.** The wrapper rewrites only `https://graph.microsoft.com` requests, refuses every other
address, never builds MSAL and never reads the real Director folder. Each of its refusals is tested. The fake model
accepts only the rig's own key. The Gateway gets a minimal environment. Addresses, keys and paths are kept out of the
transcript. I found no real address, token or owner path in the committed files.

**The transcript cannot be quoted as it stands.** One section title claims something the transcript does not show
(finding 1). Two safety lines report "none" from tools that cannot see what they claim to look for (findings 2 and 3).
Several CHECK lines cannot fail (finding 4). The rig also writes into the owner's real `~/.claude` folder, and the README
does not say so (finding 5). Finding 6 is minor. Every finding can be fixed in the rig. None of them shows a defect
in the product.

## Findings

### 1. The switch-off section does not prove a Director report was refused, and the tool that would show it can never find a line

`run_qa.py:681-686` (`director_log`), `run_qa.py:1443-1462` (`section_switch_off`), `transcript.txt:1600-1641`.

- **Claim.** The section title says "the Director's report is refused (409 mail_desk_off) and nothing is recorded".
  The contract (section 5, failure cases) asks for "a Mail Desk report refused".
- **What the transcript shows.** Two absences, "no run was recorded" and "no decision was recorded", after a
  150-second sleep. The Director log search prints `(none)`.
- **Why the search can never return a line.** It keeps a line only when `line[:19] >= since`, and `since` is
  universal time (`t = now()`, `datetime.now(timezone.utc)`). The rig sets `TZ=UTC` for the Gateway but not for the
  Director, and the Director writes its log in local time. On devlinux that is four hours behind universal time. Its
  current log starts `2026-10-20 08:17:53` while the clock reads 12:17 universal time. So for a whole day every
  Director line sorts before `since`, and the search returns nothing whatever the Director did.
- **Why the two checks prove nothing.** They also pass when no check was tried at all, for example if the Gateway
  stops handing the Mail Desk start out while the switch is off, or if the Director never polled in those 150
  seconds. The run rows show the gap (11:49:07, then 11:53:37) but not why.
- **Failure scenario.** A defect makes the Director send its report while the switch is off, and the Gateway quietly
  drops it with a 200 instead of refusing it. Nothing is recorded, both checks pass, and the transcript and the
  final report tell the owner the refusal was proven.
- **Fix.** Make it a presence: the Director log shows a 409 `mail_desk_off` for this trigger inside the window, with
  the time compared in the Director's own zone. Or start the Director with `TZ=UTC` too.

### 2. "Processes still running from the rig folder: (none)" cannot see the Gateway or the Director, and the same blind guard lets a second run delete a live run's folder

`run_qa.py:648-660` (`rig_processes`), `run_qa.py:1999-2002` (start-up guard and `shutil.rmtree(RIG)`),
`run_qa.py:2062`, `transcript.txt:2889-2890`.

- **What the tool does.** `rig_processes()` matches processes whose command line contains `/tmp/mail-desk-rig`. The
  Gateway runs as `<repo>/src/CcDirector.Gateway/bin/Debug/net10.0/CcDirector.Gateway --port N`. The Director runs
  as `<repo>/local_builds/linux/cc-director-linux5` with no arguments. Their rig folders are passed in the
  environment, not on the command line, so neither ever matches.
- **Seen on this machine.** During the Tech Lead's live run, its Gateway (pid 3010552) and Director (pid 3011030)
  were both running. The same match found one process, and that process was my own shell command, which contains the
  string.
- **Harm, part 1.** The end-of-run line "(none)" says nothing about the two processes that matter most. The stop is
  recorded only for the Director (its exit code). A Gateway that outlives its 30-second SIGTERM wait leaves one line
  that is easy to miss, while "(none)" reads as an all-clear.
- **Harm, part 2.** The start-up guard is meant to refuse when a run is still going. **Failure scenario:** a second
  session runs `run_qa.py` while the first run's Gateway and Director are alive, which is exactly what the brief
  warns about. Before any sessions have started (or between them), the guard sees nothing and runs
  `shutil.rmtree("/tmp/mail-desk-rig")`. That deletes the live Gateway's `gateway.db`, the Director's root, and the
  first run's sign-in folder and definitions while those processes are using them. The first run then fails, or
  worse, keeps running on half-deleted state.
- **Fix.** Match processes by working directory or by `CC_DIRECTOR_ROOT` in `/proc/<pid>/environ`, the way
  `session_agent_pids` already matches by working directory. Or keep a pid file for the Gateway and the Director under
  the rig and check those pids. Make the end-of-run line a CHECK that both pids are gone.

### 3. "No model question left this machine" is checked against a log that never names a model call's address

`run_qa.py:2030-2032`, `transcript.txt:2797-2800`; the Gateway source is `MailDesk/MailDeskModel.cs:254,308,341` and
`MailDesk/MailDeskModelFactory.cs:86`.

- **What the check does.** It passes when the Gateway log has no line containing `devthrottle.com`. But the Mail
  Desk model client logs only `question=`, `answer=`, `errorKind=` and `model=` for each call. No line ever carries
  the base address. Only start-up logs it (`MailDeskModelEndpointConfig`, transcript line 31). So the check would
  pass even if every question went to the hosted endpoint.
- **Failure scenario.** A later change resolves the base address somewhere that ignores `mailDeskModelBaseUrl` for
  one of the two questions. Those calls go to the hosted endpoint with the rig-only key and fail as `failed`. The log
  still has no `devthrottle.com`, so this CHECK PASS goes into the report as proof that nothing left the machine.
- **What does hold.** The fake model did receive calls carrying the rig-only key, and the start-up line names the
  loopback host. That is real evidence. It just is not what this line claims.
- **Fix.** Count the Gateway's `[MailDeskModel] AskAsync: question=` lines in the run and check that the count equals
  the number of calls the fake model recorded. Every question the Gateway asked must then have reached the fake.

### 4. Several CHECK lines cannot fail, because the decisions table holds one row per message

`run_qa.py:1377`, `1412`, `1435`, `1546-1547`; the index is `Data/GatewayDbContext.cs:1123`.

- **Why they cannot fail.** `mail_desk_decisions` has a unique index on (account, channel, message key), with the
  comment "a check that reports the same message twice records it once". A message read twice therefore leaves one
  row. These checks count rows:
  - "more mail than one batch": `23 decisions, 23 different message keys: none twice`
  - "Graph answers an error": `decided once`
  - backup: `<= 1` rows for "Invoice 1042", reported as "the cursor continued". It also passes with 0.
  - model: `attemptCount` defaults to 2 when the field is missing, so the `>= 1` half always passes.
- **What actually shows the claims are true.** The run rows the transcript prints but does not check. Batches:
  `10 + 10 + 3`, each with "0 already decided" (lines 1580-1582). Backup: the stand-in's run is "1 decided, 0 already
  decided", count 1 (line 2745).
- **Failure scenario.** A cursor that slips back makes the next run re-report old messages. The rows stay unique,
  every one of these CHECKs passes, and only someone reading the reason strings would notice "N already decided".
- **Fix.** Check the counts. Batches: the run counts sum to 23 and every reason has "0 already decided". Backup: the
  stand-in's run has count 1 and "0 already decided". Graph error: the recovering run reports the email as decided,
  not already decided. Model: read `attemptCount` without a default.

### 5. The rig writes into the owner's real `~/.claude`, and the README does not say so

`run_qa.py:603-609` (the `~/.claude` link), `run_qa.py:1017` (`CLAUDE_PROJECTS`), `README.md` (the "seats' sign-in"
point under "What it does NOT prove").

- **What happens.** The scratch home links `.claude` to the owner's own folder, so every seat session writes its
  agent transcript into the owner's `~/.claude/projects` and its prompts into `~/.claude/history.jsonl`. Nothing ever
  removes them: `rmtree(RIG)` removes only `/tmp/mail-desk-rig`.
- **What is there now.** 49 folders named `-tmp-mail-desk-rig-*` in `~/.claude/projects` (about 7 MB in the largest
  three), and 88 lines naming the rig in `history.jsonl`.
- **What the README says.** It does name the link, but only as a sign-in matter. The brief's own list of what the rig
  may write outside its folder is the `~/.claude.json` link with `~/.local/bin` left unchanged. Seats and the backup
  stand-in also share the owner's uv cache (`UV_CACHE_DIR`, lines 630 and 1002). That one is harmless, but it is also
  not stated.
- **Harm.** Every full run adds a dozen or more sessions to the owner's own resume list and prompt history. They
  carry the rig's prompts, and they pile up from run to run. The README tells the owner exactly what the rig touches,
  and here it is incomplete.
- **Fix.** Say it in the README. Better still, give the seats their own `CLAUDE_CONFIG_DIR` under the rig. If that
  breaks the sign-in, copy only the credentials file into a scratch `.claude` rather than linking the whole folder.
  Any clean-up must delete only folders whose names start `-tmp-mail-desk-rig-`, and nothing else.

### 6. (Minor) "Nothing of a preview anywhere" is a record, not a check

`run_qa.py:891-911` (`marker_anywhere`), called at `1331` and `1816`. The result is returned and thrown away. If the
preview marker were ever found in `gateway.db` or the Gateway log, the hit would be printed, the run would still end
with every CHECK passed, and "103 checks passed" would still be the summary quoted. Make it
`check(not marker_anywhere(...), ...)`.

## Checked, no finding

- **A seat reaching a real mailbox.** The owner's real `cc-outlook` is still on a seat's PATH (`~/.local/bin`, after
  the stub). But its saved sign-in is found through `CC_DIRECTOR_ROOT`, which the Director sets to the rig's root, so
  it finds none. A seat would have to set that variable to the owner's folder on purpose. The guarantee therefore
  rests on `CC_DIRECTOR_ROOT` as well as on the stub, and the README could say so.
- **The kill signal.** In the transcript it went only to a process working in
  `<rig>/director-root/.../factory-run/<session>/work`. `session_agent_pids` does not itself require the folder to be
  under the rig. A one-line check before `os.kill` (`run_qa.py:1722`) would make that a rule instead of a habit.
- **A seat doing something the rig did.** Every place the rig acts for a seat says so plainly in the title and on
  the request line ("as session ..., with its own key, as its seat would call it"; "the rig typed /exit"; "the rig
  ended session ...'s agent with a kill signal"). The one case a seat acts in, `done`, is a real seat with a 6-minute
  deadline, and the README says so.
- **Waits.** Every `wait_for` has a deadline. The two fixed sleeps (70 and 150 seconds) are on the real clock and
  bounded. Only the switch-off sleep carries weight, and finding 1 covers it.
- **The public repository rule.** The committed files hold only example addresses and `email@devthrottle.com` (none
  of the latter, in fact). `shown()` leaves no `<other address>`, `<user>` or `<token>` in the transcript; the only
  hit is inside a quoted code comment. There are no owner paths, because `<home>`, `<repo>` and `<rig>` are used.
- **The README's not-proven list** matches what the transcript shows: the stand-in backup is called a stand-in, the
  08:00 email is composed rather than sent, and the seats work from `check.json`. The exceptions are findings 1 and 5.
