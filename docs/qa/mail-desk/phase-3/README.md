# Mail Desk, phase 3: screening on the Gateway, in observe mode

Date: 18 and 19 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), the phase 3 row of
section 6, design 5.7, with phase 5 (backup machines, design 5.9) run under the same Tech Lead (its own README is
`docs/qa/mail-desk/phase-5/README.md`). Tech Lead: session 118 on devlinux. The contract every Developer built to is
`CONTRACT.md` in this folder, changed eight times as findings and rulings came in; each change is its own commit.

Everything here is behind the Mail Desk switch, off for every account. **This phase acts on no mode: it moves no
message and starts no session, even for a channel in live mode** (live mode is phase 4's). Nothing in this phase
touched the hosted Gateway, a real mailbox, any email, a desktop release or the website proxy.

## What was built

| Task | Pull request into the phase branch | What it is |
|---|---|---|
| A | 4229 | `checks/screen.py`: one mailbox failing does not stop the others (the Delivery Lead's ruling). A failing mailbox's section carries its failure sentence, its memory comes back byte for byte, the others are read. Context version 2. |
| B | 4230 | `MailDeskModel`: the model call. `devthrottle/wingman-fast`, temperature 0, two output tokens, log probabilities, the `mail-desk` tag with the account, the plan's gate, an answer held to the listed letters, confidence only from log probabilities, cost from the reply's token counts. |
| Phase 5 | 4232 | Backup machines for a definition start's check (see its own README). |
| - | 4236 | What the rebase onto phase 1 broke: a constructor argument in three tests, two migration tests, two migration designer files. |
| E | 4239 | `mail_desk_channel_checks`, the mailbox-failed and spend problems on the Open problems card, the signed-in label from those rows, the 08:00 email's Mail Desk section. |
| D | 4240 | `MailDeskScreening`, the four layers in order, the 25-route cap, the settings (route threshold 0.90, spam threshold 0.95, spend limit 5.00 dollars), their routes, their record, `maildesk settings` and `maildesk set`. |
| F | 4243 | The hand-off: every Mail Desk report goes to `MailDeskService`, never to a seat start; the `screened` outcome; the memory built per machine and merged back by mailbox; decision rows with the rules version and attempt count; the retry of one mailbox only; the spend stop; the template's draft offer at switch-on and at Gateway start. |

Every Developer's code was reviewed by a separate Reviewer session of another agent family before it merged into the
phase branch, in the order repository rule 5a sets. Every finding was answered by
the Developer who built the work, accepted or declined with a reason, in `reviews/`. Of 15 findings, 15 were accepted
and fixed; none is left unanswered.

## The proof, in plain words

### A Mail Desk report can never start a seat (the hard order)

`TriggerService` sends every report of the Mail Desk start (`MailDeskStart.Is`: the `mail-desk` factory and
`checks/screen.py`) to `MailDeskService` before it looks at the count. Tests in `MailDeskHandOffTests` report counts of
0 and 5, with the switch on and off, with no service wired, with a channel in live mode, while a screening is already
running, and from a paused start, and in every case the fake session starter is never called. With no service wired
the report is refused 409 `mail_desk_not_ready` and nothing is recorded. **A hole the task F Developer found and
closed:** an owner editing the draft from `every:` to `at:` would have sent the check through the timed path, where a
count starts the seat; `DueCheckService` now refuses that report the same way.

### The four layers decide in order, and the model only picks a label

`MailDeskScreeningTests` give each layer an email it alone decides, then emails that match several layers (a rule
beats the allow-list beats the block list), a known sender skipping the person-or-spam question, every threshold just
under and exactly at its value, an answer outside the labels, no confidence returned, no routes defined, and the spend
limit reached. The model's answer becomes a label only when it is exactly one listed letter; anything else goes to the
Mail Desk agent, never to a route or to Junk.

### A model failure is retried, never guessed, and touches only its own mailbox

When the model cannot be asked about an email (no key, an error, a timeout, no token count), that email's decision is
`failed` and the Gateway writes back that mailbox's cursor, keys and Sent-folder cache exactly as they were, so only
that mailbox reads the same mail again; every other mailbox moves on. A reread asks nothing and charges nothing for an
email already decided. The third failure keeps the email for the owner and lets its mailbox move on, so no email can
block a mailbox forever. The tests count the fake model's calls.

### One mailbox failing does not stop the others, and it shows as its own problem

`screen.py`'s tests (292, Python) prove the check side: each kind of per-mailbox failure in its own section, the
failing mailbox's memory byte for byte, the others read, the whole-run failures still whole-run, and the failure
sections never crowding out new mail. On the Gateway, each failing mailbox becomes one problem on the `mail-desk`
factory's Open problems card and in the 08:00 email, naming the mailbox, the machine and the fix, and cleared by that
mailbox's next good check (`MailDeskProblemsTests`). With two mailboxes, one failing, only the failing one has a
problem. No test has a SECOND mailbox's good check arrive while the first is still failing; the problem is kept per
mailbox in the code and the 4239 Reviewer looked for that case, but it is read, not run.

### No body text is kept

A test screens an email whose preview holds a marker text and searches every Mail Desk table and the log for it: none.

### The spend limit holds

No model call starts at or over the limit (the fake model's call log), the stop is recorded, and the settings route and
the spend problem read one fold, so they can never disagree.

## How to run the proof again

```
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "Category=MailDesk|FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~DueCheck"
uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v
pwsh -NoProfile -File scripts/test-database.ps1     # needs Docker; on Linux it calls powershell by name, see below
```

## The gate

Run by the Tech Lead on devlinux on the phase branch at `1c1bbf7e3` (rebased on origin/main `23716e3f5`), tree clean:
`scripts/test-local.ps1 -Parked`, with .NET 10, `DOTNET_USE_POLLING_FILE_WATCHER=1`, `EnableWindowsTargeting=true` and
`LOCALAPPDATA` set. Run folder `/tmp/cc-test-local-f3410ade`. inotify instances in use before it: 114 of 128.

**The script's own verdict is NEVER RAN, and it always is on this machine:** the installer suite
`cc-director-setup.Tests` builds for Windows and runs no test here. So every red suite was compared, test by test,
with unchanged origin/main on the same machine.

| Suite | Phase branch | Unchanged origin/main | Verdict |
|---|---|---|---|
| Gateway.UnitTests | 15,121: 3 failed, 2 skipped | the same 3 (`VoiceUnheardCountTests`, `VoiceAutoOffLineTests`, `HeldDeliveryDriverTests`, Linux file-permission behaviour) | clean |
| Gateway.Tests, 4 shards | 10 failed (`FleetSpawnOriginTests` 4, `FleetSpawnMissionAttachTests` 2, `WorkflowSeatTests` 2, `HostedProcessControlDenyTests` 1, `ContextLessRouteCensusTests` 1) | the same 10 (run on `8b6e31fb4`) | clean |
| Core.Tests | 8 failed (`LinkDetectorExactTextTests`) | the same 8 (`23716e3f5`) | clean |
| Core.UnitTests | 6 failed | the same 6 (`23716e3f5`); for `CatchLogLineScanTests`, which scans the whole repository, the list of offending lines was compared too and is identical, none in Mail Desk code | clean |
| Launcher.Tests | 24 failed | the same 24 | clean (Windows-only behaviour) |
| Reclaim.Tests | 56 failed | the same 56 | clean (Windows-only behaviour) |
| Terminal.Avalonia.Tests | 1 failed | the same 1 | clean |
| cc-director-setup.Tests, cc-director-setup-engine.Tests | no test run / host crashed | the same | clean (they build for Windows or crash on a Linux path) |
| Avalonia, Engine, HostedAgent, DeliveryQualification, cc-director-setup-cli, Gateway.Tests shard 1 | passed | - | clean |

**Not in the gate, run separately on the same commit:**

- `screen.py`'s tests (Python): **292 passed**.
- The `cc-devthrottle` tool's tests: **5,034 passed, 2 skipped**.
- `scripts/test-database.ps1` (the new migrations `AddMailDeskChannelChecks` and `AddMailDeskDecisionAttempts`, and
  phase 5's `AddTriggerBackupMachines`): **82 run, 81 passed, 1 failed, none skipped.** The failure is
  `AddLicenseAlarmsPostgresTests`, and it fails the same way on unchanged origin/main `23716e3f5` (79 run, 78 passed):
  main's #4252 retired the alarm kind that test still raises. Not this phase's; it should be fixed on main.

**The rebases.** The phase branch was rebased onto origin/main twice. The first broke the build (pull request 4236).
The second showed that the phase's newest migration had been generated before main's newest, so the check that the
newest migration's designer carries the whole model failed on both databases; the decision-attempts migration was
generated again after main's newest, its Up and Down byte for byte the same as before, and that check now passes.

**This gate does not cover** Windows or macOS, a real Director, or a real mailbox.


## What is NOT proven

- **The real model.** `devthrottle/wingman-fast` was never called in this phase; every model answer here is a fake's.
  Whether DeepInfra returns log probabilities for it is still unknown. **If it does not, every model decision has no
  confidence and goes to the Mail Desk agent.** That is safe in observe mode, but it would make the model layers
  useless until fixed. Phase 6 sees this on the first real screening.
- **Strict answers.** `A.` or `a` counts as outside the labels. How often the real model answers that way is phase 6's
  measurement.
- **The real mailbox, a real Director, a real check run.** The check's reports here are written by tests. Phase 6 runs
  the real thing.
- **A failed call's cost** is recorded as none; a call that timed out may still have been billed, so the counted spend
  can be lower than the real spend by about $0.0001 per such call.
- **After a Gateway restart** the time a memory was handed out is lost; the time the report arrived stands in, which
  can refuse one honest report once (a mailbox added between the hand-out and the restart). The next run is right.
- **Live mode** - filing, starting seats, routed emails - is phase 4's. A live channel's decisions are recorded with
  mode live and nothing is done.
- **Windows and macOS.** Built and tested on Linux only.

## For the Delivery Lead

1. **`scripts/test-database.ps1` calls `powershell` by name**, which does not exist on Linux; every run here used a
   two-line script on the `PATH`, outside the repository, that runs `pwsh`. A small defect in the script, worth an issue.
2. **The template's channel machines.** A channel's `machines` list now decides which machine is handed that mailbox,
   so for the backup to work the owner's channels must name both machines
   (`maildesk channel add <address> ... --machine devlinux --machine SOREN_NORTH`), and the factory's start must name
   SOREN_NORTH in `backupMachines`.
3. **uv on the live Director's PATH on devlinux** is still phase 6's fix (phase 0 finding 2).
4. **Waiting-message notices were often stuck unsent** (issue 3930): several reports reached me only because I or the
   Delivery Lead looked.
