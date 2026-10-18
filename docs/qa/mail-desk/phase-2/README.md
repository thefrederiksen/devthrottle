# Mail Desk, phase 2: the screening check and the built-in Mail Desk factory

Date: 18 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), the phase 2 row of
section 6, design 5.3, 5.4 (with its phase 0 findings) and 5.5. Tech Lead: session 113 on devlinux. Everything here is
behind the Mail Desk switch, which is off for every account, and nothing runs before an owner publishes the factory.

Nothing in this phase touched the hosted Gateway, the real mailbox, any email, a desktop release or the website proxy.

## What was built

| Piece | Where | Built by | Reviewed by |
|---|---|---|---|
| The screening check | `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py` | Developer "screen.py check", then Developer "screen.py review fixes" (the first session stalled; its work was pushed and taken over) | a separate Reviewer session: review and re-review in `reviews/screen-review.md`, answers in `reviews/screen-answers.md` |
| The check's contract for phase 3 | `CONTRACT.md` (this folder): the memory, the output, every failure sentence, the reason window, the limits | the same | the same |
| The check's tests | `tests/` (this folder), 210 tests | the same | the same |
| The built-in template | `src/CcDirector.Gateway/MailDesk/Template/` (`factory.yaml`, `seats/mail-desk.yaml`), compiled into the Gateway | Developer "template factory and draft offer" | a separate Reviewer session: `reviews/template-review.md`, answers in `reviews/template-answers.md` |
| The template's version and its offer as a draft | `src/CcDirector.Gateway/MailDesk/MailDeskTemplate.cs`, `MailDeskTemplateOffer.cs`; a shared `CheckAndSave` in `FactoryDefinitionEndpoints`; a `gateway` author kind for drafts only in `FactoryDefinitionStore` | the same | the same |

Every review finding was answered by the Developer who built the work, accepted or declined with a reason, in the
answers files. Nothing a reviewer raised is left unanswered.

## The proof, in plain words

### The check never sends mail

Phase 0 found that the saved cc-outlook sign-in gives a token that CAN send mail. So the promise rests on one function
in `screen.py` through which every Microsoft Graph request goes. It lets out only a read (GET to graph.microsoft.com
under `/v1.0/`) and `POST /v1.0/me/messages/{id}/move` whose body is exactly `{"destinationId": "archive"}` or
`{"destinationId": "junkemail"}`. `tests/test_request_guard.py` sends every other shape we and the reviewer could
think of - other methods, sendMail, reply, forward, other hosts, ports, user information, other paths, `..`, encoded
slashes, control characters, a move body with another destination or an extra key - and for each one asserts two
things: it was refused, and the fake Graph's record of requests is empty, so nothing reached the network. The reviewer
also checked that the transport is called from one place only, after the guard, and that no redirect can turn a request
into a write.

### Observe mode moves nothing

`tests/test_filing.py` gives a channel in observe mode a non-empty filing queue and asserts: no request at all, the
queue left exactly as it was, and the report saying the queue was not carried out because the channel is in observe
mode. Only the exact mode `live` files, and only into Archive or Junk.

### Batches fit, and nothing is skipped or reported twice

`tests/test_batches_and_cursor.py` holds the context to 7,168 bytes of UTF-8 and 10 messages a run (phase 0's
measurement), catches up a backlog over several runs and checks every message is reported exactly once, cuts a batch
between messages that share one receive time, and checks the cursor never moves past what was reported. **Measured:
10 messages a run at both of phase 0's sizes (446 and 680 bytes); 7 a run at 900 bytes.** The mission's "roughly 20 a
run" does not hold, because the check reports a 16-character key instead of the 152-character Graph id and the
10-message stop binds first (`CONTRACT.md` section 2).

### A failed sign-in is a failed check that names the fix - as the owner will see it

The Gateway keeps only the first 200 characters of a failed check's standard error, flattened onto one line, after uv's
own output (`TriggerCheckContract.Read` on origin/main). The first review found the fix never reached that window. Now
every failure writes its one sentence first, every sentence is at most 165 characters, and
`tests/test_what_the_gateway_keeps.py` applies the Gateway's own rule to 48 cases (12 kinds of failure, one or two
mailboxes, observe or live mode) and asserts the whole sentence is inside what the Gateway keeps, with no traceback and
the memory unchanged. Example of what the Gateway keeps: `exit code 1: email@devthrottle.com is not signed in on
devlinux: run cc-outlook -a devthrottle auth`. `tests/test_no_traceback.py` proves no failure, including the Microsoft
sign-in service being out of reach, reaches the Gateway as a traceback.

The sign-in is found only under the Director's per-user base folder at `instances/*/config/outlook/tokens/`, with the
home folder from the operating system's account record (phase 0's rule), the file named by cc-outlook's own rule taken
from its source. None found and more than one found are each a failed check naming the fix; the renewed sign-in is
written back to the same file with owner-only permissions and never into the memory.

### More than one mailbox, and not only Outlook (D19, D20)

The check reads its list of mailboxes from its memory, which the Gateway writes; no address is written into the code.
Everything specific to Outlook (MSAL, cc-outlook's file rule, Graph) is in one reader chosen by the mailbox's
provider, so Gmail is a new reader. A provider with no reader is a failed check naming it. Tests run two mailboxes in
one run and check each message names its mailbox.

### The template is a real factory definition, offered and never published

`MailDeskTemplateTests` load the embedded files and send them through the Gateway's own checked import and create
paths - the same code a person's import uses - and they pass with no errors. The template uses exactly the names phase
1's Gateway recognises a Mail Desk report by: factory `mail-desk`, check `checks/screen.py`, seat `mail-desk`.
`MailDeskTemplateOfferTests` prove the offer: no factory gives a first draft; the same template version is never
offered twice, whoever stored it; a newer template is offered as a new draft above the owner's edits, never over them;
two offers at once leave one draft and log no defect; the switch off offers nothing; and it can never publish (the
store refuses the `gateway` author kind on publish and restore).

## How to run the proof again

```
uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v
cd src/CcDirector.Gateway.UnitTests && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test --filter Category=MailDesk
```

Run by the Tech Lead on the phase branch: **210 passed** (Python, 7 seconds) and **26 of 26 passed** (Mail Desk C#).
The Python tests are not in `scripts/test-local.ps1` (it runs no Python), so they are run by this command and must be
run by hand by anyone changing `screen.py`.

## The gate

Run by the Tech Lead on devlinux, on the phase branch at `827e56e1a` (rebased on origin/main `839e0fb5d`):
`scripts/test-local.ps1 -Parked`, with .NET 10, `DOTNET_USE_POLLING_FILE_WATCHER=1`, `EnableWindowsTargeting=true`
and `LOCALAPPDATA` set (the script needs both on Linux). Run folder `/tmp/cc-test-local-a00c734c`.

**The script's own verdict is NEVER RAN (exit 1), and it always is on this machine:** the Windows-only suites cannot
pass on Linux, and the two installer suites build for Windows and crash. So every red suite was compared, test by
test, with the same suite on unchanged origin/main `839e0fb5d`, built and run on the same machine the same hour.
`-RerunFailed` refuses a run whose verdict is NEVER RAN, so the failed tests were re-run by hand with `dotnet test
--filter`.

| Suite | Phase branch | Unchanged origin/main | Verdict |
|---|---|---|---|
| Gateway.UnitTests (holds the Mail Desk tests) | 14,450 passed, 3 failed | the same 3 (listed below) | clean |
| Gateway.Tests (4 shards) | 354 failed in the run; 346 of them "inotify instances (128) reached" | 11 failed, 2 of them inotify | clean after re-runs: every failure that was not inotify also fails on main; the 87 still blocked by inotify after the first re-run ran on second and third re-runs and all 87 passed |
| Core.Tests | 9 failed | 8 failed | clean: the one extra failed on inotify and passes 3 times of 3 alone |
| Core.UnitTests | 5 failed | the same 5 | clean |
| Launcher.Tests | 24 failed | the same 24 | clean (Windows-only behaviour) |
| Reclaim.Tests | 56 failed | the same 56 | clean (Windows-only behaviour) |
| Terminal.Avalonia.Tests | 1 failed | the same 1 | clean |
| cc-director-setup.Tests, cc-director-setup-engine.Tests | crashed | crashed / 21 failed | clean (they build for Windows) |
| Avalonia, Engine, HostedAgent, DeliveryQualification, cc-director-setup-cli | passed | - | clean |

The three Gateway.UnitTests that fail on main on Linux: `VoiceUnheardCountTests.NotePlayed_AResetWhoseSaveFailed_IsWrittenAgainByTheNextPlay`,
`VoiceAutoOffLineTests.ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt`,
`HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`.
The 11 Gateway.Tests that fail on main here are in session spawning (`FleetSpawnOriginTests`,
`FleetSpawnMissionAttachTests`), `HostedProcessControlDenyTests.Hosted_director_launch_is_refused`,
`TestStorageRootRedirectTests`, `ContextLessRouteCensusTests` and two others; none is in code this phase touched.

**Why so much inotify noise:** this machine's 128 inotify instances per user were almost all held by other running
programs (127 counted during the run), so any Gateway host started by a test could fail to start. The polling setting
does not cover every file watcher. That is the machine, not the code; raising the limit
(`sudo sysctl fs.inotify.max_user_instances=1024`) is the owner's step and was not taken.

**No PostgreSQL migration** is in this phase (the template offer uses the existing factory definition tables), so
`scripts/test-database.ps1` does not apply.

**This gate does not cover** the Python tests of `screen.py` (the script runs no Python; run above by hand, 210
passed), Windows, or macOS.

## What is NOT proven

- **No run against the real mailbox or a real token.** The check was never run by a Director against
  email@devthrottle.com in this phase; phase 0 proved the sign-in premise that way, and phase 6 runs the real thing.
  That Graph answers 404 for a moved message's old id (the premise behind "already gone") is the fake's behaviour.
- **Windows and macOS.** Built and tested on Linux only. The template's line endings are pinned by `.gitattributes`
  so its version is the same on every machine, but no Windows build was run.
- **The offer is not connected to anything yet.** Phase 1's switch (`MailDeskSwitch`, `POST /gateway/admin/mail-desk`)
  is not on origin/main. What remains is two calls: build the offer in the Gateway's start-up, call `offer.Offer(tenant)`
  after the switch is turned on, and - decided by the Tech Lead - also at Gateway start for every account whose switch
  is on, so a newer template after a deploy is offered. Until then `MailDeskTemplateOffer` has no production caller.
- **The seat's prompt in live mode.** The template's prompt passes `{context}` through and its brief names the
  check's fields; what the Gateway puts in a routed seat's prompt is phase 4's to define, and the brief will follow it.
- **The HTTP pipeline** of the import path: the tests call the route handlers directly, as the existing definition
  tests do.

## For the Delivery Lead to decide or carry forward

1. **One mailbox failing stops every mailbox.** One check has one outcome and one memory, so when the owner's second
   mailbox loses its sign-in, email@devthrottle.com is not screened either until it is fixed. Kept deliberately so a
   failed sign-in stays a failed check on the Open problems card. Failing per mailbox would change phase 1's and phase
   3's contracts. Decide before phase 6 runs two mailboxes.
2. **About three mailboxes fit** in the check's 64 KB memory at its caps (15,131 bytes each, measured). Two are needed.
3. **No command routes one email by hand.** The Mail Desk agent's brief tells it to teach the lists and then close the
   email with `maildesk done` or `maildesk return` (phase 4's commands). If a hand-routing command is wanted, it is
   phase 4's.
4. **A message that enters the Inbox with a receive time older than the cursor** (moved back from Junk, delivered late)
   is never reported. That is the receive-time cursor the mission chose (`CONTRACT.md` 3.5); phase 3 should know it.
5. **uv is missing from the live Director's PATH on devlinux** (phase 0 finding 2). Not fixed here, as instructed; the
   check declares MSAL as a package, so it fails there until phase 6 fixes the machine.
6. **The template's default machine is `devlinux`**, a literal the definition format requires; another account gets a
   notice, not an error, and changes it before publishing.
7. **A Developer session stalled** because the line telling it a message was waiting was typed into its prompt box
   and never submitted ("the composer holds text"), and `session prompt` could not submit it either. Its work was
   pushed, so a fresh Developer took over. This is a product defect in message delivery, worth an issue.
