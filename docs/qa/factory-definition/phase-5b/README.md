# Factory Definition, phase 5b - checks, proven on a real Gateway and a real Director

Date: 2026-10-16. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (5.5a and the phase 5b
lines of section 7). Design: the Tech Lead's phase 5b design, sections 1 to 6. Branch:
`factory-definition/p5b-qa-report`, rebased onto `origin/main` at `b4176aabf` (pull request 4048, timed starts with a
check and failed checks as problems), which holds all of phase 5b: 4029 (carried items), 4033 (the Director's runner),
4031 (the Gateway decides), 4046 (the publish's machine questions), 4049 (`check.json` in the kit) and 4048.

This branch adds no product code. It adds the rig, its transcript and this report, and makes one change to the phase 0
email check: two optional rules keys, `graph` and `signIn`, so a rules file can point the check at a fake mailbox (the
real Microsoft addresses when left out), with two tests (22 pass, `phase-0/inventory/email-check/tests-run.txt`).

## What is in this folder

- `rig/run_qa.py` - the rig. It drives a throwaway Gateway and a real Director through every case below and writes the
  transcript.
- `rig/checks/*.py` - the rig's own checks, each a few lines: one that finds nothing, one that finds work, a slow one,
  one per way of breaking the contract, a state probe, an environment probe and a check that exists only in a draft.
- `transcript.txt` - every command the final run made, everything it printed and its exit code, every row it read from
  the rig's database, and what each agent did, read from its own Claude Code transcript. The rig's folder is written
  `<rig>`, the tree `<repo>`, the user's home `<home>`, the machine `<machine>` and the machine's tailnet address
  `<tailnet address>`.

## How it was run

**A throwaway Gateway** built from this tree, on port 7995, with an empty storage root of its own under `%TEMP%`,
Tailscale off, authentication ON and factory agents switched on in that root's own `config/config.json`. Callers are
made as phases 4 and 5 made them: a person (an enrolled browser device row) and the Director (a workstation device row),
each with its own key. The Gateway holds no account sign-in, so the factory email it composes cannot be sent: its own
send refused with "This Gateway is not signed in to DevThrottle, so it cannot send owner email. Sign in from the
Gateway tray. No email was sent."

**A real Director, slot 7 only**: `scripts\local-build\cc-director7.exe`, built from this tree with
`scripts\local-build-avalonia.ps1 -Slot 7`. On the Tech Lead's instruction it was launched through a scheduled task of
its own, `cc-director-launch-p5b`, set up exactly as CLAUDE.md rule 0b sets up `cc-director-launch` (one action, its
working folder, a far-future trigger, on demand only), started with `Start-ScheduledTask`, and stopped by its named
shutdown signal (`Local\cc-director-shutdown-<its id>`). The rig registered the task at the start and unregistered it
at the end. The shared `cc-director-launch` task, which phase 5's QA was using on slot 6 at the same time, was never
touched. The task runs a wrapper that gives the Director its own storage root under the rig, pointed at the rig
Gateway with the rig's workstation key; the Claude Code executable is copied from the owner's Director configuration.

**A fake mailbox**: the phase 0 email check's fake Graph, served by the rig on 127.0.0.1:7994. It answers the sign-in
with a token and each folder read with the messages received at or after the filter's time, oldest first. It reads no
request body (the sign-in's body holds the client entries' values and is dropped unread) and keeps no path that carries
a value; what it was asked is in the transcript, one line per call.

**The email check runs as a definition check**: `checks/new_mail.py` is the phase 0 check byte for byte, and
`files/mail-rules.json` names the entries, the ledger (`files/ledger.json`) and the fake's addresses. It never reached
the real mailbox.

**cc-secrets**: every declared entry already exists on this machine. The mail start declares three: a tenant entry and
a client entry (both settings) and a read-only token of kind secret whose entry has no variable name of its own, so it
arrives in the variable the naming rule gives it. No value was read, printed or kept by the rig; the fake ignores what
it is sent. The missing entry, `qa-p5b-no-such-secret`, is a name that does not exist.

**Real Claude Code seats**, model `claude-sonnet-5-5`, with prompts of one step (print `check.json`). What each agent
did is read from its own Claude Code transcript. Claude Code asks whether to trust each new folder; the rig answered
that question the way a person at the screen would, and the transcript records each time it did. Nothing else was ever
typed into a session.

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
powershell -NoProfile -File scripts\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\scripts\local-build"
uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with tzdata python docs/qa/factory-definition/phase-5b/rig/run_qa.py docs/qa/factory-definition/phase-5b
```

The committed transcript is from the final run, on 2026-10-16 from 01:15 to 02:06 UTC, from `b4176aabf` plus this
branch's rig and email check exactly as committed here (the branch was squashed to one commit and rebased afterwards;
only this README and the phase 0 notes changed after the run). Every id and time below is from that run. Two earlier runs of the rig, one
on main before 4048 and one on 4048's head, are not committed; one result of the first is quoted in finding 6.

**The definition.** Version 1, published: a boss and nine seats, each with an interval start and a check - `mail`
(every minute, the email check, three secrets), `probe` and `bigstate` (the probes), and six that break the contract
(every 24 hours, so each runs once). Version 2 is a draft that adds `drafty` and is never published. Version 3,
published, is version 1 plus five timed starts with checks. Every file of every version is in the transcript above the
command that saved it.

## The cases

### 1. The email check on an empty inbox: nothing to do, no session, recorded

The first check ran when the publish assigned it. The fake was asked once and answered nothing:

```
2026-10-16T01:15:43Z GET qa-outreach@example.com folder=inbox since=2026-10-15T01:15:43Z -> 0 message(s)
```

Recorded (`trigger runs`, oldest at the bottom):

```
2026-10-16T01:15:44.2013216Z  nothing-to-do  count=0
```

No session: the row's `SessionId` is empty, and the only session of the `mail` seat in the whole run is the one case 2
started. The state the check left was stored (row 1 of case 4).

### 2. Mail from a listed sender: one session, the senders in its first prompt and in its kit's `check.json`

The rig put a message from `Partner@Firm-A.example` (written to on 2026-10-07 in the ledger) beside the unlisted one.
The third check:

```
2026-10-16T01:18:41.0617779Z  started  count=1 session be1e745e-3e10-4a7c-8ae8-4e07b0c316f8
```

The session's first prompt, read from its own Claude Code transcript:

```
QA-P5B-MAIL. The check counted 1 new mail. What it found:
2026-10-16T01:16:14Z from partner@firm-a.example - Re: the proposal [inbox]
Do exactly one step, then stop: run  cat "$CC_FACTORY_KIT_DIR/check.json"  with Bash and quote what it printed.
```

What the agent's `cat` printed, and the same file read by the rig from the kit folder while the session ran:

```
{
  "checkPath": "checks/new_mail.py",
  "count": 1,
  "context": "2026-10-16T01:16:14Z from partner@firm-a.example - Re: the proposal [inbox]",
  "checkedUtc": "2026-10-16T01:18:41.0617779Z",
  "definitionVersion": 1,
  "seat": "mail",
  "start": 1
}
```

The session started on the Director that ran the check (`directorId` `fda95749-...` in the session list, the history
row and the check's row), in its kit's `work` folder, as seat `mail` of definition version 1. The unlisted sender is in
neither the prompt nor the file. One session of the seat existed.

### 3. Mail from an unlisted sender only: nothing to do

The second check, with only `newsletter@shop.example` in the inbox (not in the ledger):

```
2026-10-16T01:17:11Z GET qa-outreach@example.com folder=inbox since=2026-10-15T01:15:43Z -> 1 message(s)
2026-10-16T01:17:11.966791Z  nothing-to-do  count=0
```

### 4. A second run that remembers the first through the state file

The fourth check saw the same inbox. It asked the fake only from the time the third run's state named, and counted
nothing:

```
2026-10-16T01:20:26Z GET qa-outreach@example.com folder=inbox since=2026-10-16T01:16:14Z -> 1 message(s)
2026-10-16T01:20:26.9681205Z  nothing-to-do  count=0
```

The state rows of the start (`factory_check_state`, `OwnerKey trigger:8328bf27-...`), newest last:

```
Sequence, Bytes, WrittenUtc, WrittenBy, RunRef, Reset, Starts
1, 77, 2026-10-16 01:15:44, fda95749-..., aabd24a1-..., 0, {"folders": {"inbox": {"idsAtNewest": [], "newest": "2026-10-15T01:15:43Z"}}}
2, 82, 2026-10-16 01:17:11, fda95749-..., 8772bd0c-..., 0, {"folders": {"inbox": {"idsAtNewest": ["m-1"], "newest": "2026-10-16T01:14:11Z"}}}
3, 82, 2026-10-16 01:18:41, fda95749-..., 027f9998-..., 0, {"folders": {"inbox": {"idsAtNewest": ["m-2"], "newest": "2026-10-16T01:16:14Z"}}}
```

Row 3 was written by the run that started the session (`RunRef` is that run). The fourth run wrote the same state back,
so no fourth row: state is stored only when it changes. `factory check-state show qa-checks mail` prints row 3 as the
newest.

### 5. Each failure recorded as failed with its reason, on the Open problems card and in the 08:00 email

Six were run live; each check ran once, on the Director, and was recorded as failed with the contract's reason, quoted
from the `trigger_runs` rows:

| Start | The check | Reason recorded |
|---|---|---|
| `failexit` | exits 3, a line on standard error | `exit code 3: the mailbox answered 503 Service Unavailable` |
| `failtimeout` | sleeps 330 seconds (a 24-hour start's timeout is the five-minute ceiling) | `the check timed out` (checked 01:20:38, five minutes after it started) |
| `failnotjson` | prints `all quiet today` | `the output is not JSON: all quiet today` |
| `failnocount` | prints `{"found": 2}` | `the output has no count: {"found": 2}` |
| `failcontext` | prints a context of 9,000 bytes | `the context is 9000 bytes; it may be at most 8192 bytes (8 KB)` |
| `failsecret` | declares `qa-p5b-no-such-secret` | `the check could not start: No secret named 'qa-p5b-no-such-secret' is on this machine.` |

The Director's own log for the last: `[DefinitionCheckRunner] check could not start: No secret named
'qa-p5b-no-such-secret' is on this machine.` - the check never ran. `trigger list` shows each start red with its
reason, for example `qa-checks-failexit-start-1  [RED - check failed: exit code 3: the mailbox answered 503 Service
Unavailable]`.

**The Open problems card** (`GET /gateway/factories/qa-checks`, its `problems`) lists all six, each with the reason,
for example:

```
"seatText": "Failtimeout - qa-checks-failtimeout-start-1",
"what": "Check failed: the check timed out.",
"noSessionText": "No session - the run never started one.",
```

and `GET /cron/problems?factory=qa-checks` answers `"count":6`. The factory's status reads `FAILING`, "6 failures from
6 seats".

**The 08:00 email** was composed by the Gateway's own factory email sweep, not by the rig. The rig set the account's
time zone to `Etc/GMT-6`, whose next 08:00 (02:00 UTC) came after every failure was recorded, switched the factory
email on, and waited. The sweep composed the 08:00 slot and kept it; its send was refused, as above.
`GET /gateway/factory-emails?state=all`:

```
"slot":"08:00","kind":"morning-report","state":"failed","title":"Factories: 6 problems still open - Friday 16 October",
"factories":["qa-checks","qa-plain"],"problemCount":6,
"detail":"This Gateway is not signed in to DevThrottle, so it cannot send owner email. ... No email was sent."
```

The route answers the email as HTML only, so the rig's "text body" line reads `(no text)`; the HTML is in full in the
answer just above it in the transcript. Its lines, with the markup removed:

```
6 problems still open
2 factories, 2 runs done, 4 runs with nothing to do, 0 runs did not run.
STILL NOT FIXED
QA Checks - Failtimeout - qa-checks-failtimeout-start-1 - Check failed: the check timed out.
QA Checks - Failsecret - qa-checks-failsecret-start-1 - Check failed: the check could not start: No secret named 'qa-p5b-no-such-secret' is on this machine.
QA Checks - Failnocount - qa-checks-failnocount-start-1 - Check failed: the output has no count: {"found": 2}.
QA Checks - Failexit - qa-checks-failexit-start-1 - Check failed: exit code 3: the mailbox answered 503 Service Unavailable.
QA Checks - Failcontext - qa-checks-failcontext-start-1 - Check failed: the context is 9000 bytes; it may be at most 8192 bytes (8 KB).
QA Checks - Failnotjson - qa-checks-failnotjson-start-1 - Check failed: the output is not JSON: all quiet today.
LAST 24 HOURS   Factory     Done  Nothing to do  Did not run  Left open
                QA Checks   1     4              0            0
                QA Plain    1     0              0            0
```

The undefined factory's broken COMMAND trigger is not in the card, the problems fold or the email (case 13).

**Three failures this machine cannot produce**: no Python, packages declared with no `uv`, and a secret marked for
login only. Python and `uv` are installed here, and every cc-secrets entry on this machine allows `run`; adding a
login-only entry is owner-only. They are shown ONLY through the Director's own tests, against a fake process table and a
fake `cc-secrets list --json`, run by the rig at the start of the final run:

```
Passed ...DefinitionCheckRunnerTests.RunAsync_NoPython_StartErrorNamesWhatItLookedFor(isWindows: False, name: "python3")
Passed ...DefinitionCheckRunnerTests.RunAsync_NoPython_StartErrorNamesWhatItLookedFor(isWindows: True, name: "python.exe")
Passed ...DefinitionCheckRunnerTests.RunAsync_ASecretAllowedForLoginOnly_StartErrorNamesItAndTheCheckNeverRuns
Passed ...DefinitionCheckRunnerTests.RunAsync_PackagesDeclaredAndNoUv_StartErrorAndNoOtherInterpreterIsTried
Total tests: 4
```

The reasons those tests assert: `no Python on this machine (looked for python.exe on PATH)`, `the check declares
packages and uv is not on this machine`, and `The secret 'website-login' on this machine is not allowed for run (it is
allowed for: login).` Each is a start error; the live `failsecret` case above shows a start error travelling from the
Director to the run's reason, the card and the email.

### 6. A check in an unpublished draft is never run

Version 2 adds the seat `drafty` with `checks/draft_only.py` and is saved, not published. The download route, asked
with the Director's own key:

```
$ GET /directors/fda95749-.../factory-checks/qa-checks/2
  status: 404
  answer: {"code":"version_not_published","error":"version 2 of the definition of qa-checks is a draft, not published; a check is only ever downloaded from a published version"}
```

The same route answers 200 for version 1. After 70 seconds (two of the Director's polls): no trigger of `drafty`, no
check state, and no `draft_only.py` anywhere under the Director's `factory-checks` folder, which held only version 1.

### 7. A timed start whose check finds nothing: "nothing to do", not a problem

Version 3 adds timed starts firing at 01:26 UTC. `tquiet`'s run:

```
Seat, FiredUtc, Result, InfraStatus, ClaimedByDirectorId, CheckCount, CheckedUtc
tquiet, 2026-10-16 01:26:01.57, nothing-to-do, nothing-to-do, fda95749-..., 0, 2026-10-16 01:26:08.11
```

`schedule runs` shows it with Infra `nothing-to-do` and no session. It is in neither the card nor the problems fold;
the email's day line counts it under "Nothing to do".

### 8. A timed start whose check finds work: one session, pinned to the Director that ran the check

`twork`'s check printed count 2. Its run:

```
twork, 2026-10-16 01:26:01.58, ok, started, fda95749-..., 2, 2026-10-16 01:26:08.13, session b2537673-a0b4-4580-9c08-d543c2d60f6b
```

The session's history row against the run's claim:

```
SessionId, SessionDirector, CheckDirector
b2537673-..., fda95749-1c67-4b4e-b75c-5526e91736ac, fda95749-1c67-4b4e-b75c-5526e91736ac
```

Its first prompt carried the scheduled run's brief (definition version 3) and the start's prompt with `{count}` and
`{context}` replaced: "QA-P5B-TIMED. The timed check counted 2. What it found: QA-P5B-TIMED-FOUND: two items found by
the timed check (item-a, item-b)". Its `cat` of `check.json` printed `"checkPath": "checks/work.py", "count": 2`, that
context, `"definitionVersion": 3`, `"seat": "twork"`, `"start": 1`. The agent then reported the run `ok`, as a
scheduled run's prompt asks, and the session closed itself - so the session list and the kit folder no longer held it
when the rig looked; the agent's own transcript is the record.

### 9. A state file grown past 8 KB, still read and stored whole

`bigstate` writes about 20 KB of state and, on every later run, checks the state it was given against its own hash.
Four runs, four rows:

```
Sequence, Bytes, ..., Starts
1, 20528, ..., {"run": 1, "previousRunFound": null, ...
2, 20559, ..., {"run": 2, "previousRunFound": {"bytesRead": 20528, "whole": true}, ...
3, 20559, ..., {"run": 3, "previousRunFound": {"bytesRead": 20559, "whole": true}, ...
4, 20559, ..., {"run": 4, "previousRunFound": {"bytesRead": 20559, "whole": true}, ...
```

Each run received the previous run's state byte for byte, and each row holds the whole state.

### 10. A check reaches no secret it did not declare through the Director, and sees no `CC_` variable of the Director

`probe` declares one entry. What it wrote into its state (names and true/false only):

```
"CC_CHECK_SECRET_VARS": {"cloudflare-account-id": "CLOUDFLARE_ACCOUNT_ID"},
"declaredVariableSet": {"CLOUDFLARE_ACCOUNT_ID": true},
"undeclaredVariableSet": {"CLOUDFLARE_REGISTRAR_TOKEN": false, "POSTHOG_PROJECT_ID": false, "POSTHOG_PROJECT_KEY": false},
"variablesBeginningCC": ["CC_CHECK_FACTORY", "CC_CHECK_SEAT", "CC_CHECK_SECRET_VARS", "CC_CHECK_STATE_FILE"],
"cc-secrets run of an undeclared entry, from inside the check": {"exit": 0, "said": "set", "stderr": "[cc-secrets] command exited 0"}
```

The Director handed the check its declared entry and nothing else: the variables of the mail start's entries were
absent, and the only `CC_` variables were the check's own four - none of the Director's, though it was started by a
task on the owner's machine. **But the last line is a finding (finding 1):** the check called `cc-secrets run` itself on
an entry it did not declare (a setting), and the value arrived.

### 11. A schedule sweep fires other schedules on time while a slow check runs

`tslow`'s check takes 200 seconds. `tlater1` and `tlater2` fire one and two minutes later. The runs when `tlater2` had
fired:

```
Seat, FiredUtc, Result, InfraStatus, ClaimedByDirectorId, CheckCount, CheckedUtc
tslow, 2026-10-16 01:26:01.58, checking, check-due, fda95749-..., None, None
tlater1, 2026-10-16 01:27:01.57, nothing-to-do, nothing-to-do, fda95749-..., 0, 2026-10-16 01:27:07.95
tlater2, 2026-10-16 01:28:01.56, checking, check-due, None, None, None
```

Each fired 1.6 seconds into its minute while `tslow` was still `checking`; `tlater1`'s check had already run and
reported. `tslow` reported at 01:29:28 (`nothing-to-do`).

### 12. A publish with an interval start and no check refused; `preventOverlap: false` on a checked start refused

Both are refused already when the draft is saved, so no version exists to publish:

```
$ cc-devthrottle factory definition save qa-checks --dir <rig>\definitions\v-no-check ...
Error: The definition was not saved: the check found 1 error in the definition of qa-checks. Fix each one where it is and try again.
errors[1]:
  seats/nocheck.yaml:7: A start with every must have a check: an interval with no check is a schedule that runs too often. Add a check, or use cron.
[exit 1]

$ cc-devthrottle factory definition save qa-checks --dir <rig>\definitions\v-overlap-off ...
errors[1]:
  seats/overlap.yaml:9: A start with a check always prevents overlap, so preventOverlap cannot be false here. Remove it.
[exit 1]
```

`factory definition history` afterwards lists versions 1 and 2 only.

### 13. A factory with no definition is unchanged

`qa-plain` is registered from a manifest with its folder and has no definition. Its two COMMAND triggers ran on the
Director as commands in that folder (`[DirectorTriggerRunner] check: trigger=qa-plain-broken, command=python
broken_check.py`):

```
qa-plain-quiet   2026-10-16T01:15:39.5061127Z  nothing-to-do  count=0
qa-plain-broken  2026-10-16T01:15:39.5097844Z  failed  count=- reason: exit code 2: cannot reach the shop
```

The broken one is red on `trigger list` and appears nowhere among the problems (the card, `/cron/problems` and the
email name only `qa-checks` starts). Its schedule's run now started a session in the registered folder: the agent's
`pwd -W` printed `<rig>/repos/qa-plain`, and its history row carries no seat or definition version.

### 14. The live mailbox run: NOT run

It waits on the owner's app registration for the Outreach mailbox. Everything above used the fake mailbox.

## The publish

Version 1's publish printed, among its applied lines, one per start:

```
Trigger qa-checks-mail-start-1 created for seat mail: every minute, check checks/new_mail.py with secrets cloudflare-account-id, posthog-project-id, cloudflare-registrar-token, on <machine>.
```

Version 3's publish kept every interval trigger, each with "kept as it is; its check is read from version 3", and
created five schedules carrying checks, for example "Schedule cj_6af2e2 (QA Checks - Twork) created for seat twork:
Daily 01:26 UTC, check checks/work.py." Its machine question answered "<machine> does not hold the secret
'qa-p5b-no-such-secret'". After version 3 the Director's `factory-checks` folder held version 3 only: version 1's folder
was removed once no assignment named it.

## The check, as run

The mission's check (section 7, all seven lines), in this worktree on `b4176aabf` plus this branch, before the rebase
onto `fddfcaac9` (another mission's change to the sessions filter). This branch changes no product code.

| Line | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN, 10 suites `outcome=Completed` |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "...Factory|...Cron|...Trigger"` | 2728 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "...Factory"` | 28 passed, 0 failed |
| `dotnet test src/CcDirector.Core.Tests --filter "...Factory|...InheritedSessionEnvironment"` | 20 passed, 0 failed |
| `npm run typecheck` | exit 0 |
| `npm test --workspace @devthrottle/client-core` | 170 files, 2113 tests passed |
| `python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger"` | 1216 passed |

The last line ran under `uv run --no-project --with pytest --with typer ...`: this machine's own Python has a package
named `tests` in its site-packages, which shadows the suite's `tests` folder and stops collection with "No module named
'tests.test_factory_definition_ops'". The phase 0 email check's own tests: 22 passed.

## Findings

1. **A check can read a secret it did not declare, by asking cc-secrets itself.** The Director hands a check only what
   it declared (case 10), but the check runs as the user, and `cc-secrets run <any entry> -- ...` from inside it worked
   (`exit 0`, `set`). MISSION 5.5a says "a check cannot read a name it did not declare"; that holds for what the
   Director gives it, not for what the check can fetch. Either the sentence changes, or cc-secrets refuses to run
   inside a check. Raised with the Tech Lead.
2. **The publish's machine question timed out on the first publish.** Version 1's publish, about half a minute after
   the Director started, answered every secret question with "Whether <machine> holds 'cloudflare-account-id' could not
   be answered: cc-secrets on <machine> did not answer within 10 seconds. The check's run will check it." Version 3's
   publish, ten minutes later, answered properly: "<machine> does not hold the secret 'qa-p5b-no-such-secret'. Add it
   there with cc-secrets; the check's run will refuse without it." The earlier runs of the rig got proper answers on
   their first publish. So the 10-second budget can be missed while a Director is starting; the person sees a "could
   not be answered" notice and the run still binds.
3. **`schedule runs` and `schedule run` print Task `unknown`** for a run that started no session (`tquiet`'s
   `nothing-to-do` run), where nothing is unknown. Small; the Gateway's `taskStatus` decides it.
4. **The Open problems card's note speaks only of schedules**: "A problem stays here until the next run of the same
   schedule reports ok, ..." also heads the failed checks of interval starts, which are triggers and clear on the
   trigger's next check that does not fail. Wording only.
5. **Outside the mission: the email links to the machine's tailnet address** even though the rig Gateway runs with
   Tailscale off (`CC_GATEWAY_NO_TAILSCALE=1`). The transcript masks it as `<tailnet address>`.
6. **Before pull request 4048, an interval check that found work could not start its session**: the first run of the
   rig, on main before 4048, recorded "the session could not be started: director returned BadRequest: repoPath is
   required", and stored no state, so the next check counted the same mail again (as ruled). 4048 fixed it; case 2 is
   the proof.

## What this does NOT prove

- **The live mailbox.** The email check never read the real Outreach mailbox; the owner's app registration has not
  arrived. The three declared entries arrived (the check would have failed "not given" otherwise), but their values
  were sent only to the fake, which ignored them.
- **No Python, no `uv`, and a login-only secret** were not seen on a real machine; only through the Director's tests
  with fakes.
- **A check that declares packages** (run under `uv run --script`) was not run live; every rig check runs with plain
  Python.
- **Only one Director.** A claim held by one Director and a report from another refused (409), a claim that lapses
  after six minutes, a due check superseded ("check-not-run") when no Director runs it before its next time, and a
  Director that does not advertise `definitionChecks` are covered by the pull requests' tests, not here.
- **Clearing a problem.** No failed check was followed by one that succeeded, and nothing was resolved by a person, so
  a problem leaving the card was not seen. Nor was a state reset (`factory check-state reset`).
- **SQLite only.** The rig Gateway stores in SQLite; PostgreSQL's migrations are proven by the pull requests'
  database tests.
- **The hosted Gateway** and its other accounts were not involved; the email was composed, never sent.
- **The Cockpit.** The card is read through its Gateway route; nothing here shows how a screen draws it.
